import { describe, it, expect, vi, beforeEach } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { setActivePinia, createPinia } from 'pinia'
import { mount, flushPromises } from '@vue/test-utils'

// AdminPage wraps its tab area in <KeepAlive> so switching away from a tab and back does
// NOT remount it — the Analytics tab in particular refires ~9 aggregate requests on every
// mount. This test asserts the caching semantics: a tab mounts once, and re-activating it
// after switching away does not mount it again.

// Count how many times each mocked tab is mounted. If KeepAlive is working, a tab that is
// activated, switched away from, and re-activated stays at a single mount.
const mountCounts: Record<string, number> = {}
function countingTab(name: string) {
  return {
    default: defineComponent({
      name,
      // Accept (and ignore) whatever props/emits AdminPage binds.
      inheritAttrs: false,
      setup() {
        mountCounts[name] = (mountCounts[name] ?? 0) + 1
        return () => h('div', name)
      },
    }),
  }
}

// Mock the tab modules so their heavy transitive imports (e.g. the emoji picker that opens
// IndexedDB on load) never enter the graph; we only care about mount/unmount counting here.
vi.mock('./StudentsTab.vue', () => countingTab('StudentsTab'))
vi.mock('./TermStepsTab.vue', () => countingTab('TermStepsTab'))
vi.mock('./AnalyticsTab.vue', () => countingTab('AnalyticsTab'))
vi.mock('./AuditLogTab.vue', () => countingTab('AuditLogTab'))
vi.mock('./AdminUsersTab.vue', () => countingTab('AdminUsersTab'))
vi.mock('./AdminLogin.vue', () => ({
  default: defineComponent({ name: 'AdminLogin', setup: () => () => h('div', 'AdminLogin') }),
}))

// A fresh, resolved AdminApi stub so the on-login terms/steps watchers don't hit the network.
const apiStub = {
  get: vi.fn().mockResolvedValue([]),
  post: vi.fn().mockResolvedValue({}),
  put: vi.fn().mockResolvedValue({}),
  del: vi.fn().mockResolvedValue({}),
  raw: vi.fn(),
}
vi.mock('../../composables/useAdminApi', () => ({
  useAdminApi: () => apiStub,
}))

import AdminPage from './AdminPage.vue'

function mountAdmin() {
  // A valid-looking, non-expired admin token so AdminPage renders the tabbed UI (not the
  // login screen). isTokenExpired only reads payload.exp, so any header is fine.
  const exp = Math.floor(Date.now() / 1000) + 3600
  const payload = btoa(JSON.stringify({ exp }))
  sessionStorage.setItem('csub_admin_token', `h.${payload}.s`)
  sessionStorage.setItem(
    'csub_admin_user',
    JSON.stringify({ id: 1, email: 'a@csub.edu', displayName: 'Admin', role: 'sysadmin' }),
  )

  return mount(AdminPage)
}

describe('AdminPage KeepAlive tabs', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    sessionStorage.clear()
    for (const k of Object.keys(mountCounts)) delete mountCounts[k]
  })

  it('does not remount a tab when it is re-activated', async () => {
    const wrapper = mountAdmin()
    await flushPromises()

    // Students is the default tab, mounted once.
    expect(mountCounts.StudentsTab).toBe(1)
    expect(mountCounts.AnalyticsTab ?? 0).toBe(0)

    const vm = wrapper.vm as unknown as { activeTab: string }

    // Switch to Analytics — mounts once.
    vm.activeTab = 'analytics'
    await nextTick()
    expect(mountCounts.AnalyticsTab).toBe(1)

    // Back to Students, then back to Analytics: neither remounts (KeepAlive cache hit).
    vm.activeTab = 'students'
    await nextTick()
    vm.activeTab = 'analytics'
    await nextTick()

    expect(mountCounts.StudentsTab).toBe(1)
    expect(mountCounts.AnalyticsTab).toBe(1)
  })
})
