<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { isAzureAdConfigured, msalInstance, loginRequest } from '../../auth/msalConfig'
import { BrowserAuthError } from '@azure/msal-browser'

interface AdminUser {
  id: number
  email: string
  displayName: string
  role: string
}

const emit = defineEmits<{
  (e: 'login', token: string, user: AdminUser): void
}>()

async function sendIdTokenToServer(idToken: string): Promise<{ token: string; user: AdminUser }> {
  const res = await fetch('/api/admin/auth/sso', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ idToken }),
  })
  if (!res.ok) {
    const data = await res.json().catch(() => ({}))
    throw new Error(data.error || 'SSO login failed')
  }
  return res.json()
}

// Email/password state (used when Azure AD not configured)
const email = ref('')
const password = ref('')
const error = ref('')
const loading = ref(false)

// SSO state
const ssoLoading = ref(false)
const msalInitialized = ref(false)
let msalReady = false
let redirecting = false

// Handle SSO response (popup or redirect)
const handleSsoResponse = async (idToken: string) => {
  const data = await sendIdTokenToServer(idToken)
  emit('login', data.token, data.user)
}

// Initialize MSAL on mount
onMounted(async () => {
  if (!isAzureAdConfigured || !msalInstance) {
    msalInitialized.value = true
    return
  }

  try {
    await msalInstance.initialize()
    msalReady = true

    const redirectResponse = await msalInstance.handleRedirectPromise()
    if (redirectResponse?.idToken) {
      ssoLoading.value = true
      await handleSsoResponse(redirectResponse.idToken)
      return // Redirect login succeeded
    }
  } catch (err: any) {
    error.value = err.message || 'SSO initialization failed'
  }
  msalInitialized.value = true
})

// SSO login — popup with redirect fallback
const handleSsoLogin = async () => {
  if (!msalInstance || !msalReady) return
  ssoLoading.value = true
  error.value = ''
  try {
    let response
    try {
      response = await msalInstance.loginPopup(loginRequest)
    } catch (err: any) {
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
    if (response?.idToken) {
      await handleSsoResponse(response.idToken)
    }
  } catch (err: any) {
    if (err.errorCode === 'user_cancelled') {
      error.value = ''
    } else {
      error.value = err.message || 'SSO login failed'
    }
  } finally {
    if (!redirecting) ssoLoading.value = false
  }
}

// Email/password login (when Azure AD not configured)
const handlePasswordLogin = async () => {
  error.value = ''
  loading.value = true
  try {
    const res = await fetch('/api/admin/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: email.value, password: password.value }),
    })
    const data = await res.json()
    if (res.ok && data.token) {
      emit('login', data.token, data.user)
    } else {
      error.value = data.error || 'Invalid credentials.'
    }
  } catch {
    error.value = 'Cannot connect to server.'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="min-h-screen flex items-center justify-center bg-gray-50">
    <div class="max-w-sm w-full mx-auto px-6">
      <h1
        class="font-display text-2xl font-bold text-csub-blue-dark uppercase tracking-wide mb-2 text-center"
      >
        Admin Portal
      </h1>
      <p class="font-body text-csub-gray text-sm mb-6 text-center">
        {{
          isAzureAdConfigured
            ? 'Sign in with your CSUB account.'
            : 'Sign in with your admin credentials.'
        }}
      </p>

      <!-- SSO login -->
      <div v-if="isAzureAdConfigured" class="space-y-4">
        <button
          @click="handleSsoLogin"
          :disabled="ssoLoading || !msalInitialized"
          class="w-full bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-6 py-3 rounded-lg shadow transition-colors duration-200 disabled:opacity-50 flex items-center justify-center gap-2"
        >
          <template v-if="ssoLoading">
            <svg class="animate-spin h-4 w-4" viewBox="0 0 24 24" fill="none">
              <circle
                class="opacity-25"
                cx="12"
                cy="12"
                r="10"
                stroke="currentColor"
                stroke-width="4"
              />
              <path
                class="opacity-75"
                fill="currentColor"
                d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
              />
            </svg>
            Signing in...
          </template>
          <template v-else> Sign in with CSUB Account </template>
        </button>
        <p v-if="error" class="text-red-600 text-sm font-body text-center">{{ error }}</p>
      </div>

      <!-- Email/password login -->
      <form v-else @submit.prevent="handlePasswordLogin" class="space-y-4">
        <input
          type="email"
          required
          v-model="email"
          placeholder="Email"
          autocomplete="email"
          class="w-full px-4 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
        />
        <input
          type="password"
          required
          v-model="password"
          placeholder="Password"
          autocomplete="current-password"
          class="w-full px-4 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
        />
        <p v-if="error" class="text-red-600 text-sm font-body">{{ error }}</p>
        <button
          type="submit"
          :disabled="loading"
          class="w-full bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-6 py-3 rounded-lg shadow transition-colors duration-200 disabled:opacity-50"
        >
          {{ loading ? 'Signing in...' : 'Sign In' }}
        </button>
      </form>
    </div>
  </div>
</template>
