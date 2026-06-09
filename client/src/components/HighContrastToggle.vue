<script setup lang="ts">
import { ref, watch } from 'vue'

const enabled = ref<boolean>(localStorage.getItem('csub_high_contrast') === 'true')

watch(
  enabled,
  (value) => {
    document.documentElement.setAttribute('data-high-contrast', value ? 'true' : 'false')
    localStorage.setItem('csub_high_contrast', value ? 'true' : 'false')
  },
  { immediate: true },
)
</script>

<template>
  <button
    @click="enabled = !enabled"
    :class="`flex items-center gap-1.5 text-xs font-body px-2 py-1 rounded-lg transition-colors ${
      enabled ? 'bg-white/20 text-white' : 'text-white/50 hover:text-white/80'
    }`"
    :aria-pressed="enabled"
    :aria-label="enabled ? 'Disable high contrast mode' : 'Enable high contrast mode'"
    title="Toggle high contrast"
  >
    <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
      <path
        stroke-linecap="round"
        stroke-linejoin="round"
        d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z"
      />
    </svg>
    {{ enabled ? 'HC' : 'HC' }}
  </button>
</template>
