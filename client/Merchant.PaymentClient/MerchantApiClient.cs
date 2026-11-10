using System.Net.Http.Json;

namespace Merchant.PaymentClient;

public sealed class MerchantApiClient(HttpClient http)
{
    public async Task<MerchantConfig> GetConfig() =>
        await Read<MerchantConfig>(await http.GetAsync("api/config"));

    public async Task<CheckoutResponse> Checkout(string email, string productId, int quantity) =>
        await Read<CheckoutResponse>(await http.PostAsJsonAsync("api/checkout",
            new { email, items = new[] { new { productId, quantity } } }));

    public async Task<PaymentOrder> GetOrder(string id, string key, bool reconcile = false)
    {
        var path = $"api/orders/{Uri.EscapeDataString(id)}";
        if (reconcile) path += "/reconcile";
        path += $"?key={Uri.EscapeDataString(key)}";
        return await Read<PaymentOrder>(reconcile
            ? await http.PostAsync(path, null)
            : await http.GetAsync(path));
    }

    public async Task<DemoResponse> CompleteDemo(string id, string key, string result) =>
        await Read<DemoResponse>(await http.PostAsJsonAsync(
            $"api/demo/orders/{Uri.EscapeDataString(id)}/complete?key={Uri.EscapeDataString(key)}",
            new { result }));

    internal Task<HttpResponseMessage> Forward(HttpRequestMessage request, CancellationToken cancellationToken) =>
        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Order not found. Check that the complete order link was used."
                    : $"The merchant API returned HTTP {(int)response.StatusCode}. Please try again.");
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new HttpRequestException("The merchant API returned an empty response.");
        }
    }
}

public sealed record MerchantConfig(string Merchant, string Mode, string Currency, Product[] Catalog);
public sealed record Product(string Id, string Name, int Price);
public sealed record CheckoutResponse(PaymentOrder Order, string OrderKey, string HppUrl);
public sealed record DemoResponse(string RedirectUrl);
public sealed record PaymentOrder(string Id, string Mode, string Status, int Amount, string Currency,
    OrderItem[] Items, string? TransactionId, string? GpStatus);
public sealed record OrderItem(string Name, int Price, int Quantity);

public static class Money
{
    public static string Format(int amount, string currency) =>
        $"{currency} {(amount / 100m).ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}";
}
