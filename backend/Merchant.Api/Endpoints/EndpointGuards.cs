namespace Merchant.Api.Endpoints;

internal static class EndpointGuards
{
    public static Order FindOrder(HttpRequest request, OrderStore store, AppSettings settings, string id, bool callback = false)
    {
        var order = store.Get(id);
        var supplied = request.Query[callback ? "state" : "key"].ToString();
        if (order is null || order.Mode != settings.Mode ||
            !PaymentRules.SecureEquals(supplied, callback ? order.CallbackKey : order.AccessKey))
            throw new ApiException(404, "Order not found.");
        return order;
    }

    public static void RequireAdmin(HttpRequest request, AppSettings settings)
    {
        if (settings.AdminKey.Length < 32 ||
            !PaymentRules.SecureEquals(request.Headers.Authorization.ToString(), $"Bearer {settings.AdminKey}"))
            throw new ApiException(401, "Backend administrator authentication required.");
    }

    public static void RequireAllowedOrigin(HttpRequest request, AppSettings settings)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin != "" && !settings.IsAllowedOrigin(origin))
            throw new ApiException(403, "Invalid request origin.");
    }
}
