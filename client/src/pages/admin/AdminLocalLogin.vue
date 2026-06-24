<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

const username = ref('')
const password = ref('')
const error = ref('')
const loading = ref(false)
const router = useRouter()

const handleSubmit = async () => {
  error.value = ''
  loading.value = true
  try {
    const res = await fetch('/api/admin/auth/local-login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: username.value, password: password.value }),
    })
    const data = await res.json()
    if (res.ok && data.token) {
      sessionStorage.setItem('csub_admin_token', data.token)
      sessionStorage.setItem('csub_admin_user', JSON.stringify(data.user))
      router.push('/admin')
    } else {
      // Fallback is byte-identical to the backend contract string (no trailing period)
      // so the form never flickers between two near-identical messages.
      error.value = data.error || 'Invalid credentials'
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
        Local Admin Login
      </h1>
      <p class="font-body text-csub-gray text-sm mb-6 text-center">Emergency access only.</p>
      <form @submit.prevent="handleSubmit" class="space-y-4">
        <div>
          <label for="local-username" class="sr-only">Username</label>
          <input
            id="local-username"
            type="text"
            required
            v-model="username"
            placeholder="Username"
            autocomplete="username"
            :aria-invalid="error ? 'true' : undefined"
            :aria-describedby="error ? 'local-login-error' : undefined"
            class="w-full px-4 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
          />
        </div>
        <div>
          <label for="local-password" class="sr-only">Password</label>
          <input
            id="local-password"
            type="password"
            required
            v-model="password"
            placeholder="Password"
            autocomplete="current-password"
            :aria-invalid="error ? 'true' : undefined"
            :aria-describedby="error ? 'local-login-error' : undefined"
            class="w-full px-4 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
          />
        </div>
        <p v-if="error" id="local-login-error" role="alert" class="text-red-600 text-sm font-body">
          {{ error }}
        </p>
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
