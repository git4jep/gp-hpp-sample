namespace Merchant.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/orders/{id}", GetOrder);
        endpoints.MapPost("/api/orders/{id}/reconcile", ReconcileOrder);
        return endpoints;
    }

    private static Order GetOrder(string id, HttpRequest request, OrderStore store, AppSettings settings) =>
        EndpointGuards.FindOrder(request, store, settings, id);

    private static async Task<Order> ReconcileOrder(
        string id, HttpRequest request, OrderStore store, GpClient gp, AppSettings settings)
    {
        EndpointGuards.RequireAllowedOrigin(request, settings);
        var order = EndpointGuards.FindOrder(request, store, settings, id);
        return settings.Mode == "demo" ? order : await gp.Reconcile(store, order);
    }
}
