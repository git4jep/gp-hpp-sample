namespace Merchant.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/api/admin/access-token", ["GET", "POST"], GetAccessToken);
        endpoints.MapGet("/api/admin/orders/{id}/events", GetOrderEvents);
        endpoints.MapPost("/api/admin/orders/{id}/reconcile", ReconcileOrder);
        return endpoints;
    }

    private static async Task<IResult> GetAccessToken(HttpRequest request, GpClient gp, AppSettings settings)
    {
        EndpointGuards.RequireAdmin(request, settings);
        if (settings.Mode != "sandbox") throw new ApiException(409, "Access tokens are only available in sandbox mode.");
        var token = await gp.GetToken(request.Method == "POST");
        return Results.Ok(new { accessToken = token.Token, expiresAt = token.ExpiresAt });
    }

    private static IResult GetOrderEvents(string id, HttpRequest request, OrderStore store, AppSettings settings)
    {
        EndpointGuards.RequireAdmin(request, settings);
        return Results.Ok(store.Events(id));
    }

    private static async Task<Order> ReconcileOrder(string id, HttpRequest request, OrderStore store, GpClient gp, AppSettings settings)
    {
        EndpointGuards.RequireAdmin(request, settings);
        var order = store.Get(id);
        if (order is null || order.Mode != settings.Mode) throw new ApiException(404, "Order not found.");
        return settings.Mode == "demo" ? order : await gp.Reconcile(store, order);
    }
}
