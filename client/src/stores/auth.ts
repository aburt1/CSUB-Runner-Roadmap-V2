import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { PublicClientApplication } from '@azure/msal-browser'

// Student/admin user as returned by the auth endpoints.
export interface User {
  id: number | string
  name?: string
  displayName?: string
  email: string
  [key: string]: unknown
}

interface SsoLoginResponse {
  token: string
  student: User
}

// msalConfig (and through it @azure/msal-browser) is heavy and only needed for SSO, so it
// is dynamically imported inside init()/ssoLogin() to keep it out of the student entry
// chunk. Cache the resolved module so repeat calls don't re-import.
type MsalConfigModule = typeof import('../auth/msalConfig')
let msalConfigPromise: Promise<MsalConfigModule> | null = null
function loadMsalConfig(): Promise<MsalConfigModule> {
  msalConfigPromise ??= import('../auth/msalConfig')
  return msalConfigPromise
}

// The student session token lives in sessionStorage under 'csub_token'.
export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)
  const token = ref<string | null>(sessionStorage.getItem('csub_token'))
  const loading = ref(true)
  const ssoLoading = ref(false)
  const ssoError = ref('')
  // Whether SSO is configured. Resolved once msalConfig loads in init() (the SignInCard SSO
  // button only renders after init flips loading=false, so it is set by then). Kept in the
  // store so the public page can render it reactively.
  const isAzureAdConfigured = ref(false)

  // The resolved MSAL instance and login scopes, populated by init() once msalConfig loads.
  let msalInstance: PublicClientApplication | null = null
  let loginRequest: { scopes: string[] } = { scopes: [] }
  let msalReady = false
  let redirecting = false

  const isAuthenticated = computed(() => !!token.value)

  async function sendIdTokenToServer(idToken: string): Promise<SsoLoginResponse> {
    const res = await fetch('/api/auth/sso', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ idToken }),
    })
    if (!res.ok) {
      const data = await res.json().catch(() => ({}))
      throw new Error(data.error || 'SSO login failed')
    }
    return res.json() as Promise<SsoLoginResponse>
  }

  async function handleSsoResponse(idToken: string): Promise<void> {
    const data = await sendIdTokenToServer(idToken)
    sessionStorage.setItem('csub_token', data.token)
    token.value = data.token
    user.value = data.student
  }

  // Validate any token already in sessionStorage against /api/auth/me. Runs independently
  // of MSAL so init() can fire it in parallel with (or ahead of) MSAL setup.
  async function validateExistingToken(): Promise<void> {
    const existing = sessionStorage.getItem('csub_token')
    if (!existing) return
    try {
      const res = await fetch('/api/auth/me', {
        headers: { Authorization: `Bearer ${existing}` },
      })
      if (res.ok) {
        user.value = await res.json()
      } else if (res.status === 401 || res.status === 403) {
        // Only an explicit rejection discards the session — a transient 5xx here
        // would otherwise silently log the student out on page load.
        sessionStorage.removeItem('csub_token')
        token.value = null
      }
    } catch {
      // Server unavailable — keep token, try later
    }
  }

  // Call once on app start. Kick off token validation immediately, and — unless we can skip
  // it — set up MSAL and handle any redirect sign-in in parallel. Skips MSAL entirely when
  // there is no redirect hash to process and a token already exists, so a returning student
  // never pays the msal-browser download/init on load.
  async function init(): Promise<void> {
    // Start /api/auth/me right away so its round-trip overlaps MSAL's import + init.
    const validation = validateExistingToken()

    // A redirect sign-in leaves an id_token/code in the URL fragment that only MSAL can
    // consume; when it is absent and a session token is already stored, there is nothing
    // for MSAL to do at startup and we skip loading it.
    const hasRedirectHash = /(?:^|[#&])(id_token|code|error)=/.test(window.location.hash)
    const hasStoredToken = !!sessionStorage.getItem('csub_token')
    if (!hasRedirectHash && hasStoredToken) {
      await validation
      loading.value = false
      return
    }

    try {
      const config = await loadMsalConfig()
      isAzureAdConfigured.value = config.isAzureAdConfigured
      msalInstance = config.msalInstance
      loginRequest = config.loginRequest

      if (config.isAzureAdConfigured && msalInstance) {
        try {
          await msalInstance.initialize()
          msalReady = true
        } catch {
          // MSAL init failed — continue to normal flow
        }
        if (msalReady) {
          try {
            const redirectResponse = await msalInstance.handleRedirectPromise()
            if (redirectResponse?.idToken) {
              await handleSsoResponse(redirectResponse.idToken)
              loading.value = false
              return
            }
          } catch (err) {
            // The user DID come back from a redirect sign-in — swallowing this would
            // strand them on the public page with no explanation. Surface it.
            console.error('[sso] redirect sign-in failed', err)
            ssoError.value =
              (err as { message?: string })?.message || 'Sign-in failed. Please try again.'
          }
        }
      }
    } catch {
      // msalConfig failed to load — continue with the token-only flow.
    }

    await validation
    loading.value = false
  }

  async function ssoLogin(): Promise<void> {
    if (!msalInstance || !msalReady) return
    ssoLoading.value = true
    ssoError.value = ''
    // BrowserAuthError comes from the same lazily-loaded library; the popup-fallback branch
    // below narrows on it, so import it here rather than pulling msal into the entry chunk.
    const { BrowserAuthError } = await import('@azure/msal-browser')
    try {
      let response
      try {
        response = await msalInstance.loginPopup(loginRequest)
      } catch (err) {
        if (
          err instanceof BrowserAuthError &&
          ['popup_window_error', 'empty_window_error', 'popup_timeout'].includes(err.errorCode)
        ) {
          redirecting = true
          try {
            await msalInstance.loginRedirect(loginRequest)
          } catch (redirectErr) {
            // The redirect never happened — reset the flag so the finally block can
            // clear the spinner instead of leaving "Signing in..." stuck forever.
            redirecting = false
            throw redirectErr
          }
          return
        }
        throw err
      }
      if (response?.idToken) await handleSsoResponse(response.idToken)
    } catch (err: unknown) {
      const authErr = err as { errorCode?: string; message?: string }
      ssoError.value =
        authErr.errorCode === 'user_cancelled' ? '' : authErr.message || 'SSO login failed'
    } finally {
      if (!redirecting) ssoLoading.value = false
    }
  }

  async function devLogin(name: string, email: string): Promise<void> {
    const res = await fetch('/api/auth/dev-login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, email }),
    })
    if (!res.ok) throw new Error('Login failed')
    const data: SsoLoginResponse = await res.json()
    sessionStorage.setItem('csub_token', data.token)
    token.value = data.token
    user.value = data.student
  }

  function logout(): void {
    sessionStorage.removeItem('csub_token')
    token.value = null
    user.value = null
    if (isAzureAdConfigured.value && msalInstance && msalReady) msalInstance.clearCache()
  }

  return {
    user,
    token,
    loading,
    ssoLoading,
    ssoError,
    isAuthenticated,
    isAzureAdConfigured,
    init,
    ssoLogin,
    devLogin,
    logout,
  }
})
