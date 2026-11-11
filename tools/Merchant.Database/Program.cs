using Azure.Core;
using Azure.Identity;
using Merchant.Api;
using Microsoft.Data.SqlClient;

if (args.Length is < 1 or > 2 || args[0] is not ("initialize" or "grant") ||
    (args[0] == "grant" && (args.Length != 2 || !Guid.TryParse(args[1], out _))) ||
    (args[0] == "initialize" && args.Length != 1))
{
    Console.Error.WriteLine("Usage: Merchant.Database initialize | grant <managed-identity-object-id>");
    return 2;
}

try
{
    var settings = new AppSettings
    {
        DatabaseProvider = "sqlserver",
        DatabaseConnectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING") ?? ""
    };
    settings.ValidateDatabase();
    var options = new SqlConnectionStringBuilder(settings.DatabaseConnectionString);
    // Operator authentication is explicitly Azure CLI, not the application's managed identity.
    // SQL password connections are permitted only for an explicitly configured local test server.
    if (Environment.GetEnvironmentVariable("MERCHANT_DATABASE_LOCAL_TEST") != "1")
    {
        options.Authentication = SqlAuthenticationMethod.NotSpecified;
        options.UserID = "";
        options.Password = "";
        options.Encrypt = SqlConnectionEncryptOption.Mandatory;
        options.TrustServerCertificate = false;
    }
    using var connection = new SqlConnection(options.ConnectionString);
    if (Environment.GetEnvironmentVariable("MERCHANT_DATABASE_LOCAL_TEST") != "1")
        connection.AccessToken = (await new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = Environment.GetEnvironmentVariable("MERCHANT_AZURE_TENANT_ID")
        }).GetTokenAsync(
            new TokenRequestContext(["https://database.windows.net/.default"]))).Token;
    await connection.OpenAsync();
    if (args[0] == "initialize") DatabaseSchema.Initialize(connection);
    else DatabaseSchema.GrantRuntimeAccess(connection, Guid.Parse(args[1]));
    Console.WriteLine("Database operation completed.");
    return 0;
}
catch (Exception)
{
    // Provider/authentication exceptions can include connection details; don't print them.
    Console.Error.WriteLine("Database operation failed. Check schema compatibility, Azure CLI sign-in, SQL administrator identity, firewall and permissions.");
    return 1;
}
