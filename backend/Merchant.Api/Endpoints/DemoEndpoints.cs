using System.Text.Json;

namespace Merchant.Api.Endpoints;

public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/demo/orders/{id}/complete", CompleteOrder);
        return endpoints;
    }

    private static IResult CompleteOrder(
        string id, DemoRequest body, HttpRequest request, OrderStore store, AppSettings settings)
    {
        EndpointGuards.RequireAllowedOrigin(request, settings);
        if (settings.Mode != "demo") throw new ApiException(404, "Not found.");
        var order = EndpointGuards.FindOrder(request, store, settings, id);
        if (body.Result is not ("success" or "declined" or "cancel"))
            throw new ApiException(400, "Invalid demonstration outcome.");
        if (body.Result != "cancel")
        {
            var status = body.Result == "success" ? "PAID" : "DECLINED";
            store.Apply(id, status, $"DEMO_{id}", $"DEMO_{status}", "{}");
            store.Event(id, "demo-simulator", JsonSerializer.SerializeToElement(new { status }));
        }
        var urls = PaymentRules.Urls(settings, order);
        return Results.Ok(new { redirectUrl = body.Result == "cancel" ? urls.CancelUrl : urls.ReturnUrl });
    }
}
