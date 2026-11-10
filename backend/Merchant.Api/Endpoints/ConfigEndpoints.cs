namespace Merchant.Api.Endpoints;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/config", GetConfig);
        return endpoints;
    }

    private static IResult GetConfig(AppSettings settings) => Results.Ok(new
    {
        merchant = "Northstar Supply",
        mode = settings.Mode,
        currency = settings.Currency,
        catalog = Catalog.Products,
        publicBaseUrl = settings.PublicBaseUrl
    });
}
