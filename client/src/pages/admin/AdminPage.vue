<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { useToastStore } from '../../stores/toast'
import { useAdminApi } from '../../composables/useAdminApi'
import { parseMaybeJson } from '../../utils/json'
import AdminLogin from './AdminLogin.vue'
import StudentsTab from './StudentsTab.vue'
import AuditLogTab from './AuditLogTab.vue'
import AdminUsersTab from './AdminUsersTab.vue'
import AnalyticsTab from './AnalyticsTab.vue'
import TermStepsTab from './TermStepsTab.vue'
import { ROLES } from './roleConfig'

interface AdminUser {
  id: number
  email: string
  displayName: string
  role: string
}

interface Term {
  id: number
  name: string
  is_active: number
  start_date: string
  end_date: string
  step_count?: number
  student_count?: number
}

interface Step {
  id: number
  title: string
  description: string | null
  sort_order: number
  is_active: number
  is_optional: number
  is_public: number
  icon: string | null
  deadline: string | null
  deadline_date: string | null
  required_tags: string | string[] | null
  excluded_tags: string | string[] | null
  required_tag_mode: string | null
  term_id: number
}

interface TabDef {
  key: string
  label: string
}

function isTokenExpired(token: string): boolean {
  try {
    const payload = JSON.parse(atob(token.split('.')[1] ?? ''))
    return payload.exp * 1000 < Date.now()
  } catch {
    return true
  }
}

const toast = useToastStore()
const token = ref<string | null>(
  (() => {
    const stored = sessionStorage.getItem('csub_admin_token')
    if (stored && isTokenExpired(stored)) {
      sessionStorage.removeItem('csub_admin_token')
      sessionStorage.removeItem('csub_admin_user')
      return null
    }
    return stored
  })(),
)

const adminUser = ref<AdminUser | null>(
  (() => {
    const stored = sessionStorage.getItem('csub_admin_user')
    if (!stored) return null
    // parseMaybeJson returns null (the fallback) for corrupted values, avoiding a
    // setup-time throw that would break the entire /admin page until storage is cleared.
    const parsed = parseMaybeJson<AdminUser | null>(stored, null)
    if (parsed === null) sessionStorage.removeItem('csub_admin_user')
    return parsed
  })(),
)

const activeTab = ref('students')
const steps = ref<Step[]>([])
const terms = ref<Term[]>([])
const selectedTermId = ref<number | null>(null)

const handleAuthError = () => {
  sessionStorage.removeItem('csub_admin_token')
  sessionStorage.removeItem('csub_admin_user')
  token.value = null
  adminUser.value = null
}

const api = computed(() => useAdminApi(token.value, handleAuthError))

const handleTermsChange = (
  nextTerms: Term[],
  preferredTermId: number | null = selectedTermId.value,
) => {
  terms.value = nextTerms
  if (preferredTermId && nextTerms.some((term) => term.id === preferredTermId)) {
    selectedTermId.value = preferredTermId
    return
  }

  const active = nextTerms.find((term) => term.is_active)
  if (active) {
    selectedTermId.value = active.id
    return
  }

  selectedTermId.value = nextTerms[0]?.id || null
}

const handleLogin = (newToken: string, user: AdminUser) => {
  sessionStorage.setItem('csub_admin_token', newToken)
  sessionStorage.setItem('csub_admin_user', JSON.stringify(user))
  token.value = newToken
  adminUser.value = user
}

const handleLogout = handleAuthError

// Fetch terms on login
watch(
  [token, api],
  () => {
    if (!token.value) return
    api.value
      .get<Term[]>('/terms')
      .then((data) => {
        handleTermsChange(data)
      })
      .catch(() => {
        // Surface it — silently swallowing leaves tabs stuck on "Loading..." forever.
        toast.error('Could not load terms. Please refresh or try again.')
      })
  },
  { immediate: true },
)

// Fetch steps when term changes
watch(
  [token, api, selectedTermId],
  () => {
    if (!token.value || !selectedTermId.value) return
    api.value
      .get<Step[]>(`/steps?term_id=${selectedTermId.value}`)
      .then((data) => {
        steps.value = data
      })
      .catch(() => {
        toast.error('Could not load steps for this term.')
      })
  },
  { immediate: true },
)

const role = computed(() => adminUser.value?.role || 'viewer')
const showHeaderTermSelector = computed(
  () => ['students', 'termSteps', 'analytics'].includes(activeTab.value) && terms.value.length > 0,
)

const tabs = computed<TabDef[]>(() => [
  { key: 'students', label: 'Students' },
  { key: 'termSteps', label: 'Terms & Steps' },
  { key: 'analytics', label: 'Analytics' },
  { key: 'audit', label: 'Audit Log' },
  ...(role.value === 'sysadmin' ? [{ key: 'users', label: 'Users' }] : []),
])

const onSelectTerm = (termId: number | null) => {
  selectedTermId.value = termId
}

const onTermSelectChange = (e: Event) => {
  selectedTermId.value = parseInt((e.target as HTMLSelectElement).value, 10)
}
</script>

<template>
  <AdminLogin v-if="!token" @login="handleLogin" />
  <div v-else class="min-h-screen bg-gray-50">
    <!-- Header -->
    <div class="bg-csub-blue-dark text-white">
      <div class="max-w-5xl mx-auto px-4 sm:px-6 py-4">
        <!-- Top row: title + user actions -->
        <div class="flex items-center justify-between">
          <h1 class="font-display text-lg font-bold uppercase tracking-wide">Rowdy Rundown</h1>
          <div class="flex items-center gap-3 text-sm font-body">
            <label
              v-if="showHeaderTermSelector"
              class="flex items-center gap-2 text-xs text-white/70"
            >
              <span class="uppercase tracking-wider font-display font-bold text-[10px]">Term</span>
              <select
                :value="selectedTermId || ''"
                @change="onTermSelectChange"
                class="bg-white/10 text-white border border-white/20 rounded-md px-2 py-1 text-xs focus:outline-hidden focus:ring-2 focus:ring-csub-gold"
                aria-label="Selected term"
              >
                <option v-for="t in terms" :key="t.id" :value="t.id" class="text-gray-900">
                  {{ t.name }}{{ t.is_active ? '' : ' (inactive)' }}
                </option>
              </select>
            </label>
            <span class="text-white/70">
              {{ adminUser?.displayName }}
              <span class="text-white/40 ml-1.5 text-xs">
                {{ ROLES[role]?.label || role }}
              </span>
            </span>
            <button @click="handleLogout" class="text-white/80 hover:text-white transition-colors">
              Sign Out
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Tab nav -->
    <div class="bg-white border-b border-gray-200 shadow-xs">
      <div
        class="max-w-5xl mx-auto px-4 sm:px-6 flex gap-1 py-1.5 overflow-x-auto"
        role="tablist"
        aria-label="Admin sections"
      >
        <button
          v-for="tab in tabs"
          :key="tab.key"
          role="tab"
          :id="`tab-${tab.key}`"
          :aria-selected="activeTab === tab.key"
          :aria-controls="`tabpanel-${tab.key}`"
          @click="activeTab = tab.key"
          :class="`flex items-center gap-2 font-display text-sm font-bold uppercase tracking-wider px-4 py-2 rounded-lg transition-all whitespace-nowrap ${
            activeTab === tab.key
              ? 'bg-csub-blue/10 text-csub-blue-dark'
              : 'text-csub-gray hover:bg-gray-50 hover:text-csub-blue-dark'
          }`"
        >
          <svg
            v-if="tab.key === 'students'"
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z"
            />
          </svg>
          <svg
            v-else-if="tab.key === 'termSteps'"
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4"
            />
          </svg>
          <svg
            v-else-if="tab.key === 'analytics'"
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z"
            />
          </svg>
          <svg
            v-else-if="tab.key === 'audit'"
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
          <svg
            v-else-if="tab.key === 'users'"
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"
            />
          </svg>
          {{ tab.label }}
        </button>
      </div>
    </div>

    <!-- Tab content -->
    <div
      class="max-w-5xl mx-auto px-4 sm:px-6 py-8"
      role="tabpanel"
      :id="`tabpanel-${activeTab}`"
      :aria-labelledby="`tab-${activeTab}`"
    >
      <!-- KeepAlive caches each tab so switching away and back does not remount it (the
           Analytics tab in particular refires ~9 aggregate requests on every mount). A term
           change still refetches because each tab watches its term-id prop. The blocks form
           a single v-if/v-else-if chain so KeepAlive only ever sees one child at a time. -->
      <KeepAlive>
        <StudentsTab
          v-if="activeTab === 'students'"
          :api="api"
          :steps="steps"
          :role="role"
          :term-id="selectedTermId"
        />
        <TermStepsTab
          v-else-if="activeTab === 'termSteps'"
          :api="api"
          :role="role"
          :terms="terms"
          :selected-term-id="selectedTermId"
          @terms-change="handleTermsChange"
          @select-term="onSelectTerm"
        />
        <AnalyticsTab v-else-if="activeTab === 'analytics'" :api="api" :term-id="selectedTermId" />
        <AuditLogTab v-else-if="activeTab === 'audit'" :api="api" />
        <AdminUsersTab v-else-if="activeTab === 'users' && role === 'sysadmin'" :api="api" />
      </KeepAlive>
    </div>
  </div>
</template>
