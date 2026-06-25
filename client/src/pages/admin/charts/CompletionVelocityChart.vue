<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { Bar } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'
import './registerCharts'
import type { AdminApi } from '../../../composables/useAdminApi'
import { AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR, BAR_RADIUS, VELOCITY_COLORS } from './chartTheme'
import type { DrillDownPayload } from './types'

interface VelocityItem {
  bucket: string
  student_count: number
}

const props = defineProps<{
  termId: number | null
  api: AdminApi
  onDrillDown?: (payload: DrillDownPayload) => void
}>()

const data = ref<VelocityItem[]>([])
const loading = ref(true)

watch(
  () => [props.termId, props.api] as const,
  () => {
    const fetchData = async () => {
      try {
        const result = await props.api.get<VelocityItem[]>('/analytics/completion-velocity', {
          term_id: props.termId,
        })
        data.value = result
      } catch (err) {
        console.error('[completion-velocity]', err)
      } finally {
        loading.value = false
      }
    }
    if (props.termId) fetchData()
  },
  { immediate: true },
)

const barData = computed<ChartData<'bar'>>(() => ({
  labels: data.value.map((d) => d.bucket),
  datasets: [
    {
      label: 'student_count',
      data: data.value.map((d) => d.student_count),
      backgroundColor: data.value.map(
        (_entry, index) => VELOCITY_COLORS[index % VELOCITY_COLORS.length],
      ),
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
      if (row) props.onDrillDown?.({ filterType: 'velocity_bucket', filterValue: row.bucket })
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
      ticks: { font: { size: AXIS_FONT_SIZE }, color: AXIS_COLOR, precision: 0 },
      grid: { color: GRID_COLOR },
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
        label: (item) => `${item.parsed.y} students`,
      },
    },
  },
}))

const ariaLabel = computed(
  () =>
    `Completion velocity bar chart by time to progress. ${data.value
      .map((d) => `${d.bucket}: ${d.student_count} students`)
      .join('. ')}`,
)
</script>

<template>
  <div v-if="loading" class="h-40 bg-gray-50 rounded-xl animate-pulse" />
  <div v-else class="bg-white border border-gray-200 rounded-xl shadow-xs p-5">
    <div class="mb-4">
      <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
        Completion Velocity
      </h3>
      <p class="font-body text-xs text-csub-gray mt-1">
        How quickly students progress from first to most recent completion. Longer times may
        indicate friction in the process.
      </p>
    </div>

    <div style="height: 300px" role="img" :aria-label="ariaLabel">
      <Bar :data="barData" :options="options" aria-hidden="true" />
      <table class="sr-only">
        <caption>
          Completion velocity by time to progress
        </caption>
        <thead>
          <tr>
            <th scope="col">Time to progress</th>
            <th scope="col">Students</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in data" :key="row.bucket">
            <th scope="row">{{ row.bucket }}</th>
            <td>{{ row.student_count }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
