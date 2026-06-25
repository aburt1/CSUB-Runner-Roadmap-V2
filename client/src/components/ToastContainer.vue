<script setup lang="ts">
import { storeToRefs } from 'pinia'
import { useToastStore } from '../stores/toast'

const toast = useToastStore()
const { toasts } = storeToRefs(toast)

const styles: Record<string, string> = {
  error: 'bg-red-600 text-white',
  success: 'bg-green-600 text-white',
  info: 'bg-csub-blue text-white',
}
</script>

<template>
  <div class="fixed top-4 right-4 z-9999 flex flex-col gap-2 max-w-sm">
    <!-- Errors announce assertively (role=alert); info/success stay polite. -->
    <div
      v-for="t in toasts"
      :key="t.id"
      :role="t.type === 'error' ? 'alert' : 'status'"
      :aria-live="t.type === 'error' ? 'assertive' : 'polite'"
      :class="[
        'flex items-start gap-3 rounded-lg shadow-lg px-4 py-3 font-body text-sm',
        styles[t.type],
      ]"
    >
      <span class="flex-1">{{ t.message }}</span>
      <button
        type="button"
        class="shrink-0 opacity-80 hover:opacity-100 font-semibold"
        aria-label="Dismiss"
        @click="toast.dismiss(t.id)"
      >
        ✕
      </button>
    </div>
  </div>
</template>
