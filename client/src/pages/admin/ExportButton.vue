<script setup lang="ts">
import { ref } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'
import { useToastStore } from '../../stores/toast'

const props = defineProps<{
  api: AdminApi
  termId: number | null
}>()

const toast = useToastStore()
const loading = ref(false)

const handleExport = async () => {
  loading.value = true
  try {
    const res = await props.api.raw(`/export/progress?term_id=${props.termId}&format=csv`)
    if (!res.ok) throw new Error('Export failed')
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `progress-export-${props.termId}-${new Date().toISOString().slice(0, 10)}.csv`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  } catch {
    toast.error('Could not export data. Please try again.')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <button
    @click="handleExport"
    :disabled="loading"
    class="flex items-center gap-1.5 font-body text-xs text-csub-blue hover:text-csub-blue-dark border border-csub-blue/20 rounded-lg px-3 py-1.5 hover:bg-csub-blue/5 transition-colors disabled:opacity-50"
  >
    <svg
      class="w-3.5 h-3.5"
      fill="none"
      viewBox="0 0 24 24"
      stroke="currentColor"
      :stroke-width="2"
    >
      <path
        stroke-linecap="round"
        stroke-linejoin="round"
        d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
      />
    </svg>
    {{ loading ? 'Exporting...' : 'Export CSV' }}
  </button>
</template>
