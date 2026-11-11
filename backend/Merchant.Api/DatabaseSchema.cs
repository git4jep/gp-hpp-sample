using Microsoft.Data.SqlClient;

namespace Merchant.Api;

public static class DatabaseSchema
{
    public const int Version = 1;

    // Deployment credentials own schema changes. The API's identity never calls this.
    public static void Initialize(SqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 120;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'merchant-schema',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;
            IF @result < 0 THROW 51000, 'Could not acquire schema initialization lock.', 1;
            """;
        command.ExecuteNonQuery();
        using var resource = typeof(DatabaseSchema).Assembly.GetManifestResourceStream("Merchant.Api.Database.001-initial.sql")
            ?? throw new InvalidOperationException("Database schema resource is missing.");
        using var reader = new StreamReader(resource);
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public static void Check(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM dbo.schema_versions";
        try
        {
            if (command.ExecuteScalar() is not int version || version != Version)
                throw new InvalidOperationException("SQL schema is incompatible. Run the database deployment tool.");
        }
        catch (SqlException)
        {
            throw new InvalidOperationException("SQL schema is unavailable. Run the database deployment tool and check identity permissions.");
        }
    }

    public static void GrantRuntimeAccess(SqlConnection connection, Guid principalId)
    {
        // Explicit Entra object SID avoids Microsoft Graph lookup/Directory Readers permissions.
        var sid = Convert.ToHexString(principalId.ToByteArray());
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'merchant_api' AND sid <> 0x{{sid}})
                THROW 51000, 'merchant_api belongs to a different identity. Review and replace the user explicitly.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'merchant_api')
                CREATE USER [merchant_api] WITH SID = 0x{{sid}}, TYPE = E;
            GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.orders TO [merchant_api];
            GRANT SELECT, INSERT ON OBJECT::dbo.payment_events TO [merchant_api];
            GRANT SELECT ON OBJECT::dbo.schema_versions TO [merchant_api];
            """;
        command.ExecuteNonQuery();
    }
}
