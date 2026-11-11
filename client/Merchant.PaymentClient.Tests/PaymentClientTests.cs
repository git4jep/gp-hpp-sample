using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Merchant.Api;
using Merchant.PaymentClient;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Merchant.PaymentClient.Tests;

public sealed class PaymentClientTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"blazor-payment-tests-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> apiFactory;
    private readonly WebApplicationFactory<ClientProgram> clientFactory;
    private readonly MerchantApiClient api;
    private readonly HttpClient browser;

    public PaymentClientTests()
    {
        var settings = new AppSettings
        {
            Mode = "demo",
            PublicBaseUrl = "https://client.example",
            DatabasePath = Path.Combine(directory, "orders.sqlite")
        };
        apiFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppSettings>();
                services.AddSingleton(settings);
                services.RemoveAll<OrderStore>();
                services.AddSingleton(new OrderStore(settings));
            }));
        api = new MerchantApiClient(apiFactory.CreateClient());
        clientFactory = new WebApplicationFactory<ClientProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<MerchantApiClient>();
                services.AddTransient(_ => new MerchantApiClient(apiFactory.CreateClient()));
            }));
        browser = clientFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(settings.PublicBaseUrl),
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task PaymentPageLoadsApiCatalogAndStyles()
    {
        var html = await browser.GetStringAsync("/");
        Assert.Contains("Test a payment.", html);
        Assert.Contains("The Weekender", html);
        Assert.Contains("Continue to demo checkout", html);
        Assert.Contains("USD 89.00", html);
        var cssPath = Regex.Match(html, "href=\"([^\"]+\\.css)\"").Groups[1].Value;
        var css = await browser.GetAsync(cssPath);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("success", "success", "Payment completed.")]
    [InlineData("declined", "declined", "Payment declined.")]
    [InlineData("cancel", "cancelled", "Payment cancelled.")]
    public async Task DemoRoundTripReachesTheCorrectBlazorResult(string outcome, string page, string heading)
    {
        var checkout = await api.Checkout("shopper@example.com", "tumbler", 2);
        Assert.StartsWith("https://client.example/demo/checkout/", checkout.HppUrl);
        var demo = await browser.GetStringAsync(new Uri(checkout.HppUrl).PathAndQuery);
        Assert.Contains("Choose a test outcome.", demo);
        Assert.Contains("USD 56.00", demo);
        var completed = await api.CompleteDemo(checkout.Order.Id, checkout.OrderKey, outcome);
        if (outcome == "cancel")
            Assert.StartsWith($"https://client.example/api/payments/cancel/{checkout.Order.Id}?state=", completed.RedirectUrl);
        using var callback = await browser.GetAsync(new Uri(completed.RedirectUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("script-src 'nonce-", callback.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", callback.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
        var html = await callback.Content.ReadAsStringAsync();
        Assert.Contains("window.top.location.replace", html);
        var destination = $"https://client.example/payment/{page}?order={checkout.Order.Id}&key={checkout.OrderKey}";
        Assert.Contains(WebUtility.HtmlEncode(destination), html);
        var result = await browser.GetStringAsync(new Uri(destination).PathAndQuery);
        Assert.Contains(heading, result);
        Assert.Contains("USD 56.00", result);
        Assert.Contains(checkout.Order.Id, result);
    }

    [Theory]
    [InlineData("/payment/success")]
    [InlineData("/payment/cancelled")]
    [InlineData("/payment/return")]
    [InlineData("/payment/cancel")]
    public async Task ResultRouteNeverOverridesApiStatus(string route)
    {
        var checkout = await api.Checkout("shopper@example.com", "notebook", 1);
        var html = await browser.GetStringAsync($"{route}?order={checkout.Order.Id}&key={checkout.OrderKey}");
        Assert.Contains("Payment status pending.", html);
        Assert.DoesNotContain("Payment completed.", html);
        Assert.DoesNotContain("Payment cancelled.", html);
    }

    [Theory]
    [InlineData("/payment/return")]
    [InlineData("/payment/cancel")]
    public async Task MissingOrderLinkHasAnActionableError(string route)
    {
        var html = await browser.GetStringAsync(route);
        Assert.Contains("complete order link", html);
        Assert.DoesNotContain("Payment completed.", html);
    }

    [Fact]
    public async Task FormReturnAndJsonNotificationAreForwardedButCannotForgePayment()
    {
        using var backend = apiFactory.CreateClient();
        var response = await backend.PostAsJsonAsync("/api/checkout", new
        {
            email = "shopper@example.com",
            items = new[] { new { productId = "tumbler", quantity = 1 } }
        });
        var checkout = await response.Content.ReadFromJsonAsync<JsonElement>();
        var callbacks = checkout.GetProperty("callbacks");
        using var returned = await browser.PostAsync(
            new Uri(callbacks.GetProperty("return_url").GetString()!).PathAndQuery,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["hpp_response"] = "{\"id\":\"TRN_forged\",\"status\":\"CAPTURED\"}"
            }));
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Contains("/payment/pending?", await returned.Content.ReadAsStringAsync());
        using var notified = await browser.PostAsJsonAsync(
            new Uri(callbacks.GetProperty("status_url").GetString()!).PathAndQuery,
            new { id = "TRN_forged", status = "CAPTURED" });
        Assert.Equal(HttpStatusCode.OK, notified.StatusCode);
        var order = await api.GetOrder(checkout.GetProperty("order").GetProperty("id").GetString()!,
            checkout.GetProperty("orderKey").GetString()!);
        Assert.Equal("PENDING", order.Status);
    }

    [Fact]
    public async Task InvalidCallbackStateIsRejectedAndAdminApiIsNotExposed()
    {
        var checkout = await api.Checkout("shopper@example.com", "tumbler", 1);
        using var callback = await browser.GetAsync($"/payments/return/{checkout.Order.Id}?state=wrong");
        Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
        using var cancelled = await browser.GetAsync($"/api/payments/cancel/{checkout.Order.Id}?state=wrong");
        Assert.Equal(HttpStatusCode.NotFound, cancelled.StatusCode);
        Assert.Equal("PENDING", (await api.GetOrder(checkout.Order.Id, checkout.OrderKey)).Status);
        using var admin = await browser.PostAsync("/api/admin/access-token", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, admin.StatusCode);
    }

    [Fact]
    public async Task LegacyCancellationLinksStillReachTheCancelPage()
    {
        var checkout = await api.Checkout("shopper@example.com", "tumbler", 1);
        var cancelled = await api.CompleteDemo(checkout.Order.Id, checkout.OrderKey, "cancel");
        var legacyPath = new Uri(cancelled.RedirectUrl).PathAndQuery.Replace(
            "/api/payments/cancel/", "/payments/cancel/");
        using var callback = await browser.GetAsync(legacyPath);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("/payment/cancelled?", await callback.Content.ReadAsStringAsync());
        Assert.Equal("CANCELLED", (await api.GetOrder(checkout.Order.Id, checkout.OrderKey)).Status);
    }

    [Fact]
    public async Task LatePostCancellationStillDisplaysVerifiedPayment()
    {
        var checkout = await api.Checkout("shopper@example.com", "tumbler", 1);
        await api.CompleteDemo(checkout.Order.Id, checkout.OrderKey, "success");
        var cancelled = await api.CompleteDemo(checkout.Order.Id, checkout.OrderKey, "cancel");
        using var callback = await browser.PostAsync(new Uri(cancelled.RedirectUrl).PathAndQuery,
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Contains("/payment/success?", await callback.Content.ReadAsStringAsync());
        var html = await browser.GetStringAsync(
            $"/payment/cancelled?order={checkout.Order.Id}&key={checkout.OrderKey}");
        Assert.Contains("Payment completed.", html);
        Assert.DoesNotContain("Payment cancelled.", html);
    }

    [Fact]
    public async Task ReadinessChecksTheApiAndDatabase()
    {
        using var response = await browser.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task UnreachableApiReturnsBadGatewayWithoutLeakingDetails()
    {
        using var unavailableFactory = clientFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<MerchantApiClient>();
                services.AddTransient(_ => new MerchantApiClient(new HttpClient(new UnavailableHandler())
                {
                    BaseAddress = new Uri("http://api.internal")
                }));
            }));
        using var unavailable = unavailableFactory.CreateClient();
        var response = await unavailable.PostAsJsonAsync("/api/payments/notifications/order?state=private",
            new { status = "CAPTURED" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private", body);
        Assert.DoesNotContain("api.internal", body);
        using var readiness = await unavailable.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.False((await readiness.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ready").GetBoolean());
    }

    public void Dispose()
    {
        browser.Dispose();
        clientFactory.Dispose();
        apiFactory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Internal connection details");
    }
}
