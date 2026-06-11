<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { Bar } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'
import './registerCharts'
import type { AdminApi } from '../../../composables/useAdminApi'
import { CSUB_BLUE, AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR, BAR_RADIUS } from './chartTheme'
import type { DrillDownPayload } from './types'

interface CohortItem {
  tag: string
  avg_completion_pct: number
}

const props = defineProps<{
  termId: number | null
  api: AdminApi
  onDrillDown?: (payload: DrillDownPayload) => void
}>()

const data = ref<CohortItem[]>([])
const loading = ref(true)

watch(
  () => [props.termId, props.api] as const,
  () => {
    const fetchData = async () => {
      try {
        const result = await props.api.get<CohortItem[]>('/analytics/cohort-comparison', {
          term_id: props.termId,
        })
        data.value = result
      } catch (err) {
        console.error('[cohort-comparison]', err)
      } finally {
        loading.value = false
      }
    }
    if (props.termId) fetchData()
  },
  { immediate: true },
)

const barData = computed<ChartData<'bar'>>(() => ({
  labels: data.value.map((d) => d.tag),
  datasets: [
    {
      label: 'avg_completion_pct',
      data: data.value.map((d) => d.avg_completion_pct),
      backgroundColor: CSUB_BLUE,
      borderRadius: {
        topLeft: BAR_RADIUS[0],
        topRight: BAR_RADIUS[1],
        bottomRight: BAR_RADIUS[2],
        bottomLeft: BAR_RADIUS[3],
      },
      borderSkipped: false,
    },
  ],
}))

const options = computed<ChartOptions<'bar'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  layout: { padding: { top: 20, right: 30, left: 0, bottom: 50 } },
  onClick: (_evt, elements) => {
    if (elements.length > 0) {
      const row = data.value[elements[0].index]
      if (row) props.onDrillDown?.({ filterType: 'tag', filterValue: row.tag })
    }
  },
  onHover: (evt, elements) => {
    const target = evt.native?.target as HTMLElement | undefined
    if (target) target.style.cursor = elements.length > 0 ? 'pointer' : 'default'
  },
  scales: {
    x: {
      ticks: {
        font: { size: AXIS_FONT_SIZE },
        color: AXIS_COLOR,
        autoSkip: false,
        maxRotation: 15,
        minRotation: 15,
      },
      grid: { color: GRID_COLOR },
    },
    y: {
      min: 0,
      max: 100,
      ticks: { font: { size: AXIS_FONT_SIZE }, color: AXIS_COLOR },
      grid: { color: GRID_COLOR },
      title: { display: true, text: '%' },
    },
  },
  plugins: {
    legend: { display: false },
    tooltip: {
      backgroundColor: '#fff',
      borderColor: GRID_COLOR,
      borderWidth: 1,
      titleColor: '#374151',
      bodyColor: '#374151',
      titleFont: { size: 12 },
      bodyFont: { size: 12 },
      callbacks: {
        label: (item) => `${item.parsed.y}%`,
      },
    },
  },
}))
</script>

<template>
  <div v-if="loading" class="h-40 bg-gray-50 rounded-xl animate-pulse" />
  <div v-else class="bg-white border border-gray-200 rounded-xl shadow-sm p-5">
    <div class="mb-4">
      <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
        Cohort Comparison
      </h3>
      <p class="font-body text-xs text-csub-gray mt-1">
        Average completion rate by student population. Identify groups that may need targeted
        outreach.
      </p>
    </div>

    <div style="height: 300px">
      <Bar :data="barData" :options="options" />
    </div>
  </div>
</template>
