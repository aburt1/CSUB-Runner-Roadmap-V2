import { PublicClientApplication } from '@azure/msal-browser'

const clientId: string | undefined = import.meta.env.VITE_AZURE_AD_CLIENT_ID
const tenantId: string | undefined = import.meta.env.VITE_AZURE_AD_TENANT_ID
// Fall back to the page's own origin (correct in dev AND prod) — a hardcoded
// localhost fallback would silently break SSO redirects in production builds.
const redirectUri: string = import.meta.env.VITE_AZURE_AD_REDIRECT_URI || window.location.origin

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
