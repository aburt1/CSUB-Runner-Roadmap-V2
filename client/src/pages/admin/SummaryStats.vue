<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'
import { useToastStore } from '../../stores/toast'

interface StatsData {
  totalStudents: number
  avgCompletionPercent: number
  totalActiveSteps: number
}

interface Props {
  api: AdminApi
  termId: number | null
}

const props = defineProps<Props>()

const stats = ref<StatsData | null>(null)
const toast = useToastStore()

watch(
  () => [props.api, props.termId] as const,
  () => {
    const query = props.termId ? `/stats?term_id=${props.termId}` : '/stats'
    props.api
      .get<StatsData>(query)
      .then((data) => {
        stats.value = data
      })
      .catch(() => toast.error('Could not load summary stats.'))
  },
  { immediate: true },
)

// Suppress gold color on 0% — only highlight when there's meaningful progress.
// Total Students and Active Steps always use their static color.
const avgColor = computed(() =>
  stats.value?.avgCompletionPercent === 0 ? 'text-csub-gray' : 'text-csub-gold',
)
</script>

<template>
  <div v-if="stats" class="grid grid-cols-3 gap-4 md:grid-cols-[1fr_2fr] md:gap-6 mb-6">
    <!-- Left column: first stat — aligns with student list -->
    <div class="bg-white border border-gray-200 rounded-xl shadow-xs px-4 py-4 text-center">
      <p class="font-display text-2xl font-bold text-csub-blue-dark">
        {{ stats.totalStudents }}
      </p>
      <p class="font-body text-xs text-csub-gray mt-1 uppercase tracking-wide">Total Students</p>
    </div>
    <!-- Right column: remaining stats — aligns with detail panel -->
    <div class="col-span-2 md:col-span-1 grid grid-cols-2 gap-4 md:gap-6">
      <div class="bg-white border border-gray-200 rounded-xl shadow-xs px-4 py-4 text-center">
        <p :class="`font-display text-2xl font-bold ${avgColor}`">
          {{ `${stats.avgCompletionPercent}%` }}
        </p>
        <p class="font-body text-xs text-csub-gray mt-1 uppercase tracking-wide">Avg. Completion</p>
      </div>
      <div class="bg-white border border-gray-200 rounded-xl shadow-xs px-4 py-4 text-center">
        <p class="font-display text-2xl font-bold text-csub-blue">
          {{ stats.totalActiveSteps }}
        </p>
        <p class="font-body text-xs text-csub-gray mt-1 uppercase tracking-wide">Active Steps</p>
      </div>
    </div>
  </div>
</template>
