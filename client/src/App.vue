<script setup lang="ts">
import { onMounted, onErrorCaptured, ref } from 'vue'
import { useAuthStore } from './stores/auth'
import ToastContainer from './components/ToastContainer.vue'

// Initialize auth (MSAL + existing-token validation) once on app start.
const auth = useAuthStore()

const renderError = ref(false)

onMounted(() => {
  auth.init()
})

// Error boundary: a render/lifecycle error shows a fallback instead of a blank screen.
// Deliberately NO toast here: ToastContainer stays mounted in the fallback, so a toast
// whose own render throws would re-enter this handler in a loop. The fallback screen
// is the message.
onErrorCaptured((err) => {
  console.error('[app error boundary]', err)
  renderError.value = true
  return false
})

function reload() {
  window.location.reload()
}
</script>

<template>
  <ToastContainer />
  <div
    v-if="renderError"
    class="min-h-screen flex flex-col items-center justify-center gap-4 bg-white text-center px-6"
  >
    <h1 class="font-display text-2xl text-csub-blue-dark">Something went wrong</h1>
    <p class="font-body text-csub-gray">
      Please reload the page. If the problem persists, contact CSUB Admissions.
    </p>
    <button
      type="button"
      class="bg-csub-blue text-white font-body font-semibold rounded-lg px-5 py-2"
      @click="reload"
    >
      Reload
    </button>
  </div>
  <router-view v-else />
</template>
