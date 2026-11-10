using Merchant.Api;
using Merchant.Api.Endpoints;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Cors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
// Avoid logging callback capability URLs or bearer credentials.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32768);
var settings = AppSettings.Load();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton(new GpClient(GpClient.CreateHttpClient(), settings));
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<AppSettings>((options, config) =>
    options.AddPolicy("MerchantOrigins", policy => policy
        .WithOrigins(config.AllowedOrigins)
        .WithMethods("GET", "POST")
        .WithHeaders("Content-Type")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{ctx.Connection.RemoteIpAddress}:{(ctx.Request.Path == "/api/checkout" ? "checkout" : "general")}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ctx.Request.Path == "/api/checkout" ? 10 : 180,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'";
    try { await next(ctx); }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        var code = ex switch
        {
            ApiException api => api.StatusCode,
            JsonException => 400,
            BadHttpRequestException bad => bad.StatusCode,
            _ => 500
        };
        ctx.Response.StatusCode = code;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = ex is ApiException ? ex.Message :
                code == 400 ? "Invalid request body." : "The request could not be completed."
        });
    }
});
app.UseRouting();
app.UseCors("MerchantOrigins");
app.UseRateLimiter();

app.MapMerchantEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html")))
    app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
