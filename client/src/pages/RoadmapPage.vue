<script setup lang="ts">
import { ref, computed, watch, onUnmounted } from 'vue'
import { storeToRefs } from 'pinia'
import { useAuthStore } from '../stores/auth'
import { useToastStore } from '../stores/toast'
import { useProgress } from '../composables/useProgress'
import { useStudentApi } from '../composables/useStudentApi'
import ProgressSummary from '../components/roadmap/ProgressSummary.vue'
import CurrentStepCallout from '../components/roadmap/CurrentStepCallout.vue'
import RoadmapTimeline from '../components/roadmap/RoadmapTimeline.vue'
import ListView from '../components/roadmap/ListView.vue'
import StepDetailPanel from '../components/roadmap/StepDetailPanel.vue'
import HelpSection from '../components/roadmap/HelpSection.vue'
import CompletionBanner from '../components/roadmap/CompletionBanner.vue'
import Celebration from '../components/Celebration.vue'
import HighContrastToggle from '../components/HighContrastToggle.vue'
import type { StepWithStatus, CheckStatusResponse } from '../types/api'

type ViewMode = 'timeline' | 'list'

const auth = useAuthStore()
const toast = useToastStore()
const api = useStudentApi()
const { user, token } = storeToRefs(auth)
const { logout } = auth

const {
  steps,
  completedDates,
  loading,
  error,
  totalSteps,
  completedCount,
  percentage,
  currentStep,
  allComplete,
  term,
  retry,
} = useProgress()

const selectedStep = ref<StepWithStatus | null>(null)
const showCelebration = ref<boolean>(false)
const celebrationShown = ref<boolean>(false)
const viewMode = ref<ViewMode>('timeline')
const showOnlyIncomplete = ref<boolean>(false)
const updatingOptionalStepId = ref<number | null>(null)

// API check polling — trigger background checks and poll for results.
// Each watch firing starts its own run; a monotonically increasing generation
// counter lets an orphaned run (from an overlapping re-fire or unmount) notice
// it is no longer the current owner and self-terminate. cleanupPolling() bumps
// the generation so no run's callback can clear a *later* run's interval.
let currentGeneration = 0

function cleanupPolling() {
  currentGeneration++
}

watch(
  [loading, token, () => steps.value.length],
  () => {
    // Tear down any prior run
    cleanupPolling()

    if (loading.value || !token.value || steps.value.length === 0) return

    const myGeneration = currentGeneration

    api
      .raw('/roadmap/run-api-checks', { method: 'POST' })
      .then((res) => res.json())
      .then((data: { status: string }) => {
        if (myGeneration !== currentGeneration || data.status !== 'started') return

        const startTime = Date.now()
        const myInterval = setInterval(async () => {
          if (myGeneration !== currentGeneration) {
            clearInterval(myInterval)
            return
          }
          if (Date.now() - startTime > 30000) {
            clearInterval(myInterval)
            return
          }
          try {
            const res = await api.raw('/roadmap/check-status')
            const poll: CheckStatusResponse = await res.json()
            if (poll.checkedSteps?.length && poll.checkedSteps.length > 0) retry()
            if (poll.status === 'complete') clearInterval(myInterval)
          } catch {
            clearInterval(myInterval)
          }
        }, 2000)
      })
      .catch(() => {})
  },
  { immediate: true },
)

onUnmounted(() => {
  cleanupPolling()
})

// Show celebration once when all complete
watch(
  allComplete,
  () => {
    if (allComplete.value && !celebrationShown.value) {
      showCelebration.value = true
      celebrationShown.value = true
    }
  },
  { immediate: true },
)

const firstName = computed(
  () => (user.value as { displayName?: string } | null)?.displayName?.split(' ')[0] || 'Student',
)

// Filter steps
const filteredSteps = computed<StepWithStatus[]>(() => {
  if (!showOnlyIncomplete.value) return steps.value
  return steps.value.filter((s) => s.status !== 'completed' && s.status !== 'waived')
})

// selectedStepList was `filteredSteps` when a step was selected, or [] when none.
// The only consumers are selectedStepIndex, StepDetailPanel total-steps and nav —
// they all want filteredSteps; the "no selection" guard is handled at the use sites.
const selectedStepIndex = computed(() =>
  selectedStep.value ? filteredSteps.value.findIndex((s) => s.id === selectedStep.value!.id) : -1,
)

const currentStepNumber = computed(() =>
  currentStep.value ? steps.value.findIndex((s) => s.id === currentStep.value!.id) + 1 : 0,
)

async function handleOptionalStepStatusChange(step: StepWithStatus, status: string) {
  updatingOptionalStepId.value = step.id
  try {
    await api.put(`/steps/${step.id}/status`, { status })

    await retry()
    if (showOnlyIncomplete.value && status === 'completed') {
      selectedStep.value = null
    } else {
      const prev = selectedStep.value
      if (prev && prev.id === step.id) {
        selectedStep.value = {
          ...prev,
          status: status === 'completed' ? 'completed' : 'not_started',
        } as StepWithStatus
      }
    }
  } catch {
    toast.error('Could not update that step. Please try again.')
  } finally {
    updatingOptionalStepId.value = null
  }
}

function handleNavigate(direction: 'prev' | 'next') {
  if (!selectedStep.value) return
  const idx = filteredSteps.value.findIndex((s) => s.id === selectedStep.value!.id)
  const next = direction === 'next' ? filteredSteps.value[idx + 1] : filteredSteps.value[idx - 1]
  if (next) selectedStep.value = next
}
</script>

<template>
  <!-- Loading state -->
  <div
    v-if="loading"
    class="min-h-screen flex items-center justify-center bg-gray-50"
    role="status"
    aria-label="Loading your roadmap"
  >
    <div class="text-center">
      <div
        class="w-10 h-10 border-4 border-csub-blue border-t-transparent rounded-full animate-spin mx-auto mb-4"
        aria-hidden="true"
      />
      <p class="text-csub-blue font-display text-lg font-semibold uppercase tracking-wider">
        Loading your roadmap...
      </p>
    </div>
  </div>

  <!-- Error state -->
  <div
    v-else-if="error && steps.length === 0"
    class="min-h-screen flex items-center justify-center bg-gray-50 px-4"
  >
    <div class="text-center max-w-md">
      <div class="w-16 h-16 bg-red-50 rounded-full flex items-center justify-center mx-auto mb-4">
        <svg class="w-8 h-8 text-red-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            stroke-width="2"
            d="M12 9v2m0 4h.01M12 3a9 9 0 100 18 9 9 0 000-18z"
          />
        </svg>
      </div>
      <h2 class="font-display text-xl font-bold text-csub-blue-dark uppercase tracking-wide mb-2">
        Something went wrong
      </h2>
      <p class="font-body text-csub-gray mb-6">{{ error }}</p>
      <button
        @click="retry"
        class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-6 py-3 rounded-lg shadow-sm transition-colors text-sm"
      >
        Try Again
      </button>
      <p class="font-body text-sm text-csub-gray mt-4">
        If this keeps happening, contact
        <a href="mailto:admissions@csub.edu" class="text-csub-blue underline"
          >admissions@csub.edu</a
        >
      </p>
    </div>
  </div>

  <!-- Empty state -->
  <div
    v-else-if="steps.length === 0"
    class="min-h-screen flex items-center justify-center bg-gray-50 px-4"
  >
    <div class="text-center max-w-md">
      <div class="text-5xl mb-4">📋</div>
      <h2 class="font-display text-xl font-bold text-csub-blue-dark uppercase tracking-wide mb-2">
        No Checklist Available
      </h2>
      <p class="font-body text-csub-gray mb-6">
        Your admissions checklist hasn't been set up yet. This usually means your application is
        still being processed.
      </p>
      <p class="font-body text-sm text-csub-gray">
        Questions? Contact
        <a href="mailto:admissions@csub.edu" class="text-csub-blue underline"
          >admissions@csub.edu</a
        >
        or call <a href="tel:6616542160" class="text-csub-blue underline">(661) 654-2160</a>
      </p>
    </div>
  </div>

  <div v-else class="min-h-screen bg-gray-50">
    <!-- ===== A. Page Header ===== -->
    <header class="bg-csub-blue-dark text-white">
      <div class="max-w-4xl mx-auto px-4 sm:px-6 py-6 sm:py-8">
        <div class="flex items-start justify-between">
          <div>
            <p class="font-body text-csub-gold text-sm font-semibold tracking-wide mb-1">
              {{ term?.name || 'Admissions' }}
            </p>
            <h1
              class="font-display text-2xl sm:text-3xl md:text-4xl font-bold uppercase tracking-wide"
            >
              Welcome, {{ firstName }}
            </h1>
            <p class="font-body text-white/70 text-sm mt-1">
              Your step-by-step guide to becoming a Roadrunner
            </p>
          </div>
          <div class="flex items-center gap-3 shrink-0 mt-1">
            <HighContrastToggle />
            <span class="text-white/30">&middot;</span>
            <button
              @click="logout"
              class="font-body text-sm text-white/80 hover:text-white transition-colors"
              aria-label="Sign out"
            >
              Sign out
            </button>
          </div>
        </div>
      </div>
    </header>

    <!-- ===== B. Progress Summary (sticky) ===== -->
    <ProgressSummary
      :completed-count="completedCount"
      :total-steps="totalSteps"
      :percentage="percentage"
      :current-step-title="currentStep?.title"
      :all-complete="allComplete"
    />

    <main id="main-content" class="max-w-4xl mx-auto px-4 sm:px-6 pb-16">
      <!-- ===== F. Completion Banner ===== -->
      <CompletionBanner v-if="allComplete" :first-name="firstName" />

      <!-- ===== C. Current Step Callout ===== -->
      <CurrentStepCallout
        v-if="currentStep && !allComplete"
        :step="currentStep"
        :step-number="currentStepNumber"
        @view-details="selectedStep = currentStep"
      />

      <!-- ===== View Controls ===== -->
      <div class="flex items-center justify-between mb-4">
        <div class="flex items-center gap-3">
          <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wider">
            Your Roadmap
          </h2>
          <div class="flex-1 h-px bg-gray-200 hidden sm:block" style="min-width: 2rem" />
        </div>

        <div class="flex items-center gap-3">
          <!-- Filter -->
          <label class="flex items-center gap-1.5 cursor-pointer">
            <input
              type="checkbox"
              v-model="showOnlyIncomplete"
              class="w-5 h-5 rounded-sm border-gray-300 text-csub-blue focus:ring-csub-blue"
            />
            <span class="font-body text-xs text-csub-gray">Incomplete only</span>
          </label>

          <!-- View toggle -->
          <div class="flex bg-gray-100 rounded-lg p-0.5">
            <button
              @click="viewMode = 'timeline'"
              :class="`p-2.5 rounded-md transition-colors ${
                viewMode === 'timeline'
                  ? 'bg-white shadow-xs text-csub-blue'
                  : 'text-gray-400 hover:text-gray-600'
              }`"
              aria-label="Timeline view"
              title="Timeline view"
            >
              <svg
                class="w-4 h-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                stroke-width="2"
              >
                <path stroke-linecap="round" stroke-linejoin="round" d="M4 6h16M4 12h16M4 18h16" />
                <circle cx="4" cy="6" r="1" fill="currentColor" />
                <circle cx="4" cy="12" r="1" fill="currentColor" />
                <circle cx="4" cy="18" r="1" fill="currentColor" />
              </svg>
            </button>
            <button
              @click="viewMode = 'list'"
              :class="`p-2.5 rounded-md transition-colors ${
                viewMode === 'list'
                  ? 'bg-white shadow-xs text-csub-blue'
                  : 'text-gray-400 hover:text-gray-600'
              }`"
              aria-label="List view"
              title="List view"
            >
              <svg
                class="w-4 h-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                stroke-width="2"
              >
                <path stroke-linecap="round" stroke-linejoin="round" d="M4 6h16M4 12h16M4 18h16" />
              </svg>
            </button>
          </div>
        </div>
      </div>

      <!-- ===== D. Roadmap View ===== -->
      <RoadmapTimeline
        v-if="viewMode === 'timeline'"
        :steps="filteredSteps"
        :completed-dates="completedDates"
        @select-step="(s) => (selectedStep = s)"
      />
      <ListView
        v-else
        :steps="filteredSteps"
        :completed-dates="completedDates"
        @select-step="(s) => (selectedStep = s)"
      />

      <!-- Filtered empty state -->
      <div v-if="showOnlyIncomplete && filteredSteps.length === 0" class="text-center py-12">
        <div class="text-4xl mb-3">🎉</div>
        <p class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
          All caught up!
        </p>
        <p class="font-body text-sm text-csub-gray mt-1">No incomplete steps remaining.</p>
      </div>

      <!-- ===== E. Help Section ===== -->
      <HelpSection />
    </main>

    <!-- Step Detail Panel -->
    <Transition>
      <StepDetailPanel
        v-if="selectedStep"
        :step="selectedStep"
        :step-number="selectedStepIndex + 1"
        :total-steps="filteredSteps.length"
        :completed-at="completedDates[selectedStep.id]"
        :has-prev="selectedStepIndex > 0"
        :has-next="selectedStepIndex < filteredSteps.length - 1"
        :on-optional-step-status-change="
          selectedStep.is_optional === 1
            ? (status: string) => handleOptionalStepStatusChange(selectedStep!, status)
            : null
        "
        :updating-optional-step="updatingOptionalStepId === selectedStep.id"
        @close="selectedStep = null"
        @navigate="handleNavigate"
      />
    </Transition>

    <!-- Celebration modal -->
    <Transition>
      <div v-if="showCelebration" class="fixed inset-0 z-50">
        <Celebration @close="showCelebration = false" />
      </div>
    </Transition>
  </div>
</template>
