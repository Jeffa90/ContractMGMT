// Azure reference deployment: App Service (Linux, container) + Azure SQL + Key Vault + Log Analytics.
// Status: Scaffolded / Not Verified (no Azure subscription, az CLI or Bicep compiler in the build workspace; not compiled).
// Secrets are never parameters with values in source control: the SQL admin is Entra-only and the application
// connection string is a Key Vault reference.
targetScope = 'resourceGroup'

@description('Short name used as a prefix for resources.')
param name string
param location string = resourceGroup().location
@description('Container image, e.g. myregistry.azurecr.io/ecp:1.0.0')
param image string
@description('Object ID of the Entra group that administers the SQL server.')
param sqlAdminGroupObjectId string
param sqlAdminGroupName string

var suffix = uniqueString(resourceGroup().id)

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-log-${suffix}'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 90 }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('${name}kv${suffix}', 24)
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled' // restrict with private endpoints in a hardened deployment
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${name}-sql-${suffix}'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: subscription().tenantId
    }
  }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: 'ecp'
  location: location
  sku: { name: 'S1', tier: 'Standard' }
  properties: { requestedBackupStorageRedundancy: 'Geo' }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${name}-plan-${suffix}'
  location: location
  kind: 'linux'
  sku: { name: 'P1v3', tier: 'PremiumV3' }
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: '${name}-app-${suffix}'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|${image}'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/ready'
      appSettings: [
        { name: 'Ecp__Mode', value: 'Production' }
        { name: 'Ecp__DemoTenantResolution', value: 'false' }
        { name: 'Ecp__DemoSeed', value: 'false' }
        { name: 'Ecp__DataDirectory', value: '/home/ecp' }
        { name: 'Database__Provider', value: 'SqlServer' }
        // Secret reference only; the value lives in Key Vault. See the platform's keyvault: secret reference notes.
        { name: 'Database__ConnectionStringRef', value: 'env:ECP_SQL_CONNECTION' }
        { name: 'ECP_SQL_CONNECTION', value: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=ecp-sql-connection)' }
        { name: 'Database__AutoMigrate', value: 'false' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'true' }
      ]
    }
  }
}

// The app identity may read secrets (Key Vault Secrets User).
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, app.id, 'secrets-user')
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

output appHostName string = app.properties.defaultHostName
