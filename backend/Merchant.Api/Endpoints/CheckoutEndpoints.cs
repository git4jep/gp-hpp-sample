using System.Text.RegularExpressions;

namespace Merchant.Api.Endpoints;

public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/checkout", CreateCheckout);
        return endpoints;
    }

    private static async Task<IResult> CreateCheckout(
        CheckoutRequest body, HttpRequest request, OrderStore store, GpClient gp, AppSettings settings)
    {
        EndpointGuards.RequireAllowedOrigin(request, settings);
        if (body.Email is null || body.Email.Length > 100 || !Regex.IsMatch(body.Email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
            throw new ApiException(400, "Provide a valid email address.");
        var order = store.Create(body.Items, settings);
        var urls = PaymentRules.Urls(settings, order);
        try
        {
            var link = settings.Mode == "demo"
                ? new PaymentLink($"DEMO_{order.Id}", $"{settings.PublicBaseUrl}/demo/checkout/{order.Id}?key={order.AccessKey}")
                : await gp.CreateLink(order, body.Email, urls);
            store.Link(order.Id, link.Id, link.Url);
            return Results.Json(new { order, orderKey = order.AccessKey, hppUrl = link.Url, callbacks = urls }, statusCode: 201);
        }
        catch { store.LinkFailed(order.Id); throw; }
    }
}
