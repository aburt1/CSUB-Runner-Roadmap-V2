<script setup lang="ts">
import { ref, watch } from 'vue'
import type { AdminApi } from '../../../composables/useAdminApi'

interface RiskStep {
  id: number
  title: string
  deadline_date: string
  at_risk_count: number
}

interface DrillDownPayload {
  filterType: string
  filterValue: any
}

const props = defineProps<{
  termId: number | null
  api: AdminApi
  onDrillDown?: (payload: DrillDownPayload) => void
}>()

const data = ref<RiskStep[]>([])
const loading = ref(true)

watch(
  () => [props.termId, props.api] as const,
  () => {
    const fetchData = async () => {
      try {
        const result = await props.api.get<RiskStep[]>('/analytics/deadline-risk', {
          term_id: props.termId,
          days: 14,
        })
        data.value = result
      } catch (err) {
        console.error('[deadline-risk]', err)
      } finally {
        loading.value = false
      }
    }
    if (props.termId) fetchData()
  },
  { immediate: true },
)

function formatDeadline(date: string): string {
  return new Date(date).toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  })
}
</script>

<template>
  <div v-if="loading" class="h-40 bg-gray-50 rounded-xl animate-pulse" />
  <div v-else class="bg-white border border-gray-200 rounded-xl shadow-sm p-5">
    <div class="mb-4">
      <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
        Deadline Risk
      </h3>
      <p class="font-body text-xs text-csub-gray mt-1">
        Steps with deadlines in the next 14 days and students who haven't completed them yet.
      </p>
    </div>

    <p v-if="data.length === 0" class="font-body text-sm text-csub-gray">
      No steps with upcoming deadlines.
    </p>
    <div v-else class="overflow-x-auto">
      <table class="w-full text-sm font-body">
        <thead>
          <tr class="border-b border-gray-200">
            <th
              class="text-left py-2 px-3 font-semibold text-csub-blue-dark uppercase text-xs tracking-wide"
            >
              Step
            </th>
            <th
              class="text-left py-2 px-3 font-semibold text-csub-blue-dark uppercase text-xs tracking-wide"
            >
              Deadline
            </th>
            <th
              class="text-center py-2 px-3 font-semibold text-csub-blue-dark uppercase text-xs tracking-wide"
            >
              At Risk
            </th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="step in data"
            :key="step.id"
            class="border-b border-gray-100 hover:bg-gray-50 cursor-pointer"
            @click="onDrillDown?.({ filterType: 'deadline_risk', filterValue: step.id })"
          >
            <td class="py-2.5 px-3 text-csub-blue-dark">{{ step.title }}</td>
            <td class="py-2.5 px-3 text-csub-gray">
              {{ formatDeadline(step.deadline_date) }}
            </td>
            <td class="py-2.5 px-3 text-center">
              <span class="bg-red-100 text-red-700 font-semibold px-2 py-0.5 rounded-full text-xs">
                {{ step.at_risk_count }} {{ step.at_risk_count === 1 ? 'student' : 'students' }}
              </span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
