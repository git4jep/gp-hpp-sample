using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Merchant.Api;

public sealed class OrderStore
{
    private readonly string connectionString;
    public OrderStore(AppSettings settings)
    {
        var path = Path.GetFullPath(settings.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS orders (
                id TEXT PRIMARY KEY, mode TEXT NOT NULL, access_key TEXT NOT NULL, callback_key TEXT NOT NULL,
                status TEXT NOT NULL, amount INTEGER NOT NULL, currency TEXT NOT NULL, items TEXT NOT NULL,
                link_id TEXT, hpp_url TEXT, transaction_id TEXT, gp_status TEXT, verified_response TEXT,
                cancelled_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS payment_events (
                id INTEGER PRIMARY KEY, order_id TEXT NOT NULL, source TEXT NOT NULL,
                payload TEXT NOT NULL, verified INTEGER NOT NULL, created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }
    public Order Create(CartItem[] items, AppSettings settings)
    {
        if (items is not { Length: >= 1 and <= 10 }) throw new ApiException(400, "Choose 1–10 items.");
        var normalized = items.Select(item =>
        {
            var product = item is null ? null : Catalog.Products.FirstOrDefault(p => p.Id == item.ProductId);
            if (product is null || item!.Quantity is < 1 or > 10) throw new ApiException(400, "Choose a valid product and quantity (1–10).");
            return new OrderItem(product.Id, product.Name, product.Price, item.Quantity);
        }).ToArray();
        var order = new Order
        {
            Id = $"ord_{Guid.NewGuid():N}",
            Mode = settings.Mode,
            AccessKey = PaymentRules.NewKey(),
            CallbackKey = PaymentRules.NewKey(),
            Amount = normalized.Sum(i => i.Price * i.Quantity),
            Currency = settings.Currency,
            Items = normalized,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orders (id, mode, access_key, callback_key, status, amount, currency, items, created_at, updated_at)
            VALUES ($id, $mode, $access, $callback, 'PENDING', $amount, $currency, $items, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$id", order.Id);
        cmd.Parameters.AddWithValue("$mode", order.Mode);
        cmd.Parameters.AddWithValue("$access", order.AccessKey);
        cmd.Parameters.AddWithValue("$callback", order.CallbackKey);
        cmd.Parameters.AddWithValue("$amount", order.Amount);
        cmd.Parameters.AddWithValue("$currency", order.Currency);
        cmd.Parameters.AddWithValue("$items", JsonSerializer.Serialize(order.Items));
        cmd.Parameters.AddWithValue("$now", order.CreatedAt);
        cmd.ExecuteNonQuery();
        return order;
    }
    public Order? Get(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string Text(string key) => r.GetString(r.GetOrdinal(key));
        string? Optional(string key) => r.IsDBNull(r.GetOrdinal(key)) ? null : Text(key);
        return new Order
        {
            Id = Text("id"),
            Mode = Text("mode"),
            AccessKey = Text("access_key"),
            CallbackKey = Text("callback_key"),
            Status = Text("status"),
            Amount = r.GetInt32(r.GetOrdinal("amount")),
            Currency = Text("currency"),
            Items = JsonSerializer.Deserialize<OrderItem[]>(Text("items"))!,
            LinkId = Optional("link_id"),
            HppUrl = Optional("hpp_url"),
            TransactionId = Optional("transaction_id"),
            GpStatus = Optional("gp_status"),
            CancelledAt = Optional("cancelled_at"),
            CreatedAt = Text("created_at")
        };
    }
    private void Execute(string sql, params (string Key, object? Value)[] parameters)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void Link(string id, string linkId, string url) =>
        Execute("UPDATE orders SET link_id=$link, hpp_url=$url, updated_at=$now WHERE id=$id",
            ("$id", id), ("$link", linkId), ("$url", url), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    public void LinkFailed(string id) => Execute("UPDATE orders SET status='LINK_FAILED' WHERE id=$id", ("$id", id));
    public void Cancel(string id) =>
        Execute("""
            UPDATE orders SET cancelled_at=$now, updated_at=$now,
              status=CASE WHEN status IN ('PAID','AUTHORIZED') THEN status ELSE 'CANCELLED' END WHERE id=$id
            """, ("$id", id), ("$now", DateTimeOffset.UtcNow.ToString("O")));

    public void Apply(string id, string next, string transactionId, string gpStatus, string safeResponse)
    {
        // Atomic WHERE prevents out-of-order callbacks from downgrading an approval.
        Execute("""
            UPDATE orders SET
              status=CASE WHEN $status='PENDING' AND cancelled_at IS NOT NULL THEN 'CANCELLED' ELSE $status END,
              transaction_id=$transaction, gp_status=$gp, verified_response=$response, updated_at=$now
            WHERE id=$id AND
              (CASE status WHEN 'PAID' THEN 3 WHEN 'AUTHORIZED' THEN 2 WHEN 'DECLINED' THEN 1 ELSE 0 END) <= $rank
            """, ("$id", id), ("$status", next), ("$rank", PaymentRules.Rank(next)),
            ("$transaction", transactionId), ("$gp", gpStatus), ("$response", safeResponse),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }
    public void Event(string id, string source, JsonElement payload, bool verified = false) =>
        Execute("INSERT INTO payment_events (order_id,source,payload,verified,created_at) VALUES ($id,$source,$payload,$verified,$now)",
            ("$id", id), ("$source", source), ("$payload", JsonSerializer.Serialize(PaymentRules.SafeResponse(payload))),
            ("$verified", verified ? 1 : 0), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    public object[] Events(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source,payload,verified,created_at FROM payment_events WHERE order_id=$id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        var events = new List<object>();
        while (reader.Read()) events.Add(new
        {
            source = reader.GetString(0),
            payload = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1)),
            verified = reader.GetInt32(2) == 1,
            createdAt = reader.GetString(3)
        });
        return events.ToArray();
    }
}
