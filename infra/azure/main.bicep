targetScope = 'resourceGroup'

@minLength(3)
@maxLength(16)
param prefix string
param location string = resourceGroup().location
@description('Signed-in deployment operator and SQL Entra administrator object ID.')
param operatorObjectId string
param operatorLogin string
@allowed(['B1', 'B2', 'B3', 'S1', 'P0v3', 'P1v3'])
param planSku string = 'B1'
param gpAccountName string = 'transaction_processing'
param gpMerchantId string = 'MER_d30e85f8e3d64aa9b4535b14e1c2095d'
param gpAppId string = '8KR95sQLBorAJBAhsdxGxyn6GLO2RfEewWxjYbYKAP6DREvn'
@allowed(['USD', 'EUR', 'GBP'])
param currency string = 'USD'
param country string = 'US'
@allowed(['demo', 'sandbox'])
param paymentMode string = 'sandbox'

var suffix = uniqueString(resourceGroup().id)
var vaultName = take('${prefix}${suffix}', 24)
var apiName = '${prefix}-api-${suffix}'
var clientName = '${prefix}-client-${suffix}'
var sqlName = '${prefix}-sql-${suffix}'
var tags = { application: 'merchant-hpp', environment: 'sandbox' }

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: '${prefix}-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: planSku, capacity: 1 }
  properties: { reserved: true }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    accessPolicies: []
    publicNetworkAccess: 'Enabled'
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'User'
      login: operatorLogin
      sid: operatorObjectId
      tenantId: tenant().tenantId
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'merchant'
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: {
    maxSizeBytes: 2147483648
    collation: 'Latin1_General_100_BIN2'
    requestedBackupStorageRedundancy: 'Local'
  }
}

resource api 'Microsoft.Web/sites@2024-11-01' = {
  name: apiName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet Merchant.Api.dll'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      httpLoggingEnabled: false
      detailedErrorLoggingEnabled: false
      requestTracingEnabled: false
      // Closed until the provisioning script installs explicit outbound-IP rules.
      ipSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsUseMain: false
    }
  }
}

resource client 'Microsoft.Web/sites@2024-11-01' = {
  name: clientName
  location: location
  tags: tags
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet Merchant.PaymentClient.dll'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      httpLoggingEnabled: false
      detailedErrorLoggingEnabled: false
      requestTracingEnabled: false
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsUseMain: false
    }
  }
}

resource apiVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, api.id, 'secrets-user')
  scope: vault
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource operatorVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, operatorObjectId, 'secrets-officer')
  scope: vault
  properties: {
    principalId: operatorObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  }
}

resource apiSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: api
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    DATABASE_PROVIDER: 'sqlserver'
    DATABASE_CONNECTION_STRING: 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;'
    GP_MODE: paymentMode
    PUBLIC_BASE_URL: 'https://${client.properties.defaultHostName}'
    LOCAL_DEV_ORIGINS: ''
    GP_ACCOUNT_NAME: gpAccountName
    GP_MERCHANT_ID: gpMerchantId
    GP_APP_ID: gpAppId
    GP_CURRENCY: currency
    GP_COUNTRY: country
    GP_APP_KEY: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/gp-app-key/)'
    ADMIN_API_KEY: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/admin-api-key/)'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    WEBSITE_RUN_FROM_PACKAGE: '1'
  }
}

resource clientSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: client
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    MerchantApi__BaseUrl: 'https://${api.properties.defaultHostName}/'
    AllowedHosts: client.properties.defaultHostName
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    WEBSITE_RUN_FROM_PACKAGE: '1'
  }
}

output apiName string = api.name
output clientName string = client.name
output apiUrl string = 'https://${api.properties.defaultHostName}'
output clientUrl string = 'https://${client.properties.defaultHostName}'
output apiPrincipalId string = api.identity.principalId
output apiOutboundIps string = api.properties.possibleOutboundIpAddresses
output clientOutboundIps string = client.properties.possibleOutboundIpAddresses
output sqlServer string = sql.name
output sqlHost string = sql.properties.fullyQualifiedDomainName
output databaseName string = database.name
output vaultName string = vault.name
