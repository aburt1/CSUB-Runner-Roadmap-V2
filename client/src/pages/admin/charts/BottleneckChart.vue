<script setup lang="ts">
import { computed } from 'vue';
import { Bar } from 'vue-chartjs';
import type { ChartData, ChartOptions } from 'chart.js';
import './registerCharts';
import { AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR, BAR_RADIUS, getCompletionColor } from './chartTheme';

interface StepData {
  id: number;
  title: string;
  completion_pct: number;
  completed_count: number;
}

interface BottleneckData {
  steps: StepData[];
  totalStudents: number;
}

interface DrillDownPayload {
  filterType: string;
  filterValue: any;
}

const props = defineProps<{
  data: BottleneckData | null;
  onDrillDown?: (payload: DrillDownPayload) => void;
}>();

interface ChartRow {
  id: number;
  name: string;
  fullTitle: string;
  pct: number;
  count: number;
  total: number;
}

const chartData = computed<ChartRow[]>(() => {
  if (!props.data?.steps?.length) return [];
  return props.data.steps.map((s) => ({
    id: s.id,
    name: s.title.length > 30 ? s.title.slice(0, 28) + '...' : s.title,
    fullTitle: s.title,
    pct: s.completion_pct,
    count: s.completed_count,
    total: props.data!.totalStudents,
  }));
});

const barData = computed<ChartData<'bar'>>(() => ({
  labels: chartData.value.map((d) => d.name),
  datasets: [
    {
      label: 'Completion',
      data: chartData.value.map((d) => d.pct),
      backgroundColor: chartData.value.map((d) => getCompletionColor(d.pct)),
      borderRadius: { topLeft: BAR_RADIUS[0], topRight: BAR_RADIUS[1], bottomRight: BAR_RADIUS[2], bottomLeft: BAR_RADIUS[3] },
      borderSkipped: false,
    },
  ],
}));

const options = computed<ChartOptions<'bar'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  layout: { padding: { left: 10, right: 20, top: 5, bottom: 5 } },
  onClick: (_evt, elements) => {
    if (elements.length > 0) {
      const row = chartData.value[elements[0].index];
      if (row) props.onDrillDown?.({ filterType: 'step_not_completed', filterValue: row.id });
    }
  },
  onHover: (evt, elements) => {
    const target = evt.native?.target as HTMLElement | undefined;
    if (target) target.style.cursor = elements.length > 0 ? 'pointer' : 'default';
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
          const row = chartData.value[items[0].dataIndex];
          return row?.fullTitle ?? '';
        },
        label: (item) => {
          const row = chartData.value[item.dataIndex];
          return row ? `${row.count}/${row.total} (${row.pct}%)` : '';
        },
      },
    },
  },
}));
</script>

<template>
  <p v-if="!data?.steps?.length" class="font-body text-sm text-csub-gray text-center py-4">No data</p>
  <div v-else class="h-64">
    <Bar :data="barData" :options="options" />
  </div>
</template>
