import { ref, computed, watch, onUnmounted } from 'vue'
import { storeToRefs } from 'pinia'
import { useAuthStore } from '../stores/auth'
import { useToastStore } from '../stores/toast'
import type { Step, StepWithStatus, StepStatus, ProgressResponse, Term } from '../types/api'
import { parseMaybeJson } from '../utils/json'

const API_BASE = '/api'
// 30 seconds: long enough to avoid hammering the server, short enough that a
// step completed by an admin on behalf of a student becomes visible promptly.
const POLL_INTERVAL = 30000

export interface ProgressMapEntry {
  status: StepStatus
  completed_at: string | null
}

// Does a step apply to a student given their tags?
// Exported so the tag-matching rules can be unit-tested directly.
export function stepApplies(step: Step, studentTags: string[]): boolean {
  // Malformed tag JSON degrades to "no rule" — the same fallback the server
  // applies, so client and server stay in agreement on which steps apply.
  const requiredTags = parseMaybeJson<string[] | null>(step.required_tags, null)
  const excludedTags = parseMaybeJson<string[] | null>(step.excluded_tags, null)
  const requiredTagMode: 'all' | 'any' = step.required_tag_mode === 'all' ? 'all' : 'any'

  if (excludedTags && excludedTags.some((tag) => studentTags.includes(tag))) return false
  if (!requiredTags || requiredTags.length === 0) return true
  return requiredTagMode === 'all'
    ? requiredTags.every((tag) => studentTags.includes(tag))
    : requiredTags.some((tag) => studentTags.includes(tag))
}

// Required steps follow a progression (first incomplete = in_progress); optional steps don't.
// Exported for unit testing of the status-derivation logic.
export function deriveAllStepStatuses(
  steps: Step[],
  progressMap: Map<number, ProgressMapEntry>,
): StepWithStatus[] {
  let foundCurrent = false
  return steps.map((step) => {
    const progress = progressMap.get(step.id)
    if (step.is_optional === 1) {
      if (progress) return { ...step, status: progress.status }
      return { ...step, status: 'not_started' as const }
    }
    if (progress) return { ...step, status: progress.status }
    if (!foundCurrent) {
      foundCurrent = true
      return { ...step, status: 'in_progress' as const }
    }
    return { ...step, status: 'not_started' as const }
  })
}

// Fetches steps + progress, polls every 30s, derives statuses, and exposes
// computed completion metrics.
export function useProgress() {
  const auth = useAuthStore()
  const toast = useToastStore()
  const { token, isAuthenticated } = storeToRefs(auth)

  const rawSteps = ref<Step[]>([])
  const progressMap = ref<Map<number, ProgressMapEntry>>(new Map())
  const completedDates = ref<Record<string, string | null>>({})
  const studentTags = ref<string[]>([])
  const term = ref<Term | null>(null)
  const loading = ref(true)
  const error = ref<string | null>(null)
  let interval: ReturnType<typeof setInterval> | null = null

  async function fetchSteps(): Promise<void> {
    try {
      const headers = token.value ? { Authorization: `Bearer ${token.value}` } : undefined
      const res = await fetch(`${API_BASE}/steps`, { headers })
      if (res.ok) rawSteps.value = await res.json()
      else error.value = 'Failed to load checklist steps.'
    } catch {
      error.value = 'Unable to connect. Please try again later.'
    }
  }

  async function fetchProgress(): Promise<void> {
    if (!token.value) return
    try {
      const res = await fetch(`${API_BASE}/steps/progress`, {
        headers: { Authorization: `Bearer ${token.value}` },
      })
      if (res.ok) {
        const data: ProgressResponse = await res.json()
        const map = new Map<number, ProgressMapEntry>()
        for (const p of data.progress) {
          map.set(p.step_id, {
            // NULL status from legacy rows defaults to 'completed': a row was
            // only written when a step was done, so a missing status means done.
            status: (p.status || 'completed') as StepStatus,
            completed_at: p.completed_at,
          })
        }
        progressMap.value = map
        // completedDates duplicates the completed_at values from progressMap.
        // It exists so the timeline/list components can read dates without
        // unpacking the Map — a map lookup every render is equivalent but noisier.
        completedDates.value = Object.fromEntries(
          data.progress.map((p) => [p.step_id, p.completed_at]),
        )
        studentTags.value = data.tags || []
        if (data.term) term.value = data.term
        error.value = null
      } else if (res.status === 401) {
        // Token expired/invalid — drop back to the public/login view.
        toast.error('Your session expired — please sign in again.')
        auth.logout()
      } else if (progressMap.value.size === 0) {
        // Non-ok, non-401 (e.g. 5xx) with no prior data: surface an error so the
        // student sees a retry path instead of a silently wiped checklist. If we
        // already have progress loaded, keep it stale rather than overwrite.
        error.value = 'Unable to load your progress. Please try again.'
      }
    } catch {
      // Server unavailable — keep existing data
    }
  }

  const steps = computed<StepWithStatus[]>(() => {
    const applicable = rawSteps.value.filter((s) => stepApplies(s, studentTags.value))
    return deriveAllStepStatuses(applicable, progressMap.value)
  })
  const requiredOnly = computed(() => steps.value.filter((s) => s.is_optional !== 1))
  const totalSteps = computed(() => requiredOnly.value.length)
  const completedCount = computed(
    () =>
      requiredOnly.value.filter((s) => s.status === 'completed' || s.status === 'waived').length,
  )
  const percentage = computed(() =>
    totalSteps.value > 0 ? Math.round((completedCount.value / totalSteps.value) * 100) : 0,
  )
  const currentStep = computed<StepWithStatus | null>(
    () => requiredOnly.value.find((s) => s.status === 'in_progress') || null,
  )
  const allComplete = computed(
    () => totalSteps.value > 0 && completedCount.value === totalSteps.value,
  )

  // Load on mount + whenever the token changes; poll while authenticated.
  watch(
    token,
    () => {
      fetchSteps()
      if (interval) {
        clearInterval(interval)
        interval = null
      }
      if (isAuthenticated.value) {
        fetchProgress().then(() => (loading.value = false))
        interval = setInterval(fetchProgress, POLL_INTERVAL)
      } else {
        loading.value = false
      }
    },
    { immediate: true },
  )

  onUnmounted(() => {
    if (interval) clearInterval(interval)
  })

  return {
    steps,
    completedDates,
    studentTags,
    term,
    loading,
    error,
    totalSteps,
    completedCount,
    percentage,
    currentStep,
    allComplete,
    // Retry must re-run BOTH fetches: if the steps fetch was the one that failed,
    // retrying only progress would clear the error while steps stay empty —
    // flipping the UI into a false "No Checklist Available" state.
    retry: async () => {
      await fetchSteps()
      await fetchProgress()
    },
  }
}
