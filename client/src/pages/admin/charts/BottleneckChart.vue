<script setup lang="ts">
import { computed } from 'vue'
import { Bar } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'
import './registerCharts'
import {
  AXIS_COLOR,
  AXIS_FONT_SIZE,
  GRID_COLOR,
  BAR_RADIUS,
  getCompletionColor,
} from './chartTheme'
import type { DrillDownPayload, BottleneckData } from './types'

const props = defineProps<{
  data: BottleneckData | null
  onDrillDown?: (payload: DrillDownPayload) => void
}>()

interface ChartRow {
  id: number
  name: string
  fullTitle: string
  pct: number
  count: number
  total: number
}

const chartData = computed<ChartRow[]>(() => {
  if (!props.data?.steps?.length) return []
  return props.data.steps.map((s) => ({
    id: s.id,
    name: s.title.length > 30 ? s.title.slice(0, 28) + '...' : s.title,
    fullTitle: s.title,
    pct: s.completion_pct,
    count: s.completed_count,
    total: props.data!.totalStudents,
  }))
})

const barData = computed<ChartData<'bar'>>(() => ({
  labels: chartData.value.map((d) => d.name),
  datasets: [
    {
      label: 'Completion',
      data: chartData.value.map((d) => d.pct),
      backgroundColor: chartData.value.map((d) => getCompletionColor(d.pct)),
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
  layout: { padding: { left: 10, right: 20, top: 5, bottom: 5 } },
  onClick: (_evt, elements) => {
    if (elements.length > 0) {
      const row = chartData.value[elements[0].index]
      if (row) props.onDrillDown?.({ filterType: 'step_not_completed', filterValue: row.id })
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
      ticks: {
        font: { size: AXIS_FONT_SIZE },
        color: AXIS_COLOR,
        callback: (value) => `${value}%`,
      },
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
        title: (items) => {
          const row = chartData.value[items[0].dataIndex]
          return row?.fullTitle ?? ''
        },
        label: (item) => {
          const row = chartData.value[item.dataIndex]
          return row ? `${row.count}/${row.total} (${row.pct}%)` : ''
        },
      },
    },
  },
}))

const ariaLabel = computed(
  () =>
    `Bottleneck bar chart. ${chartData.value
      .map((d) => `${d.fullTitle}: ${d.count} of ${d.total} students (${d.pct}%)`)
      .join('. ')}`,
)
</script>

<template>
  <p v-if="!data?.steps?.length" class="font-body text-sm text-csub-gray text-center py-4">
    No data
  </p>
  <div v-else class="h-64" role="img" :aria-label="ariaLabel">
    <Bar :data="barData" :options="options" aria-hidden="true" />
    <table class="sr-only">
      <caption>
        Bottleneck steps
      </caption>
      <thead>
        <tr>
          <th scope="col">Step</th>
          <th scope="col">Completed</th>
          <th scope="col">Percent</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in chartData" :key="row.id">
          <th scope="row">{{ row.fullTitle }}</th>
          <td>{{ row.count }} of {{ row.total }}</td>
          <td>{{ row.pct }}%</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
