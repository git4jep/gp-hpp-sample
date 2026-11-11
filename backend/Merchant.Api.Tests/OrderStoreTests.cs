using System.Text.Json;
using Merchant.Api;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Merchant.Api.Tests;

public sealed class OrderStoreTests
{
    [Fact]
    public void SqliteStoreRetainsThePersistenceContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"merchant-store-{Guid.NewGuid():N}");
        var settings = new AppSettings { DatabasePath = Path.Combine(directory, "orders.sqlite") };
        try { VerifyContract(settings); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [SqlServerFact]
    public void SqlServerStoreRetainsThePersistenceContractAndSchemaIsRepeatable()
    {
        var connectionString = Environment.GetEnvironmentVariable("MERCHANT_TEST_SQL_CONNECTION_STRING")!;
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        DatabaseSchema.Initialize(connection);
        DatabaseSchema.Initialize(connection);
        DatabaseSchema.Check(connection);
        VerifyContract(new AppSettings { DatabaseProvider = "sqlserver", DatabaseConnectionString = connectionString });
    }

    [Fact]
    public void SqlServerSettingsDoNotRequireASqlitePath()
    {
        new AppSettings
        {
            DatabaseProvider = "sqlserver",
            DatabasePath = "",
            DatabaseConnectionString = "Server=example.database.windows.net;Database=merchant;Authentication=Active Directory Managed Identity;Encrypt=True"
        }.ValidateDatabase();
    }

    [Theory]
    [InlineData("postgres", "")]
    [InlineData("sqlserver", "")]
    [InlineData("sqlserver", "Server=localhost;Password=fake-private-value;")]
    [InlineData("sqlserver", "unrecognized=fake-private-value")]
    public void InvalidDatabaseSettingsFailWithoutDisclosingConnectionData(string provider, string connection)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AppSettings
        {
            DatabaseProvider = provider, DatabaseConnectionString = connection
        }.ValidateDatabase());
        Assert.DoesNotContain("fake-private-value", error.ToString());
    }

    private static void VerifyContract(AppSettings settings)
    {
        var store = new OrderStore(settings);
        store.CheckReadiness();
        var order = store.Create([new("tumbler", 2)], settings);
        try
        {
            store.Link(order.Id, "LNK_contract", "https://example.com/hosted");
            var reread = new OrderStore(settings).Get(order.Id)!;
            Assert.Equal(5600, reread.Amount);
            Assert.Equal(order.AccessKey, reread.AccessKey);
            Assert.Equal(order.CallbackKey, reread.CallbackKey);
            Assert.NotEqual(reread.AccessKey, reread.CallbackKey);
            Assert.Equal("LNK_contract", reread.LinkId);
            Assert.Single(reread.Items);
            Assert.Null(store.Get(order.Id.ToUpperInvariant()));
            Assert.Null(store.Get("' OR 1=1 --"));
            store.Event(order.Id, "return", JsonSerializer.SerializeToElement(new
            {
                id = "TRN_contract", status = "CAPTURÉD", token = "not-for-storage", card = "private-card"
            }));
            store.Event(order.Id, "notification", JsonSerializer.SerializeToElement(new { id = "TRN_contract" }), true);
            var events = JsonSerializer.SerializeToElement(store.Events(order.Id));
            Assert.Equal(2, events.GetArrayLength());
            Assert.Equal("return", events[0].GetProperty("source").GetString());
            Assert.Equal("CAPTURÉD", events[0].GetProperty("payload").GetProperty("status").GetString());
            Assert.True(events[1].GetProperty("verified").GetBoolean());
            Assert.DoesNotContain("not-for-storage", events.GetRawText());
            Assert.DoesNotContain("private-card", events.GetRawText());
            store.Cancel(order.Id);
            store.Apply(order.Id, "PENDING", "TRN_pending", "PENDING", "{}");
            Assert.Equal("CANCELLED", store.Get(order.Id)!.Status);
            store.Apply(order.Id, "AUTHORIZED", "TRN_authorized", "PREAUTHORIZED", "{}");
            Assert.Equal("AUTHORIZED", store.Get(order.Id)!.Status);
            store.Apply(order.Id, "PAID", "TRN_captured", "CAPTURED", "{}");
            Parallel.For(0, 8, index =>
            {
                if (index % 2 == 0) store.Cancel(order.Id);
                else store.Apply(order.Id, "DECLINED", "TRN_declined", "DECLINED", "{}");
            });
            store.Apply(order.Id, "PAID", "TRN_captured", "CAPTURED", "{}");
            reread = new OrderStore(settings).Get(order.Id)!;
            Assert.Equal("PAID", reread.Status);
            Assert.Equal("CAPTURED", reread.GpStatus);
            Assert.Equal("TRN_captured", reread.TransactionId);
            Assert.NotNull(reread.CancelledAt);
            Assert.Throws<ApiException>(() => store.Create([new("unknown", 1)], settings));
            Assert.Throws<ApiException>(() => store.Create([new("tumbler", 0)], settings));
        }
        finally
        {
            // Only remove this test's random order, never truncate/drop an operator's database.
            if (settings.DatabaseProvider == "sqlserver")
            {
                using var connection = new SqlConnection(settings.DatabaseConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM dbo.payment_events WHERE order_id=@id; DELETE FROM dbo.orders WHERE id=@id;";
                command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.VarChar, 50) { Value = order.Id });
                command.ExecuteNonQuery();
            }
        }
    }
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MERCHANT_TEST_SQL_CONNECTION_STRING")))
            Skip = "Set MERCHANT_TEST_SQL_CONNECTION_STRING to a dedicated SQL Server/Azure SQL test database.";
    }
}
