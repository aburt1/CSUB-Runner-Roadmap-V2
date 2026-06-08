<script setup lang="ts">
import { ref, watch } from 'vue';
import type { AdminApi } from '../../composables/useAdminApi';
import SummaryStats from './SummaryStats.vue';
import StepCompletionChart from './charts/StepCompletionChart.vue';
import CompletionTrendChart from './charts/CompletionTrendChart.vue';
import BottleneckChart from './charts/BottleneckChart.vue';
import CohortDistributionChart from './charts/CohortDistributionChart.vue';
import DeadlineRiskChart from './charts/DeadlineRiskChart.vue';
import StalledStudentsChart from './charts/StalledStudentsChart.vue';
import CohortComparisonChart from './charts/CohortComparisonChart.vue';
import CompletionVelocityChart from './charts/CompletionVelocityChart.vue';
import ExportButton from './ExportButton.vue';
import StudentDrillDown from './StudentDrillDown.vue';

interface DrillDownState {
  filterType: string;
  filterValue: any;
}

const props = defineProps<{
  api: AdminApi;
  termId: number | null;
}>();

const stepCompletion = ref<any>(null);
const trend = ref<any>(null);
const bottlenecks = ref<any>(null);
const cohort = ref<any>(null);
const trendDays = ref(30);
const loading = ref(true);
const drillDown = ref<DrillDownState | null>(null);

function setDrillDown(payload: DrillDownState) {
  drillDown.value = payload;
}

function handlePrint() {
  window.print();
}

watch(
  () => [props.api, props.termId, trendDays.value] as const,
  () => {
    if (!props.termId) return;
    loading.value = true;
    const q = `term_id=${props.termId}`;
    Promise.all([
      props.api.get(`/analytics/step-completion?${q}`),
      props.api.get(`/analytics/completion-trend?${q}&days=${trendDays.value}`),
      props.api.get(`/analytics/bottlenecks?${q}`),
      props.api.get(`/analytics/cohort-summary?${q}`),
    ])
      .then(([sc, tr, bn, co]) => {
        stepCompletion.value = sc;
        trend.value = tr;
        bottlenecks.value = bn;
        cohort.value = co;
      })
      .catch(() => {})
      .finally(() => {
        loading.value = false;
      });
  },
  { immediate: true },
);
</script>

<template>
  <p v-if="loading" class="font-body text-sm text-csub-gray text-center py-8">Loading analytics...</p>

  <div v-else class="space-y-8">
    <div class="flex items-center justify-between">
      <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
        Analytics Dashboard
      </h2>
      <div class="flex items-center gap-3">
        <ExportButton :api="api" :termId="termId" />
        <button
          @click="handlePrint"
          class="font-body text-xs text-csub-blue hover:text-csub-blue-dark border border-csub-blue/20 rounded-lg px-3 py-1.5 hover:bg-csub-blue/5 transition-colors"
        >
          Print
        </button>
      </div>
    </div>

    <SummaryStats :api="api" :termId="termId" />

    <!-- Step Completion Rates -->
    <div class="bg-white rounded-xl border border-gray-200 shadow-sm p-5">
      <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-1">
        Step Completion Rates
      </h3>
      <p class="font-body text-xs text-csub-gray mb-4">Percentage of students who have completed each step</p>
      <StepCompletionChart :data="stepCompletion" :onDrillDown="setDrillDown" />
    </div>

    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- Completion Trend -->
      <div class="bg-white rounded-xl border border-gray-200 shadow-sm p-5">
        <div class="flex items-center justify-between mb-4">
          <div>
            <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide">
              Completion Trend
            </h3>
            <p class="font-body text-xs text-csub-gray mt-1">How many students are completing steps over time</p>
          </div>
          <div class="flex gap-1">
            <button
              v-for="d in [7, 30, 90]"
              :key="d"
              @click="trendDays = d"
              :class="`font-body text-xs px-2.5 py-1 rounded-lg transition-colors ${
                trendDays === d
                  ? 'bg-csub-blue text-white'
                  : 'text-csub-gray hover:bg-gray-100'
              }`"
            >
              {{ d }}d
            </button>
          </div>
        </div>
        <CompletionTrendChart :data="trend" :onDrillDown="setDrillDown" />
      </div>

      <!-- Bottlenecks -->
      <div class="bg-white rounded-xl border border-gray-200 shadow-sm p-5">
        <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-1">
          Bottleneck Steps
        </h3>
        <p class="font-body text-xs text-csub-gray mb-4">Steps with the lowest completion rates — these may need attention or clearer instructions</p>
        <BottleneckChart :data="bottlenecks" :onDrillDown="setDrillDown" />
      </div>
    </div>

    <!-- Cohort Distribution -->
    <div class="bg-white rounded-xl border border-gray-200 shadow-sm p-5">
      <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-1">
        Student Progress Distribution
      </h3>
      <p class="font-body text-xs text-csub-gray mb-4">How students are distributed across completion percentages</p>
      <CohortDistributionChart :data="cohort" :onDrillDown="setDrillDown" />
    </div>

    <!-- Admissions Outreach Analytics -->
    <div class="border-t border-gray-300 pt-8">
      <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide mb-6">
        Outreach & Action Items
      </h2>

      <!-- Deadline Risk -->
      <div class="mb-6">
        <DeadlineRiskChart :termId="termId" :api="api" :onDrillDown="setDrillDown" />
      </div>

      <!-- Stalled Students -->
      <div class="mb-6">
        <StalledStudentsChart :termId="termId" :api="api" :onDrillDown="setDrillDown" />
      </div>

      <!-- Cohort Comparison + Velocity -->
      <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <CohortComparisonChart :termId="termId" :api="api" :onDrillDown="setDrillDown" />
        <CompletionVelocityChart :termId="termId" :api="api" :onDrillDown="setDrillDown" />
      </div>
    </div>

    <StudentDrillDown
      :open="!!drillDown"
      :onClose="() => (drillDown = null)"
      :filterType="drillDown?.filterType"
      :filterValue="drillDown?.filterValue"
      :termId="termId"
      :api="api"
    />
  </div>
</template>
