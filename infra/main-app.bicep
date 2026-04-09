param location string
param tags object
param resourceToken string
param containerAppsEnvironmentId string
param containerRegistryName string
param aiAgentEndpoint string
param aiAgentId string
param aiAgentVersion string = ''
param entraSpaClientId string
param entraTenantId string
param entraBackendClientId string = ''
param webImageName string
param userAssignedIdentityId string = ''
param oboManagedIdentityClientId string = ''
param appInsightsConnectionString string = ''
param appInsightsFrontendConnectionString string = ''

var abbrs = loadJsonContent('./abbreviations.json')

// Base env vars always present
var baseEnv = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'ASPNETCORE_URLS'
    value: 'http://+:8080'
  }
  {
    name: 'ENTRA_SPA_CLIENT_ID'
    value: entraSpaClientId
  }
  {
    name: 'ENTRA_TENANT_ID'
    value: entraTenantId
  }
  {
    name: 'AI_AGENT_ENDPOINT'
    value: aiAgentEndpoint
  }
  {
    name: 'AI_AGENT_ID'
    value: aiAgentId
  }
  {
    name: 'AI_AGENT_VERSION'
    value: aiAgentVersion
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'APPLICATIONINSIGHTS_FRONTEND_CONNECTION_STRING'
    value: appInsightsFrontendConnectionString
  }
]

// User-assigned MI client ID — always needed since RBAC is assigned to this MI
var miEnv = [
  {
    name: 'MANAGED_IDENTITY_CLIENT_ID'
    value: oboManagedIdentityClientId
  }
]

// OBO env vars only injected when configured
var oboEnv = !empty(entraBackendClientId) ? [
  {
    name: 'ENTRA_BACKEND_CLIENT_ID'
    value: entraBackendClientId
  }
] : []

var containerEnv = concat(baseEnv, miEnv, oboEnv)

// Single Container App - serves both frontend and backend
module webApp './core/host/container-app.bicep' = {
  name: 'web-container-app'
  params: {
    name: '${abbrs.appContainerApps}web-${resourceToken}'
    location: location
    tags: union(tags, { 'azd-service-name': 'web' })
    containerAppsEnvironmentId: containerAppsEnvironmentId
    containerRegistryName: containerRegistryName
    containerImage: webImageName
    targetPort: 8080
    env: containerEnv
    enableIngress: true
    external: true
    healthProbePath: '/api/health'
    userAssignedIdentityId: userAssignedIdentityId
  }
}

output webEndpoint string = 'https://${webApp.outputs.fqdn}'
output webAppName string = webApp.outputs.name
