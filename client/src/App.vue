<script setup lang="ts">
import { onMounted } from 'vue'
import { useAuthStore } from './stores/auth'

// Initialize auth (MSAL + existing-token validation) once on app start.
const auth = useAuthStore()
onMounted(() => {
  auth.init()
})
</script>

<template>
  <router-view />
</template>
