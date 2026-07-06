import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'

// Azure AD is "not configured" in tests — mock the config + library so no real MSAL
// runs when useProgress pulls in the auth store (matches auth.test.ts).
vi.mock('../auth/msalConfig', () => ({
  msalInstance: null,
  isAzureAdConfigured: false,
  loginRequest: {},
}))
vi.mock('@azure/msal-browser', () => ({
  BrowserAuthError: class BrowserAuthError extends Error {},
}))

import { useProgress } from './useProgress'
import { useAuthStore } from '../stores/auth'
import { useToastStore } from '../stores/toast'

// Response helper: a fetch Response with a JSON body and status.
function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

// A steps response (GET /api/steps) with a single required step, and a progress
// response (GET /api/steps/progress) the test can shape per case.
const STEPS_BODY = [
  {
    id: 1,
    title: 'Step one',
    description: '',
    icon: '',
    sort_order: 0,
    is_public: 1,
    is_optional: 0,
    deadline: null,
    deadline_date: null,
    links: null,
    guide_content: null,
    contact_info: null,
    required_tags: null,
    excluded_tags: null,
    required_tag_mode: null,
    link_url: null,
    link_label: null,
    category: null,
    api_check_type: null,
  },
]

// Route a fetch mock by URL: /api/steps/progress -> progressResponder, /api/steps -> steps.
// Returns the spy so tests can count progress polls.
function stubRoutedFetch(progressResponder: () => Response) {
  const fn = vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input.toString()
    if (url.includes('/steps/progress')) return progressResponder()
    if (url.includes('/steps')) return json(200, STEPS_BODY)
    return json(404, {})
  })
  vi.stubGlobal('fetch', fn)
  return fn
}

// Mount useProgress inside a host component so the watch(immediate) + onUnmounted
// lifecycle actually runs. Exposes the composable's return value on the wrapper.
function mountProgress() {
  let api!: ReturnType<typeof useProgress>
  const Host = defineComponent({
    setup() {
      api = useProgress()
      return () => h('div')
    },
  })
  const wrapper = mount(Host)
  return { wrapper, getApi: () => api }
}

describe('useProgress (stateful)', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    sessionStorage.clear()
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('logs out and toasts when progress returns 401', async () => {
    const auth = useAuthStore()
    const toast = useToastStore()
    auth.token = 'valid-token'
    const logoutSpy = vi.spyOn(auth, 'logout')
    const toastErrorSpy = vi.spyOn(toast, 'error')

    stubRoutedFetch(() => json(401, { error: 'expired' }))
    mountProgress()

    // Let the immediate watch's fetchSteps + fetchProgress promises settle.
    await vi.runOnlyPendingTimersAsync()

    expect(logoutSpy).toHaveBeenCalledOnce()
    expect(toastErrorSpy).toHaveBeenCalledOnce()
    expect(auth.token).toBeNull()
  })

  it('keeps already-loaded progress on a 5xx (does not wipe stale data)', async () => {
    const auth = useAuthStore()
    auth.token = 'valid-token'

    // First progress call succeeds with one completed step; later calls return 500.
    let call = 0
    stubRoutedFetch(() => {
      call++
      if (call === 1) {
        return json(200, {
          progress: [{ step_id: 1, status: 'completed', completed_at: '2026-01-01' }],
          tags: [],
          term: null,
        })
      }
      return json(500, { error: 'boom' })
    })

    const { getApi } = mountProgress()
    await vi.runOnlyPendingTimersAsync()

    // Loaded once: the step is completed and there is no error.
    expect(getApi().completedCount.value).toBe(1)
    expect(getApi().error.value).toBeNull()

    // Advance one poll interval (30s) so a 5xx lands with data already present.
    await vi.advanceTimersByTimeAsync(30000)

    // Stale data is preserved, and no error is surfaced (we only error on empty).
    expect(getApi().completedCount.value).toBe(1)
    expect(getApi().error.value).toBeNull()
  })

  it('surfaces an error on a 5xx when there is no prior progress', async () => {
    const auth = useAuthStore()
    auth.token = 'valid-token'
    stubRoutedFetch(() => json(500, { error: 'boom' }))

    const { getApi } = mountProgress()
    await vi.runOnlyPendingTimersAsync()

    // Non-ok, non-401, empty map -> a retryable error message.
    expect(getApi().error.value).toBe('Unable to load your progress. Please try again.')
    expect(getApi().completedCount.value).toBe(0)
  })

  it('polls progress on the 30s interval while authenticated', async () => {
    const auth = useAuthStore()
    auth.token = 'valid-token'
    const fetchSpy = stubRoutedFetch(() => json(200, { progress: [], tags: [], term: null }))

    mountProgress()
    await vi.runOnlyPendingTimersAsync()

    const progressCalls = () =>
      fetchSpy.mock.calls.filter(([u]) => String(u).includes('/steps/progress')).length
    const afterMount = progressCalls()
    expect(afterMount).toBeGreaterThanOrEqual(1)

    // Two more intervals -> two more progress polls.
    await vi.advanceTimersByTimeAsync(30000)
    await vi.advanceTimersByTimeAsync(30000)
    expect(progressCalls()).toBe(afterMount + 2)
  })

  it('clears the poll interval on unmount (no further polls)', async () => {
    const auth = useAuthStore()
    auth.token = 'valid-token'
    const fetchSpy = stubRoutedFetch(() => json(200, { progress: [], tags: [], term: null }))

    const { wrapper } = mountProgress()
    await vi.runOnlyPendingTimersAsync()

    const progressCalls = () =>
      fetchSpy.mock.calls.filter(([u]) => String(u).includes('/steps/progress')).length
    const beforeUnmount = progressCalls()

    wrapper.unmount()
    // After unmount, advancing time must NOT trigger more progress polls.
    await vi.advanceTimersByTimeAsync(30000 * 3)
    expect(progressCalls()).toBe(beforeUnmount)
  })

  it('does not poll when unauthenticated', async () => {
    // No token -> isAuthenticated is false -> no interval, no progress fetch.
    const fetchSpy = stubRoutedFetch(() => json(200, { progress: [], tags: [], term: null }))

    const { getApi } = mountProgress()
    await vi.runOnlyPendingTimersAsync()
    await vi.advanceTimersByTimeAsync(30000 * 2)

    const progressCalls = fetchSpy.mock.calls.filter(([u]) =>
      String(u).includes('/steps/progress'),
    ).length
    expect(progressCalls).toBe(0)
    // Loading still resolves to false so the UI leaves its spinner.
    expect(getApi().loading.value).toBe(false)
  })

  it('retry() re-runs BOTH the steps and the progress fetch', async () => {
    const auth = useAuthStore()
    auth.token = 'valid-token'
    const fetchSpy = stubRoutedFetch(() => json(200, { progress: [], tags: [], term: null }))

    const { getApi } = mountProgress()
    await vi.runOnlyPendingTimersAsync()

    const stepsCalls = () =>
      fetchSpy.mock.calls.filter(
        ([u]) => String(u).includes('/steps') && !String(u).includes('/progress'),
      ).length
    const progressCalls = () =>
      fetchSpy.mock.calls.filter(([u]) => String(u).includes('/steps/progress')).length

    const stepsBefore = stepsCalls()
    const progressBefore = progressCalls()

    await getApi().retry()
    await nextTick()

    // Retry must hit BOTH endpoints — retrying only progress would leave the UI in a
    // false "No Checklist Available" state if the steps fetch was the one that failed.
    expect(stepsCalls()).toBe(stepsBefore + 1)
    expect(progressCalls()).toBe(progressBefore + 1)
  })
})
