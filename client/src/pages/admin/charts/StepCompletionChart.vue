<script setup lang="ts">
import { computed } from 'vue'
import { Bar } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'
import './registerCharts'
import { CSUB_BLUE, CSUB_GOLD, AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR } from './chartTheme'

interface StepData {
  id: number
  title: string
  completed_count: number
}

interface CompletionData {
  steps: StepData[]
  totalStudents: number
}

interface DrillDownPayload {
  filterType: string
  filterValue: any
}

const props = defineProps<{
  data: CompletionData | null
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
    name: s.title.length > 28 ? s.title.slice(0, 26) + '...' : s.title,
    fullTitle: s.title,
    pct:
      props.data!.totalStudents > 0
        ? Math.round((s.completed_count / props.data!.totalStudents) * 100)
        : 0,
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
      backgroundColor: chartData.value.map((d) => (d.pct === 100 ? CSUB_GOLD : CSUB_BLUE)),
      borderRadius: { topLeft: 0, topRight: 4, bottomRight: 4, bottomLeft: 0 },
      borderSkipped: false,
    },
  ],
}))

const options = computed<ChartOptions<'bar'>>(() => ({
  indexAxis: 'y',
  responsive: true,
  maintainAspectRatio: false,
  layout: { padding: { left: 10, right: 20, top: 5, bottom: 5 } },
  onClick: (_evt, elements) => {
    if (elements.length > 0) {
      const row = chartData.value[elements[0].index]
      if (row) props.onDrillDown?.({ filterType: 'step_completed', filterValue: row.id })
    }
  },
  onHover: (evt, elements) => {
    const target = evt.native?.target as HTMLElement | undefined
    if (target) target.style.cursor = elements.length > 0 ? 'pointer' : 'default'
  },
  scales: {
    x: {
      type: 'linear',
      min: 0,
      max: 100,
      ticks: {
        font: { size: AXIS_FONT_SIZE },
        color: AXIS_COLOR,
        callback: (value) => `${value}%`,
      },
      grid: { color: GRID_COLOR },
    },
    y: {
      type: 'category',
      ticks: { font: { size: AXIS_FONT_SIZE }, color: AXIS_COLOR },
      grid: { display: false },
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
</script>

<template>
  <p v-if="!data?.steps?.length" class="font-body text-sm text-csub-gray text-center py-4">
    No data
  </p>
  <div v-else class="h-80">
    <Bar :data="barData" :options="options" />
  </div>
</template>
