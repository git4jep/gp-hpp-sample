using Merchant.PaymentClient.Components;

namespace Merchant.PaymentClient;

public class ClientProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
        // Receipt and callback URLs contain capabilities; never log HTTP request URLs.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        var apiUrl = builder.Configuration["MerchantApi:BaseUrl"] ?? "http://127.0.0.1:5080/";
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiUri) ||
            apiUri.Scheme is not ("http" or "https") || apiUri.UserInfo != "" ||
            apiUri.Query != "" || apiUri.Fragment != "" || apiUri.AbsolutePath != "/")
            throw new InvalidOperationException("MerchantApi:BaseUrl must be an HTTP(S) origin.");
        builder.Services.AddHttpClient<MerchantApiClient>(client =>
        {
            client.BaseAddress = apiUri;
            client.Timeout = TimeSpan.FromSeconds(60);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await next();
        });
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapPaymentCallbacks();
        app.MapGet("/health/ready", async (MerchantApiClient api, HttpContext context) =>
        {
            try
            {
                return await api.IsReady(context.RequestAborted)
                    ? Results.Json(new { ready = true })
                    : Results.Json(new { ready = false }, statusCode: 503);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { ready = false }, statusCode: 503);
            }
        });
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Run();
    }
}
