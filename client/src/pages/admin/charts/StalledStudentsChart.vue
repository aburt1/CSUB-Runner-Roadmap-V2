<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { Bar } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'
import './registerCharts'
import type { AdminApi } from '../../../composables/useAdminApi'
import { COLOR_DANGER, AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR, BAR_RADIUS } from './chartTheme'

interface StalledStudent {
  id: number
  last_completion_date: string | null
}

interface BucketItem {
  bucket: string
  student_count: number
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

const data = ref<BucketItem[]>([])
const loading = ref(true)

watch(
  () => [props.termId, props.api] as const,
  () => {
    const fetchData = async () => {
      try {
        const students = await props.api.get<StalledStudent[]>('/analytics/stalled-students', {
          term_id: props.termId,
          days: 7,
        })

        // Group into buckets
        const buckets: Record<string, number> = {
          '7-14 days': 0,
          '2-4 weeks': 0,
          '1-3 months': 0,
          '3+ months': 0,
        }

        const now = Date.now()
        for (const student of students) {
          if (!student.last_completion_date) {
            buckets['3+ months']!++
            continue
          }
          const daysInactive = Math.floor(
            (now - new Date(student.last_completion_date).getTime()) / (1000 * 60 * 60 * 24),
          )
          if (daysInactive <= 14) buckets['7-14 days']!++
          else if (daysInactive <= 28) buckets['2-4 weeks']!++
          else if (daysInactive <= 90) buckets['1-3 months']!++
          else buckets['3+ months']!++
        }

        data.value = Object.entries(buckets).map(([bucket, count]) => ({
          bucket,
          student_count: count,
        }))
      } catch (err) {
        console.error('[stalled-students]', err)
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
      backgroundColor: COLOR_DANGER,
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
      if (row) props.onDrillDown?.({ filterType: 'stalled', filterValue: row.bucket })
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
</script>

<template>
  <div v-if="loading" class="h-40 bg-gray-50 rounded-xl animate-pulse" />
  <div v-else class="bg-white border border-gray-200 rounded-xl shadow-sm p-5">
    <div class="mb-4">
      <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
        Stalled Students
      </h3>
      <p class="font-body text-xs text-csub-gray mt-1">
        Students with no new completions in 7+ days. These students may need outreach to continue
        their admissions journey.
      </p>
    </div>

    <div style="height: 300px">
      <Bar :data="barData" :options="options" />
    </div>
  </div>
</template>
