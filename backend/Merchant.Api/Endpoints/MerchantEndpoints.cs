namespace Merchant.Api.Endpoints;

public static class MerchantEndpoints
{
    public static IEndpointRouteBuilder MapMerchantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapConfigEndpoints();
        endpoints.MapAdminEndpoints();
        endpoints.MapCheckoutEndpoints();
        endpoints.MapOrderEndpoints();
        endpoints.MapDemoEndpoints();
        endpoints.MapPaymentCallbackEndpoints();
        endpoints.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "API route not found." }));
        endpoints.MapFallback("/payments/{**path}", () => Results.NotFound(new { error = "Callback route not found." }));
        return endpoints;
    }
}
