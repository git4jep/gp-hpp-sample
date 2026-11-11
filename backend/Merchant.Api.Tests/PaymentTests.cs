using Merchant.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Merchant.Api.Tests;

public sealed class PaymentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"northstar-tests-{Guid.NewGuid():N}");
    private AppSettings Settings(string mode = "sandbox") => new()
    {
        Mode = mode,
        DatabasePath = Path.Combine(directory, "orders.sqlite"),
        AppKey = "fake-test-key",
        PublicBaseUrl = "https://merchant.example",
        AdminKey = new string('a', 48)
    };
    private static Order NewOrder(OrderStore store, AppSettings settings) => store.Create([new("tumbler", 2)], settings);
    private static JsonElement Transaction(Order order, AppSettings s, string status = "CAPTURED",
        string? merchantId = null, string? amount = null, string? currency = null, string? reference = null, string? accountId = null) =>
        JsonSerializer.SerializeToElement(new
        {
            id = "TRN_verified",
            status,
            type = "SALE",
            merchant_id = merchantId ?? s.MerchantId,
            account_id = accountId ?? "TRA_test",
            account_name = s.AccountName,
            amount = amount ?? order.Amount.ToString(),
            currency = currency ?? order.Currency,
            reference = reference ?? order.Id
        });

    [Fact]
    public async Task LocalOriginsCanBeDisabledWithoutChangingThePublicOrigin()
    {
        var s = new AppSettings
        {
            PublicBaseUrl = "https://merchant.example",
            LocalDevelopmentOrigins = [],
            DatabasePath = Path.Combine(directory, "no-local.sqlite")
        };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppSettings>(); services.AddSingleton(s);
                services.RemoveAll<OrderStore>(); services.AddSingleton(new OrderStore(s));
            }));
        using var client = factory.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/checkout");
        preflight.Headers.Add("Origin", "http://localhost:4200");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var response = await client.SendAsync(preflight);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(s.IsAllowedOrigin("http://localhost:4200"));
        Assert.True(s.IsAllowedOrigin("https://merchant.example"));
    }
    [Theory]
    [InlineData("http://localhost:4200")]
    [InlineData("http://127.0.0.1:4200")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("https://merchant.example")]
    public async Task AllowedOriginsPassPreflightAndSandboxCheckoutKeepsPublicCallbacks(string origin)
    {
        var s = Settings();
        var store = new OrderStore(s);
        var fake = new FakeGp(s, NewOrder(store, s));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppSettings>(); services.AddSingleton(s);
                services.RemoveAll<OrderStore>(); services.AddSingleton(store);
                services.RemoveAll<GpClient>(); services.AddSingleton(new GpClient(new HttpClient(fake), s));
            }));
        using var client = factory.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/checkout");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal(origin, preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", preflightResponse.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Credentials"));
        using var checkout = new HttpRequestMessage(HttpMethod.Post, "/api/checkout");
        checkout.Headers.Add("Origin", origin);
        checkout.Content = JsonContent.Create(new
        {
            email = "shopper@example.com",
            items = new[] { new { productId = "tumbler", quantity = 1 } }
        });
        using var response = await client.SendAsync(checkout);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var name in new[] { "return_url", "cancel_url", "status_url" })
        {
            Assert.StartsWith(s.PublicBaseUrl + "/", result.GetProperty("callbacks").GetProperty(name).GetString());
            Assert.StartsWith(s.PublicBaseUrl + "/", fake.LastLink.GetProperty("notifications").GetProperty(name).GetString());
        }
    }
    [Theory]
    [InlineData("https://untrusted.example")]
    [InlineData("http://localhost:4201")]
    [InlineData("http://localhost:4200.evil.example")]
    [InlineData("null")]
    public async Task UnlistedOriginsCannotReadCorsResponsesOrCreateOrders(string origin)
    {
        var s = Settings("demo");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppSettings>(); services.AddSingleton(s);
                services.RemoveAll<OrderStore>(); services.AddSingleton(new OrderStore(s));
            }));
        using var client = factory.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/checkout");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var preflightResponse = await client.SendAsync(preflight);
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        client.DefaultRequestHeaders.Add("Origin", origin);
        using var response = await client.PostAsJsonAsync("/api/checkout", new
        {
            email = "shopper@example.com",
            items = new[] { new { productId = "tumbler", quantity = 1 } }
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Contains("Invalid request origin", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public void SecretIsSha512OfNonceAndKey()
    {
        var expected = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes("noncefake-key")));
        Assert.Equal(expected, GpClient.Secret("nonce", "fake-key"));
    }
    [Fact]
    public void PricesAreServerControlledAndBadQuantitiesRejected()
    {
        var s = Settings(); var store = new OrderStore(s);
        var order = NewOrder(store, s);
        Assert.Equal(5600, order.Amount);
        Assert.Throws<ApiException>(() => store.Create([new("tumbler", -1)], s));
        Assert.Throws<ApiException>(() => store.Create([new("unknown", 1)], s));
        Assert.NotEqual(order.AccessKey, order.CallbackKey);
    }
    [Theory]
    [InlineData("merchant")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("reference")]
    [InlineData("account")]
    public void VerificationRejectsMismatchedTransaction(string field)
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        var txn = Transaction(order, s, merchantId: field == "merchant" ? "MER_wrong" : null,
            amount: field == "amount" ? "1" : null, currency: field == "currency" ? "GBP" : null,
            reference: field == "reference" ? "other" : null, accountId: field == "account" ? "TRA_wrong" : null);
        Assert.False(GpClient.Matches(txn, order, s, "TRA_test"));
    }
    [Fact]
    public void CallbackUrlsUseDistinctRoutesAndRandomState()
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        var urls = PaymentRules.Urls(s, order);
        Assert.Contains($"/payments/return/{order.Id}?state={order.CallbackKey}", urls.ReturnUrl);
        Assert.Equal($"{s.PublicBaseUrl}/api/payments/cancel/{order.Id}?state={order.CallbackKey}", urls.CancelUrl);
        Assert.Contains("/api/payments/notifications/", urls.StatusUrl);
        Assert.DoesNotContain(order.AccessKey, urls.ReturnUrl);
    }
    [Fact]
    public async Task TokenAndHppRequestsMatchGpApiContract()
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        var fake = new FakeGp(s, order);
        var gp = new GpClient(new HttpClient(fake), s);
        var first = await gp.GetToken();
        var second = await gp.GetToken();
        Assert.Equal(first.Token, second.Token);
        Assert.Equal(1, fake.TokenCalls);
        var link = await gp.CreateLink(order, "payer@example.com", PaymentRules.Urls(s, order));
        Assert.StartsWith("LNK_", link.Id);
        var body = fake.LastLink;
        Assert.Equal("HOSTED_PAYMENT_PAGE", body.GetProperty("type").GetString());
        Assert.Equal("5600", body.GetProperty("order").GetProperty("amount").GetString());
        Assert.Equal(PaymentRules.Urls(s, order).CancelUrl,
            body.GetProperty("notifications").GetProperty("cancel_url").GetString());
        Assert.DoesNotContain(s.AppKey, body.GetRawText());
    }
    [Fact]
    public async Task LateCancellationOrDeclineCannotOverwriteVerifiedPayment()
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        var fake = new FakeGp(s, order);
        var gp = new GpClient(new HttpClient(fake), s);
        await gp.Reconcile(store, order, "TRN_verified");
        store.Cancel(order.Id);
        fake.TransactionStatus = "DECLINED";
        await gp.Reconcile(store, order, "TRN_verified");
        Assert.Equal("PAID", store.Get(order.Id)!.Status);
        Assert.Equal("CAPTURED", store.Get(order.Id)!.GpStatus);
        Assert.NotNull(store.Get(order.Id)!.CancelledAt);
    }
    [Fact]
    public async Task ApprovalAfterCancellationIsStillRecordedAndDuplicatesAreIdempotent()
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        store.Cancel(order.Id);
        var gp = new GpClient(new HttpClient(new FakeGp(s, order)), s);
        await gp.Reconcile(store, order);
        await gp.Reconcile(store, order);
        Assert.Equal("PAID", store.Get(order.Id)!.Status);
        Assert.Equal("TRN_verified", store.Get(order.Id)!.TransactionId);
    }
    [Fact]
    public async Task AuthorizationIsNotReportedAsCaptured()
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        var gp = new GpClient(new HttpClient(new FakeGp(s, order) { TransactionStatus = "PREAUTHORIZED" }), s);
        var result = await gp.Reconcile(store, order);
        Assert.Equal("AUTHORIZED", result.Status);
    }
    [Fact]
    public void RawCardAndCredentialDataAreNotPersistedOrSerialized()
    {
        var s = Settings(); var store = new OrderStore(s); var order = NewOrder(store, s);
        var payload = JsonSerializer.SerializeToElement(new
        {
            id = "TRN_test",
            status = "CAPTURED",
            token = "secret-token",
            card = "private-card",
            payer = new { email = "private@example.com" }
        });
        store.Event(order.Id, "return", payload);
        var serialized = JsonSerializer.Serialize(store.Events(order.Id));
        Assert.DoesNotContain("private", serialized);
        Assert.DoesNotContain("secret-token", serialized);
        var publicOrder = JsonSerializer.Serialize(order);
        Assert.DoesNotContain(order.AccessKey, publicOrder);
        Assert.DoesNotContain(order.CallbackKey, publicOrder);
    }
    [Fact]
    public async Task SandboxCallbacksUseProviderStatusAndNotificationsUpdateWithoutBrowser()
    {
        var s = Settings();
        var store = new OrderStore(s);
        var order = NewOrder(store, s);
        var fake = new FakeGp(s, order) { TransactionStatus = "DECLINED" };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppSettings>(); services.AddSingleton(s);
                services.RemoveAll<OrderStore>(); services.AddSingleton(store);
                services.RemoveAll<GpClient>(); services.AddSingleton(new GpClient(new HttpClient(fake), s));
            }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var urls = PaymentRules.Urls(s, order);
        var returnPath = new Uri(urls.ReturnUrl).PathAndQuery;
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/access-token");
        tokenRequest.Headers.Authorization = new("Bearer", s.AdminKey);
        using var tokenResponse = await client.SendAsync(tokenRequest);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        Assert.Equal("no-store", tokenResponse.Headers.CacheControl!.ToString());
        // The callback claims capture; the authenticated provider lookup reports decline.
        var result = await client.PostAsJsonAsync(returnPath, new { id = "TRN_verified", status = "CAPTURED" });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("/payment/declined?", await result.Content.ReadAsStringAsync());
        Assert.Equal("DECLINED", store.Get(order.Id)!.Status);
        fake.TransactionStatus = "CAPTURED";
        var notification = await client.PostAsJsonAsync(new Uri(urls.StatusUrl).PathAndQuery, new { id = "TRN_verified" });
        Assert.Equal(HttpStatusCode.OK, notification.StatusCode);
        Assert.Equal("PAID", store.Get(order.Id)!.Status);
        var demoAttempt = await client.PostAsJsonAsync($"/api/demo/orders/{order.Id}/complete?key={order.AccessKey}",
            new { result = "success" });
        Assert.Equal(HttpStatusCode.NotFound, demoAttempt.StatusCode);
    }
    [Fact]
    public async Task HttpDemoReturnsToDifferentMerchantPagesAndChecksCapabilities()
    {
        var s = Settings("demo");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppSettings>(); services.AddSingleton(s);
                services.RemoveAll<OrderStore>(); services.AddSingleton(new OrderStore(s));
            }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var created = await client.PostAsJsonAsync("/api/checkout", new
        {
            email = "payer@example.com",
            items = new[] { new { productId = "tumbler", quantity = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var checkout = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = checkout.GetProperty("order").GetProperty("id").GetString();
        var key = checkout.GetProperty("orderKey").GetString();
        var urls = checkout.GetProperty("callbacks");
        var bad = await client.GetAsync($"/api/orders/{id}?key=wrong");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
        var forbiddenToken = await client.GetAsync("/api/admin/access-token");
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenToken.StatusCode);
        var cancel = await client.GetAsync(new Uri(urls.GetProperty("cancel_url").GetString()!).PathAndQuery);
        Assert.Contains("/payment/cancelled?", await cancel.Content.ReadAsStringAsync());
        Assert.Contains("window.top.location.replace", await cancel.Content.ReadAsStringAsync());
        // A forged successful callback cannot mark an order paid, even in demo mode.
        var returnPath = new Uri(urls.GetProperty("return_url").GetString()!).PathAndQuery;
        await client.PostAsJsonAsync(returnPath, new { id = "TRN_forged", status = "CAPTURED" });
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/orders/{id}?key={key}");
        Assert.Equal("CANCELLED", read.GetProperty("status").GetString());
        await client.PostAsJsonAsync($"/api/demo/orders/{id}/complete?key={key}", new { result = "success" });
        var returned = await client.PostAsync(returnPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response"] = "{\"id\":\"TRN_verified\",\"status\":\"CAPTURED\"}"
        }));
        Assert.Contains("/payment/success?", await returned.Content.ReadAsStringAsync());
        var lateCancel = await client.GetAsync(new Uri(urls.GetProperty("cancel_url").GetString()!).PathAndQuery);
        Assert.Contains("/payment/success?", await lateCancel.Content.ReadAsStringAsync());
        var invalid = await client.PostAsync(returnPath, new StringContent("[]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FakeGp(AppSettings settings, Order order) : HttpMessageHandler
    {
        public int TokenCalls { get; private set; }
        public JsonElement LastLink { get; private set; }
        public string TransactionStatus { get; set; } = "CAPTURED";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body;
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("2021-03-22", request.Headers.GetValues("X-GP-Version").Single());
            if (path.EndsWith("/accesstoken"))
            {
                TokenCalls++;
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                Assert.Equal(GpClient.Secret(payload.GetProperty("nonce").GetString()!, settings.AppKey), payload.GetProperty("secret").GetString());
                Assert.DoesNotContain(settings.AppKey, payload.GetRawText());
                body = new
                {
                    token = "fake-access-token",
                    seconds_to_expire = "3600",
                    scope = new
                    {
                        merchant_id = settings.MerchantId,
                        accounts = new[]
                    {
                        new { id = "TRA_test", name = settings.AccountName, permissions = new[] { "LNK_POST_Create", "TRN_GET" } }
                    }
                    }
                };
            }
            else
            {
                Assert.Equal("fake-access-token", request.Headers.Authorization!.Parameter);
                if (path.EndsWith("/links"))
                {
                    LastLink = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    body = new { id = "LNK_test", url = "https://apis.sandbox.globalpay.com/ucp/hpp/redirect/test" };
                }
                else if (path.EndsWith("/transactions")) body = new { transactions = new[] { Transaction(order, settings, TransactionStatus) } };
                else body = Transaction(order, settings, TransactionStatus);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
