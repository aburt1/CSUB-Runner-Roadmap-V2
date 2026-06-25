<script setup lang="ts">
import { storeToRefs } from 'pinia'
import { useAuthStore } from '../stores/auth'
import RoadmapPage from '../pages/RoadmapPage.vue'
import PublicRoadmapPreview from '../components/PublicRoadmapPreview.vue'

// Show the roadmap when signed in, otherwise the public preview.
const auth = useAuthStore()
const { loading, isAuthenticated } = storeToRefs(auth)
</script>

<template>
  <div
    v-if="loading"
    role="status"
    aria-label="Loading"
    class="min-h-screen flex items-center justify-center bg-white"
  >
    <div
      class="w-12 h-12 border-4 border-csub-blue/20 border-t-csub-blue rounded-full animate-spin"
      aria-hidden="true"
    ></div>
    <span class="sr-only">Loading...</span>
  </div>
  <template v-else-if="isAuthenticated">
    <a href="#main-content" class="skip-link">Skip to main content</a>
    <RoadmapPage />
  </template>
  <PublicRoadmapPreview v-else :on-login="auth.devLogin" />
</template>
