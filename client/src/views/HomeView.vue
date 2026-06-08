<script setup lang="ts">
import { storeToRefs } from 'pinia'
import { useAuthStore } from '../stores/auth'
import RoadmapPage from '../pages/RoadmapPage.vue'
import PublicRoadmapPreview from '../components/PublicRoadmapPreview.vue'

// Port of the old App.tsx: show the roadmap when signed in, otherwise the public preview.
const auth = useAuthStore()
const { loading, isAuthenticated } = storeToRefs(auth)
</script>

<template>
  <div v-if="loading" class="min-h-screen flex items-center justify-center bg-white">
    <div class="text-csub-blue font-display text-xl">Loading…</div>
  </div>
  <RoadmapPage v-else-if="isAuthenticated" />
  <PublicRoadmapPreview v-else />
</template>
