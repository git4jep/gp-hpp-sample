using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Merchant.Api;

public record AccessToken(string Token, string AccountId, DateTimeOffset ExpiresAt);
public record PaymentLink(string Id, string Url);

public sealed class GpClient(HttpClient http, AppSettings settings)
{
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private AccessToken? cached;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    public static string Secret(string nonce, string appKey) =>
        Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(nonce + appKey)));

    private async Task<JsonElement> Request(string path, HttpMethod method, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(settings.ApiBaseUrl), path));
        request.Headers.Add("X-GP-Version", "2021-03-22");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request);
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode)
            {
                var code = Text(data, "error_code");
                if (!Regex.IsMatch(code, "^[A-Z0-9_]{1,80}$")) code = $"HTTP_{(int)response.StatusCode}";
                throw new ApiException(502, $"Global Payments rejected the request ({code}). Check credentials, account name, and HPP permissions.");
            }
            return data;
        }
        catch (ApiException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ApiException(502, "Global Payments could not be reached or returned an invalid response. Try again later.");
        }
    }
    public async Task<AccessToken> GetToken(bool force = false)
    {
        await tokenLock.WaitAsync();
        try
        {
            if (!force && cached?.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;
            var nonce = PaymentRules.NewKey();
            var data = await Request("accesstoken", HttpMethod.Post, new
            {
                app_id = settings.AppId,
                nonce,
                secret = Secret(nonce, settings.AppKey),
                grant_type = "client_credentials"
            });

            if (Text(data, "token") == "" || !data.TryGetProperty("scope", out var scope) ||
                Text(scope, "merchant_id") != settings.MerchantId)
                throw new ApiException(502, "Access token scope does not match the configured merchant.");
            if (!scope.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
                throw new ApiException(502, "No processing accounts are available in the token scope.");
            var account = accounts.EnumerateArray().FirstOrDefault(a => Text(a, "name") == settings.AccountName);
            if (account.ValueKind != JsonValueKind.Object || Text(account, "id") == "" ||
                !account.TryGetProperty("permissions", out var permissions) ||
                !permissions.EnumerateArray().Any(p => p.GetString() == "LNK_POST_Create"))
                throw new ApiException(502, "GP_ACCOUNT_NAME must select an HPP-enabled account with LNK_POST_Create permission.");
            var secondsText = Text(data, "seconds_to_expire");
            if (secondsText == "") secondsText = Text(data, "interval_to_expire");
            var seconds = double.TryParse(secondsText, out var number) && number > 0 && number < 604800 ? number : 600;
            cached = new(Text(data, "token"), Text(account, "id"), DateTimeOffset.UtcNow.AddSeconds(seconds));
            return cached;
        }
        finally { tokenLock.Release(); }
    }
    private async Task<JsonElement> Authenticated(string path, HttpMethod method, object? body = null)
    {
        var auth = await GetToken();
        return await Request(path, method, body, auth.Token);
    }
    public async Task<PaymentLink> CreateLink(Order order, string email, CallbackUrls urls)
    {
        var data = await Authenticated("links", HttpMethod.Post, new
        {
            account_name = settings.AccountName,
            type = "HOSTED_PAYMENT_PAGE",
            usage_mode = "SINGLE",
            usage_limit = "1",
            reference = order.Id,
            name = "Northstar Supply",
            description = "Thoughtful essentials. Everyday adventures.",
            payer = new { email, language = "en" },
            order = new
            {
                amount = order.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                currency = order.Currency,
                reference = order.Id,
                transaction_configuration = new
                {
                    channel = "CNP",
                    country = settings.Country,
                    capture_mode = "AUTO",
                    allowed_payment_methods = new[] { "CARD" }
                },
                payment_method_configuration = new { authentication = new { preference = "CHALLENGE_PREFERRED" } }
            },
            notifications = urls
        });
        var id = Text(data, "id");
        var url = Text(data, "url");
        if (!id.StartsWith("LNK_", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !(uri.Host == "globalpay.com" || uri.Host.EndsWith(".globalpay.com", StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(502, "Global Payments returned an invalid hosted payment link.");
        return new(id, url);
    }
    public async Task<Order> Reconcile(OrderStore store, Order order, string? transactionId = null)
    {
        var token = await GetToken();
        List<JsonElement> transactions;
        if (transactionId is not null)
            transactions = [await Authenticated($"transactions/{Uri.EscapeDataString(transactionId)}", HttpMethod.Get)];
        else
        {
            var data = await Authenticated($"transactions?reference={Uri.EscapeDataString(order.Id)}&account_name={Uri.EscapeDataString(settings.AccountName)}&page_size=100&order=DESC&order_by=TIME_CREATED", HttpMethod.Get);
            transactions = data.TryGetProperty("transactions", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList() : [];
        }
        var verified = transactions.Where(t => Matches(t, order, settings, token.AccountId))
            .OrderByDescending(t => PaymentRules.Rank(PaymentRules.Outcome(Text(t, "status")))).ToList();
        if (transactionId is not null && verified.Count == 0)
            throw new ApiException(409, "The payment response does not match this merchant order.");
        if (verified.Count > 0)
        {
            var t = verified[0];
            store.Event(order.Id, "gp-api", t, true);
            store.Apply(order.Id, PaymentRules.Outcome(Text(t, "status")), Text(t, "id"), Text(t, "status"),
                JsonSerializer.Serialize(PaymentRules.SafeResponse(t)));
        }
        return store.Get(order.Id)!;
    }
    public static bool Matches(JsonElement t, Order order, AppSettings s, string accountId) =>
        Regex.IsMatch(Text(t, "id"), "^TRN_[A-Za-z0-9_-]+$") &&
        Text(t, "merchant_id") == s.MerchantId && Text(t, "account_id") == accountId &&
        (Text(t, "account_name") == "" || Text(t, "account_name") == s.AccountName) &&
        Text(t, "reference") == order.Id && Text(t, "currency") == order.Currency &&
        long.TryParse(Text(t, "amount"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount == order.Amount &&
        Text(t, "type") == "SALE";
    public static string Text(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";
}
