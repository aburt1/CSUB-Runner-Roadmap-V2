<script setup lang="ts">
import { computed } from 'vue';
import { Bar } from 'vue-chartjs';
import type { ChartData, ChartOptions, Plugin } from 'chart.js';
import './registerCharts';
import { AXIS_COLOR, AXIS_FONT_SIZE, GRID_COLOR, BAR_RADIUS, PROGRESS_COLORS } from './chartTheme';

interface BucketItem {
  bucket: string;
  student_count: number;
}

interface DrillDownPayload {
  filterType: string;
  filterValue: any;
}

const props = defineProps<{
  data: BucketItem[] | null;
  onDrillDown?: (payload: DrillDownPayload) => void;
}>();

const BUCKET_ORDER = ['0%', '1-25%', '26-50%', '51-75%', '76-100%'];

interface ChartRow {
  name: string;
  value: number;
  color: string;
}

const chartData = computed<ChartRow[]>(() => {
  if (!props.data?.length) return [];
  const bucketMap = Object.fromEntries(props.data.map((d) => [d.bucket, d.student_count]));
  return BUCKET_ORDER.map((bucket, i) => ({
    name: bucket,
    value: bucketMap[bucket] || 0,
    color: PROGRESS_COLORS[i],
  }));
});

const barData = computed<ChartData<'bar'>>(() => ({
  labels: chartData.value.map((d) => d.name),
  datasets: [
    {
      label: 'Students',
      data: chartData.value.map((d) => d.value),
      backgroundColor: chartData.value.map((d) => d.color),
      borderRadius: { topLeft: BAR_RADIUS[0], topRight: BAR_RADIUS[1], bottomRight: BAR_RADIUS[2], bottomLeft: BAR_RADIUS[3] },
      borderSkipped: false,
    },
  ],
}));

// Reproduces recharts <LabelList position="top"> — draws the value above each bar.
const valueLabels: Plugin<'bar'> = {
  id: 'cohortValueLabels',
  afterDatasetsDraw(chart) {
    const { ctx } = chart;
    const meta = chart.getDatasetMeta(0);
    ctx.save();
    ctx.font = `${AXIS_FONT_SIZE}px sans-serif`;
    ctx.fillStyle = '#374151';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'bottom';
    meta.data.forEach((bar, i) => {
      const value = chartData.value[i]?.value;
      if (value == null) return;
      ctx.fillText(String(value), bar.x, bar.y - 4);
    });
    ctx.restore();
  },
};

const options = computed<ChartOptions<'bar'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  layout: { padding: { left: 10, right: 30, top: 20, bottom: 5 } },
  onClick: (_evt, elements) => {
    if (elements.length > 0) {
      const row = chartData.value[elements[0].index];
      if (row) props.onDrillDown?.({ filterType: 'cohort_bucket', filterValue: row.name });
    }
  },
  onHover: (evt, elements) => {
    const target = evt.native?.target as HTMLElement | undefined;
    if (target) target.style.cursor = elements.length > 0 ? 'pointer' : 'default';
  },
  scales: {
    x: {
      ticks: { font: { size: AXIS_FONT_SIZE }, color: AXIS_COLOR },
      grid: { display: false },
      title: { display: true, text: 'Completion %', font: { size: 10 }, color: AXIS_COLOR },
    },
    y: {
      ticks: { font: { size: AXIS_FONT_SIZE }, color: AXIS_COLOR, precision: 0 },
      grid: { color: GRID_COLOR },
      title: { display: true, text: 'Students', font: { size: 10 }, color: AXIS_COLOR },
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
        title: (items) => `Progress: ${items[0].label}`,
        label: (item) => `${item.parsed.y} students`,
      },
    },
  },
}));
</script>

<template>
  <p v-if="!data?.length" class="font-body text-sm text-csub-gray text-center py-4">No data</p>
  <div v-else class="h-64">
    <Bar :data="barData" :options="options" :plugins="[valueLabels]" />
  </div>
</template>
