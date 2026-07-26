@description('Location for the Static Web App. Supplied by main.bicep\'s webLocation, which exists as its own parameter because the Free SKU is only offered in westus2, centralus, eastus2, westeurope and eastasia.')
param location string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

// Hosts the Blazor WebAssembly frontend (src/CogniLens.Web). Free tier: $0/month, 100 GB egress,
// and up to 10 free apps per subscription — so unlike Azure SQL and AI Search this does not
// consume a one-per-subscription free allocation, and dev taking it does not block a future prod.
//
// The alternative considered was serving the published wwwroot from the Api container app via
// UseStaticFiles, which would make the SPA same-origin and remove the need for CORS entirely.
// Rejected: it couples every frontend change to an Api revision, canary shift and EF migration
// run, and puts a static bundle behind a minReplicas:0 container that cold-starts. See
// docs/decisions.md.
resource staticSite 'Microsoft.Web/staticSites@2023-12-01' = {
  name: '${namePrefix}-web'
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    // Deliberately no repositoryUrl/branch/repositoryToken. Linking the app to GitHub here would
    // make Azure generate and commit its own workflow file, which would then race cd.yml for the
    // same deployment. cd.yml instead fetches the upload token at deploy time via
    // `az staticwebapp secrets list` on its existing OIDC session and pushes the bundle itself —
    // no deployment token is ever stored as a GitHub secret.
    allowConfigFileUpdates: true

    // Free tier has no staging environments; disabling explicitly means a pull request can never
    // silently attempt to provision one and fail the deploy.
    stagingEnvironmentPolicy: 'Disabled'
  }
}

@description('Name of the Static Web App, used by cd.yml to fetch the deployment token.')
output staticSiteName string = staticSite.name

@description('Public origin of the frontend, e.g. https://witty-sand-0abc.azurestaticapps.net. Feeds the Api CORS allow-list and the storage account CORS rule.')
output webUrl string = 'https://${staticSite.properties.defaultHostname}'
