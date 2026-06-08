import { PublicClientApplication } from '@azure/msal-browser'

const clientId: string | undefined = import.meta.env.VITE_AZURE_AD_CLIENT_ID
const tenantId: string | undefined = import.meta.env.VITE_AZURE_AD_TENANT_ID
const redirectUri: string = import.meta.env.VITE_AZURE_AD_REDIRECT_URI || 'http://localhost:3000'

export const isAzureAdConfigured: boolean = !!(clientId && tenantId)

export const loginRequest: { scopes: string[] } = { scopes: ['openid', 'profile', 'email'] }

export const msalInstance: PublicClientApplication | null = isAzureAdConfigured
  ? new PublicClientApplication({
      auth: {
        clientId: clientId!,
        authority: `https://login.microsoftonline.com/${tenantId}`,
        redirectUri,
      },
      cache: {
        cacheLocation: 'sessionStorage',
      },
    })
  : null
