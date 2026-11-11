using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Merchant.Api.Endpoints;

/// <summary>
/// Exposes and handles HTTP endpoints for Global Payments Hosted Payment Page (HPP) callbacks,
/// including customer browser redirects (return and cancel) and asynchronous server-to-server webhook notifications.
/// </summary>
public static class PaymentCallbackEndpoints
{
    /// <summary>
    /// Maps the payment callback routes to the endpoint routing builder.
    /// </summary>
    /// <param name="endpoints">The route builder to attach callback endpoints to.</param>
    /// <returns>The route builder with callback endpoints registered.</returns>
    public static IEndpointRouteBuilder MapPaymentCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Customer browser returns after completed or submitted payment flow
        endpoints.MapMethods("/payments/return/{id}", ["GET", "POST"], HandleReturn);

        // Customer browser redirects when canceling checkout on the payment page
        endpoints.MapMethods("/api/payments/cancel/{id}", ["GET", "POST"], HandleCancel);
        // Retain the original callback route for already-issued HPP links.
        endpoints.MapMethods("/payments/cancel/{id}", ["GET", "POST"], HandleCancel);

        // Asynchronous server-to-server webhook notifications from Global Payments
        endpoints.MapPost("/api/payments/notifications/{id}", HandleNotification);

        return endpoints;
    }

    /// <summary>
    /// Handles browser return callbacks when a customer finishes or submits the hosted payment form.
    /// </summary>
    private static Task<IResult> HandleReturn(
        string id, HttpContext ctx, OrderStore store, GpClient gp, AppSettings settings) =>
        HandleCallback(id, ctx, store, gp, settings, "return");

    /// <summary>
    /// Handles browser cancel callbacks when a customer cancels payment on the hosted payment page.
    /// </summary>
    private static Task<IResult> HandleCancel(
        string id, HttpContext ctx, OrderStore store, GpClient gp, AppSettings settings) =>
        HandleCallback(id, ctx, store, gp, settings, "cancel");

    /// <summary>
    /// Handles asynchronous server-to-server webhook notifications sent by Global Payments.
    /// </summary>
    private static Task<IResult> HandleNotification(
        string id, HttpContext ctx, OrderStore store, GpClient gp, AppSettings settings) =>
        HandleCallback(id, ctx, store, gp, settings, "notification");

    /// <summary>
    /// Extracts and parses the callback payload from either form-encoded data or a JSON request body.
    /// Supports unwrapping nested JSON strings present in known form field keys (e.g. response, hppResponse, hpp_response).
    /// </summary>
    /// <param name="request">The incoming HTTP request containing callback data.</param>
    /// <returns>The parsed callback payload as a <see cref="JsonElement"/>.</returns>
    /// <exception cref="ApiException">Thrown when the callback payload cannot be parsed as a JSON object.</exception>
    private static async Task<JsonElement> ReadCallback(HttpRequest request)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new { });
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var wrapped = new[] { "response", "hppResponse", "hpp_response" }.FirstOrDefault(form.ContainsKey);
            payload = wrapped is null
                ? JsonSerializer.SerializeToElement(form.ToDictionary(p => p.Key, p => p.Value.ToString()))
                : JsonSerializer.Deserialize<JsonElement>(form[wrapped].ToString());
        }
        else if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
            payload = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);

        if (payload.ValueKind != JsonValueKind.Object) throw new ApiException(400, "Invalid callback payload.");
        return payload;
    }

    /// <summary>
    /// Core processing pipeline for payment callbacks across return, cancel, and webhook notification events.
    /// Records event data, updates order state, reconciles status with the payment gateway, and either returns
    /// an acknowledgment JSON response or renders an HTML page that safely breaks out of the HPP iframe.
    /// </summary>
    /// <param name="id">The unique identifier of the order.</param>
    /// <param name="ctx">The current HTTP context.</param>
    /// <param name="store">The in-memory or persistent order data store.</param>
    /// <param name="gp">The Global Payments client for API reconciliation.</param>
    /// <param name="settings">Application settings containing environment mode and public base URLs.</param>
    /// <param name="kind">The callback event classification ("return", "cancel", or "notification").</param>
    /// <returns>A JSON result for notifications, or an HTML redirect page for browser return/cancel.</returns>
    /// <exception cref="ApiException">Thrown when the transaction identifier format is invalid or order is not found.</exception>
    private static async Task<IResult> HandleCallback(
        string id, HttpContext ctx, OrderStore store, GpClient gp, AppSettings settings, string kind)
    {
        // 1. Locate the order associated with the callback
        var order = EndpointGuards.FindOrder(ctx.Request, store, settings, id, true);
        var payload = await ReadCallback(ctx.Request);

        // 2. Extract transaction ID hint from payload or query string parameters
        var hint = GpClient.Text(payload, "id");
        if (hint == "") hint = GpClient.Text(payload, "TRN_ID");
        if (hint == "") hint = ctx.Request.Query["TRN_ID"].ToString();
        if (hint == "") hint = ctx.Request.Query["transaction_id"].ToString();
        if (hint != "" && !Regex.IsMatch(hint, "^TRN_[A-Za-z0-9_-]{1,180}$"))
            throw new ApiException(400, "Invalid transaction identifier.");

        // 3. Persist the callback event and mark cancellation if applicable
        store.Event(id, kind, payload);
        if (kind == "cancel") store.Cancel(id);

        // 4. In sandbox mode, reconcile order status directly with the gateway
        var verificationFailed = false;
        if (settings.Mode == "sandbox")
        {
            try { order = await gp.Reconcile(store, order, hint == "" ? null : hint); }
            catch (ApiException)
            {
                verificationFailed = true;
                store.Event(id, "verification-pending", JsonSerializer.SerializeToElement(new { }));
            }
        }

        // 5. Respond with HTTP 200 (or 503 retry status on reconciliation failure) for webhook notifications
        if (kind == "notification") return Results.Json(new { received = !verificationFailed }, statusCode: verificationFailed ? 503 : 200);

        // 6. For browser redirects, determine target customer-facing status page
        order = store.Get(id)!;
        var page = order.Status is "PAID" or "AUTHORIZED" ? "success" :
            kind == "cancel" ? "cancelled" : order.Status == "DECLINED" ? "declined" : "pending";
        var destination = $"{settings.PublicBaseUrl}/payment/{page}?order={id}&key={order.AccessKey}";

        // 7. Render breakout HTML with strict Content Security Policy to navigate parent frame out of the HPP iframe
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        ctx.Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; base-uri 'none'; frame-ancestors https://*.globalpay.com https://*.realexpayments.com";
        var target = JsonSerializer.Serialize(destination);
        return Results.Content($$"""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Returning to Northstar</title>
            <body><p>Returning to Northstar Supply…</p>
            <a href="{{WebUtility.HtmlEncode(destination)}}" target="_top">Continue to your order</a>
            <script nonce="{{nonce}}">try { window.top.location.replace({{target}}); } catch { window.location.replace({{target}}); }</script>
            </body></html>
            """, "text/html");
    }
}
