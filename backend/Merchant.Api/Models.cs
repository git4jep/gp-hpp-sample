using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Merchant.Api;

public sealed class AppSettings
{
    public string Mode { get; init; } = "demo";
    public string PublicBaseUrl { get; init; } = "http://localhost:4200";
    public string[] LocalDevelopmentOrigins { get; init; } =
    [
        "http://localhost:4200", "http://127.0.0.1:4200",
        "http://localhost:5080", "http://127.0.0.1:5080"
    ];
    public string MerchantId { get; init; } = "MER_d30e85f8e3d64aa9b4535b14e1c2095d";
    public string AppId { get; init; } = "8KR95sQLBorAJBAhsdxGxyn6GLO2RfEewWxjYbYKAP6DREvn";
    public string AppKey { get; init; } = "";
    public string AccountName { get; init; } = "transaction_processing";
    public string Currency { get; init; } = "USD";
    public string Country { get; init; } = "US";
    public string AdminKey { get; init; } = "";
    public string DatabasePath { get; init; } = "data/merchant.sqlite";
    public string DatabaseProvider { get; init; } = "sqlite";
    public string DatabaseConnectionString { get; init; } = "";
    public string ApiBaseUrl => "https://apis.sandbox.globalpay.com/ucp/";

    public string[] AllowedOrigins => [.. LocalDevelopmentOrigins.Prepend(PublicBaseUrl).Distinct(StringComparer.Ordinal)];
    public bool IsAllowedOrigin(string origin) => AllowedOrigins.Contains(origin, StringComparer.Ordinal);

    public static AppSettings Load()
    {
        string Env(string key, string fallback) => Environment.GetEnvironmentVariable(key) ?? fallback;
        var s = new AppSettings
        {
            Mode = Env("GP_MODE", "demo"),
            PublicBaseUrl = Env("PUBLIC_BASE_URL", "http://localhost:4200").TrimEnd('/'),
            LocalDevelopmentOrigins = Env("LOCAL_DEV_ORIGINS",
                "http://localhost:4200,http://127.0.0.1:4200,http://localhost:5080,http://127.0.0.1:5080")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            MerchantId = Env("GP_MERCHANT_ID", "MER_d30e85f8e3d64aa9b4535b14e1c2095d"),
            AppId = Env("GP_APP_ID", "8KR95sQLBorAJBAhsdxGxyn6GLO2RfEewWxjYbYKAP6DREvn"),
            AppKey = Env("GP_APP_KEY", ""),
            AccountName = Env("GP_ACCOUNT_NAME", "transaction_processing"),
            Currency = Env("GP_CURRENCY", "USD"),
            Country = Env("GP_COUNTRY", "US"),
            AdminKey = Env("ADMIN_API_KEY", ""),
            DatabasePath = Env("DATABASE_PATH", "data/merchant.sqlite"),
            DatabaseProvider = Env("DATABASE_PROVIDER", "sqlite"),
            DatabaseConnectionString = Env("DATABASE_CONNECTION_STRING", "")
        };
        s.ValidateDatabase();
        if (s.Mode is not ("demo" or "sandbox")) throw new InvalidOperationException("GP_MODE must be demo or sandbox.");
        foreach (var origin in s.LocalDevelopmentOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var local) ||
                local.Scheme is not ("http" or "https") || !local.IsLoopback ||
                local.UserInfo != "" || local.AbsolutePath != "/" || local.Query != "" ||
                local.Fragment != "" || origin != local.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("LOCAL_DEV_ORIGINS must contain comma-separated HTTP(S) loopback origins without paths or trailing slashes.");
        if (!Uri.TryCreate(s.PublicBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" ||
            uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new InvalidOperationException("PUBLIC_BASE_URL must be an HTTP(S) origin without a path or query.");
        if (s.Mode == "sandbox" && (uri.Scheme != "https" || s.AppKey == ""))
            throw new InvalidOperationException("Sandbox requires GP_APP_KEY and a public HTTPS PUBLIC_BASE_URL. Run scripts/setup.py.");
        if (s.Currency is not ("USD" or "EUR" or "GBP") || !System.Text.RegularExpressions.Regex.IsMatch(s.Country, "^[A-Z]{2}$"))
            throw new InvalidOperationException("Choose USD, EUR, or GBP and a two-letter GP_COUNTRY.");
        return s;
    }

    public void ValidateDatabase()
    {
        if (DatabaseProvider is not ("sqlite" or "sqlserver"))
            throw new InvalidOperationException("DATABASE_PROVIDER must be sqlite or sqlserver.");
        if (DatabaseProvider == "sqlite")
        {
            if (string.IsNullOrWhiteSpace(DatabasePath))
                throw new InvalidOperationException("SQLite requires DATABASE_PATH.");
            return;
        }
        try
        {
            var options = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(DatabaseConnectionString);
            if (string.IsNullOrWhiteSpace(options.DataSource) || string.IsNullOrWhiteSpace(options.InitialCatalog))
                throw new ArgumentException();
        }
        catch (ArgumentException)
        {
            // Never include the supplied string (which may contain credentials) in diagnostics.
            throw new InvalidOperationException("SQL Server requires a valid DATABASE_CONNECTION_STRING with Server and Database.");
        }
    }
}

public record Product(string Id, string Name, string Category, int Price, string Art, string Description);
public static class Catalog
{
    public static readonly Product[] Products =
    [
        new("weekender", "The Weekender", "EVERYDAY CARRY", 8900, "bag", "A canvas companion for wherever the weekend takes you."),
        new("tumbler", "Trail Tumbler", "SMALL ESSENTIALS", 2800, "cup", "Your morning ritual, ready for the road. Double-wall steel."),
        new("notebook", "Field Notes Set", "GOOD IDEAS START HERE", 1800, "book", "Three pocket notebooks for plans, sketches, and detours.")
    ];
}

public record CartItem(string ProductId, int Quantity);
public record CheckoutRequest(CartItem[] Items, string Email);
public record OrderItem(string ProductId, string Name, int Price, int Quantity);
public record DemoRequest(string Result);

public sealed class Order
{
    public required string Id { get; init; }
    public required string Mode { get; init; }
    [JsonIgnore] public string AccessKey { get; init; } = "";
    [JsonIgnore] public string CallbackKey { get; init; } = "";
    public string Status { get; set; } = "PENDING";
    public int Amount { get; init; }
    public required string Currency { get; init; }
    public required OrderItem[] Items { get; init; }
    [JsonIgnore] public string? LinkId { get; set; }
    [JsonIgnore] public string? HppUrl { get; set; }
    public string? TransactionId { get; set; }
    public string? GpStatus { get; set; }
    public string? CancelledAt { get; set; }
    public required string CreatedAt { get; init; }
}

public record CallbackUrls(
    [property: JsonPropertyName("return_url")] string ReturnUrl,
    [property: JsonPropertyName("cancel_url")] string CancelUrl,
    [property: JsonPropertyName("status_url")] string StatusUrl);

public static class PaymentRules
{
    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    public static bool SecureEquals(string? a, string? b) =>
        a is not null && b is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    public static string Outcome(string? status) => status switch
    {
        "CAPTURED" or "FUNDED" => "PAID",
        "PREAUTHORIZED" => "AUTHORIZED",
        "DECLINED" or "REJECTED" => "DECLINED",
        _ => "PENDING"
    };
    public static int Rank(string status) => status switch { "PAID" => 3, "AUTHORIZED" => 2, "DECLINED" => 1, _ => 0 };
    public static CallbackUrls Urls(AppSettings settings, Order order)
    {
        var suffix = $"{order.Id}?state={order.CallbackKey}";
        return new($"{settings.PublicBaseUrl}/payments/return/{suffix}",
            $"{settings.PublicBaseUrl}/api/payments/cancel/{suffix}",
            $"{settings.PublicBaseUrl}/api/payments/notifications/{suffix}");
    }
    public static Dictionary<string, string> SafeResponse(JsonElement data)
    {
        string[] allowed = ["id", "status", "type", "amount", "currency", "reference",
            "merchant_id", "account_id", "account_name", "time_created", "capture_mode"];
        var result = new Dictionary<string, string>();
        if (data.ValueKind != JsonValueKind.Object) return result;
        foreach (var key in allowed)
            if (data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: <= 200 } text) result[key] = text;
        return result;
    }
}

public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
