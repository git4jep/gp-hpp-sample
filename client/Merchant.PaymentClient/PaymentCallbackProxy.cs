namespace Merchant.PaymentClient;

public static class PaymentCallbackProxy
{
    public static void MapPaymentCallbacks(this WebApplication app)
    {
        // Only provider callback routes are exposed, not admin or arbitrary API routes.
        app.MapMethods("/payments/return/{id}", ["GET", "POST"], Forward).DisableAntiforgery();
        app.MapMethods("/payments/cancel/{id}", ["GET", "POST"], Forward).DisableAntiforgery();
        app.MapPost("/api/payments/notifications/{id}", Forward).DisableAntiforgery();
    }

    private static async Task Forward(HttpContext context, MerchantApiClient api)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            context.Request.Path + context.Request.QueryString);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        try
        {
            using var response = await api.Forward(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            foreach (var header in new[] { "Content-Security-Policy", "Referrer-Policy", "X-Content-Type-Options" })
                if (response.Headers.TryGetValues(header, out var values))
                    context.Response.Headers[header] = values.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception ex) when (!context.Response.HasStarted &&
            !context.RequestAborted.IsCancellationRequested &&
            ex is HttpRequestException or TaskCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(
                "The payment callback could not reach the merchant API. Please retry when the API is available.");
        }
    }
}
