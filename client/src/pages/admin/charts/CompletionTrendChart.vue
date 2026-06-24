<script setup lang="ts">
import { computed } from 'vue'
import { Line } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'
import './registerCharts'
import { CSUB_BLUE, CSUB_GOLD, AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR } from './chartTheme'
import type { DrillDownPayload, TrendPoint } from './types'

const props = defineProps<{
  data: TrendPoint[] | null
  onDrillDown?: (payload: DrillDownPayload) => void
}>()

interface ChartRow {
  date: string
  rawDate: string
  completions: number
}

const chartData = computed<ChartRow[]>(() => {
  if (!props.data?.length) return []
  return props.data.map((d) => ({
    date: new Date(d.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric' }),
    rawDate: d.date,
    completions: d.completions,
  }))
})

const lineData = computed<ChartData<'line'>>(() => ({
  labels: chartData.value.map((d) => d.date),
  datasets: [
    {
      label: 'completions',
      data: chartData.value.map((d) => d.completions),
      borderColor: CSUB_BLUE,
      borderWidth: 2,
      tension: 0.4,
      pointBackgroundColor: CSUB_GOLD,
      pointBorderColor: CSUB_GOLD,
      pointRadius: 4,
      pointHoverRadius: 6,
      pointHoverBackgroundColor: CSUB_GOLD,
      fill: false,
    },
  ],
}))

const options = computed<ChartOptions<'line'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  layout: { padding: { left: 0, right: 20, top: 5, bottom: 5 } },
  onClick: (_evt, elements) => {
    if (elements.length > 0) {
      const row = chartData.value[elements[0].index]
      if (row) props.onDrillDown?.({ filterType: 'trend_date', filterValue: row.rawDate })
    }
  },
  onHover: (evt, elements) => {
    const target = evt.native?.target as HTMLElement | undefined
    if (target) target.style.cursor = elements.length > 0 ? 'pointer' : 'default'
  },
  scales: {
    x: {
      ticks: { font: { size: AXIS_FONT_SIZE }, color: AXIS_COLOR },
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
        label: (item) => `${item.parsed.y} completions`,
      },
    },
  },
}))

const ariaLabel = computed(
  () =>
    `Completion trend line chart over time. ${chartData.value
      .map((d) => `${d.date}: ${d.completions} completions`)
      .join('. ')}`,
)
</script>

<template>
  <p v-if="!data?.length" class="font-body text-sm text-csub-gray text-center py-4">No data</p>
  <div v-else class="h-64" role="img" :aria-label="ariaLabel">
    <Line :data="lineData" :options="options" aria-hidden="true" />
    <table class="sr-only">
      <caption>
        Completions over time
      </caption>
      <thead>
        <tr>
          <th scope="col">Date</th>
          <th scope="col">Completions</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in chartData" :key="row.rawDate">
          <th scope="row">{{ row.date }}</th>
          <td>{{ row.completions }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
