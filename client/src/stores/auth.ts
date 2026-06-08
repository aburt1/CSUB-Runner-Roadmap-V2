import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { BrowserAuthError } from '@azure/msal-browser'
import { msalInstance, isAzureAdConfigured, loginRequest } from '../auth/msalConfig'

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

// Pinia port of the old React AuthProvider. The student session token lives in
// sessionStorage under 'csub_token' (same key as before).
export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)
  const token = ref<string | null>(sessionStorage.getItem('csub_token'))
  const loading = ref(true)
  const ssoLoading = ref(false)
  const ssoError = ref('')

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

  // Call once on app start: init MSAL, handle redirect, then validate any existing token.
  async function init(): Promise<void> {
    if (isAzureAdConfigured && msalInstance) {
      try {
        await msalInstance.initialize()
        msalReady = true
        const redirectResponse = await msalInstance.handleRedirectPromise()
        if (redirectResponse?.idToken) {
          await handleSsoResponse(redirectResponse.idToken)
          loading.value = false
          return
        }
      } catch {
        // MSAL init/redirect failed — continue to normal flow
      }
    }

    const existing = sessionStorage.getItem('csub_token')
    if (existing) {
      try {
        const res = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${existing}` } })
        if (res.ok) {
          user.value = await res.json()
        } else {
          sessionStorage.removeItem('csub_token')
          token.value = null
        }
      } catch {
        // Server unavailable — keep token, try later
      }
    }
    loading.value = false
  }

  async function ssoLogin(): Promise<void> {
    if (!msalInstance || !msalReady) return
    ssoLoading.value = true
    ssoError.value = ''
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
          await msalInstance.loginRedirect(loginRequest)
          return
        }
        throw err
      }
      if (response?.idToken) await handleSsoResponse(response.idToken)
    } catch (err: unknown) {
      const authErr = err as { errorCode?: string; message?: string }
      ssoError.value = authErr.errorCode === 'user_cancelled' ? '' : authErr.message || 'SSO login failed'
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
    if (isAzureAdConfigured && msalInstance && msalReady) msalInstance.clearCache()
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
