<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import SummaryStats from './SummaryStats.vue'
import StudentDetail from './StudentDetail.vue'
import type { AdminApi } from '../../composables/useAdminApi'

interface Step {
  id: number
  title: string
  icon: string | null
  is_active: number
  is_optional: number
}

interface StudentListItem {
  id: number
  display_name: string
  email: string
  emplid?: string
  applicant_type?: string
  residency?: string
  completed_steps: number
  overdue_step_count: number
}

interface SortOption {
  value: string
  label: string
}

const props = withDefaults(defineProps<{
  api: AdminApi
  steps: Step[]
  role?: string
  termId: number | null
}>(), {
  role: 'viewer',
})

const PER_PAGE = 25

const SORT_OPTIONS: SortOption[] = [
  { value: 'date_desc', label: 'Newest First' },
  { value: 'date_asc', label: 'Oldest First' },
  { value: 'name_asc', label: 'Name A–Z' },
  { value: 'name_desc', label: 'Name Z–A' },
  { value: 'progress_desc', label: 'Most Progress' },
  { value: 'progress_asc', label: 'Least Progress' },
]

const students = ref<StudentListItem[]>([])
const search = ref('')
const selectedStudent = ref<StudentListItem | null>(null)
const loading = ref(true)
const totalActiveSteps = ref(0)

const page = ref(1)
const totalStudents = ref(0)
const sortBy = ref('date_desc')
const overdueOnly = ref(false)

watch(
  () => props.steps,
  () => {
    totalActiveSteps.value = props.steps.filter((s) => s.is_active !== 0).length
  },
  { immediate: true },
)

const fetchStudents = async (overrides: Record<string, any> = {}) => {
  loading.value = true
  try {
    const p = overrides.page ?? page.value
    const s = overrides.sort ?? sortBy.value
    const q = overrides.search ?? search.value
    const od = overrides.overdueOnly ?? overdueOnly.value

    let url = `/students?page=${p}&per_page=${PER_PAGE}&sort=${s}`
    if (q.trim()) url += `&search=${encodeURIComponent(q)}`
    if (props.termId) url += `&term_id=${props.termId}`
    if (od) url += `&overdue_only=1`

    const data: any = await props.api.get(url)
    students.value = data.students
    totalStudents.value = data.total
  } catch {
    // ignore
  } finally {
    loading.value = false
  }
}

watch(
  () => [page.value, sortBy.value, overdueOnly.value, props.termId] as const,
  () => { fetchStudents() },
  { immediate: true },
)

const handleSearch = () => { page.value = 1; fetchStudents({ page: 1 }) }
const handleSearchClear = () => { search.value = ''; page.value = 1; fetchStudents({ page: 1, search: '' }) }
const handleSortChange = (newSort: string) => { sortBy.value = newSort; page.value = 1; fetchStudents({ page: 1, sort: newSort }) }
const handleOverdueToggle = () => { const next = !overdueOnly.value; overdueOnly.value = next; page.value = 1; fetchStudents({ page: 1, overdueOnly: next }) }
const refreshStudents = () => fetchStudents()

const getInitials = (name: string | undefined): string => {
  if (!name) return '?'
  return name.split(' ').map(n => n[0]).join('').slice(0, 2).toUpperCase()
}

const totalPages = computed(() => Math.ceil(totalStudents.value / PER_PAGE))
const rangeStart = computed(() => (page.value - 1) * PER_PAGE + 1)
const rangeEnd = computed(() => Math.min(page.value * PER_PAGE, totalStudents.value))
</script>

<template>
  <div>
    <SummaryStats :api="api" :term-id="termId" />

    <div class="grid grid-cols-1 md:grid-cols-[1fr_2fr] gap-6">
      <!-- Left: Student list -->
      <div>
        <div class="mb-4">
          <div class="flex items-center gap-3">
            <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">Students</h2>
            <span v-if="totalStudents > 0" class="font-body text-xs text-csub-gray bg-gray-100 rounded-full px-2.5 py-0.5">{{ totalStudents }} total</span>
            <div class="flex-1 h-px bg-gray-200" />
          </div>
          <p class="font-body text-xs text-csub-gray mt-1">Select a student to view their progress and manage step completions</p>
        </div>

        <!-- Search bar -->
        <div class="relative mb-3">
          <div class="absolute inset-y-0 left-0 pl-3.5 flex items-center pointer-events-none">
            <svg class="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
              <path stroke-linecap="round" stroke-linejoin="round" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
          </div>
          <input
            type="text" v-model="search"
            @keydown.enter="handleSearch"
            placeholder="Search by name or email..."
            class="w-full pl-10 pr-24 py-2.5 rounded-xl border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
          />
          <div class="absolute inset-y-0 right-0 pr-1.5 flex items-center gap-1">
            <button v-if="search" @click="handleSearchClear" class="text-gray-400 hover:text-gray-600 p-1 transition-colors" title="Clear search">
              <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
                <path stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
            <button @click="handleSearch" :disabled="loading" class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-3 py-1.5 rounded-lg shadow transition-colors text-xs disabled:opacity-50">
              {{ loading ? '...' : 'Search' }}
            </button>
          </div>
        </div>

        <!-- Toolbar -->
        <div class="flex items-center gap-2 mb-3">
          <select :value="sortBy" @change="handleSortChange(($event.target as HTMLSelectElement).value)" class="px-3 py-1.5 rounded-lg border border-gray-300 font-body text-xs focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent bg-white">
            <option v-for="opt in SORT_OPTIONS" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
          </select>
          <button @click="handleOverdueToggle" :class="`flex items-center gap-1.5 px-3 py-1.5 rounded-lg border font-body text-xs font-medium transition-all ${overdueOnly ? 'border-red-300 bg-red-50 text-red-700' : 'border-gray-300 bg-white text-csub-gray hover:border-red-200 hover:text-red-600'}`">
            <svg class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
              <path stroke-linecap="round" stroke-linejoin="round" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4.5c-.77-.833-2.694-.833-3.464 0L3.34 16.5c-.77.833.192 2.5 1.732 2.5z" />
            </svg>
            Overdue
          </button>
        </div>

        <!-- Student list -->
        <div class="space-y-2 max-h-[55vh] overflow-y-auto pr-1">
          <p v-if="!loading && students.length === 0" class="font-body text-sm text-csub-gray text-center py-6">{{ search ? 'No students found.' : 'No students in this term yet.' }}</p>
          <p v-if="loading && students.length === 0" class="font-body text-sm text-csub-gray text-center py-6">Loading students...</p>
          <button
            v-for="s in students"
            :key="s.id"
            @click="selectedStudent = s"
            :class="`w-full text-left px-4 py-3 rounded-xl border transition-all duration-150 group ${selectedStudent?.id === s.id ? 'border-csub-blue bg-csub-blue/5 shadow-sm' : 'border-gray-200 bg-white hover:border-csub-blue/30 hover:shadow-sm'}`"
          >
            <div class="flex items-center gap-3">
              <div class="w-8 h-8 rounded-full bg-csub-blue/10 flex items-center justify-center text-csub-blue font-display text-xs font-bold flex-shrink-0">{{ getInitials(s.display_name) }}</div>
              <div class="flex-1 min-w-0">
                <p class="font-body text-sm font-semibold text-csub-blue-dark">{{ s.display_name }}</p>
                <p class="font-body text-xs text-csub-gray">
                  {{ s.email }}
                  <span v-if="s.emplid" class="ml-2">&middot; Student ID # {{ s.emplid }}</span>
                </p>
                <div class="flex flex-wrap gap-1 mt-1">
                  <span v-if="s.applicant_type" class="text-[10px] bg-gray-100 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">{{ s.applicant_type }}</span>
                  <span v-if="s.residency" class="text-[10px] bg-gray-100 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">{{ s.residency }}</span>
                </div>
              </div>
              <div class="flex items-center gap-2 flex-shrink-0">
                <span v-if="s.overdue_step_count > 0" class="inline-flex items-center text-[10px] font-body font-semibold text-red-600 bg-red-50 rounded-full px-1.5 py-0.5">{{ s.overdue_step_count }} overdue</span>
                <span class="font-body text-xs text-csub-gray">{{ s.completed_steps }}/{{ totalActiveSteps }}</span>
              </div>
            </div>
            <div class="w-full h-1.5 bg-gray-100 rounded-full mt-2 overflow-hidden">
              <div class="h-full rounded-full transition-all duration-300" :style="{ width: `${totalActiveSteps > 0 ? Math.round((s.completed_steps / totalActiveSteps) * 100) : 0}%`, background: (totalActiveSteps > 0 ? Math.round((s.completed_steps / totalActiveSteps) * 100) : 0) === 100 ? 'linear-gradient(90deg, #003594, #FFC72C)' : 'linear-gradient(90deg, #003594, #0052CC)' }" />
            </div>
          </button>
        </div>

        <!-- Pagination -->
        <div v-if="totalStudents > 0" class="flex items-center justify-between mt-3 pt-3 border-t border-gray-100">
          <button @click="page = Math.max(1, page - 1)" :disabled="page <= 1" class="flex items-center gap-1 font-body text-xs font-semibold text-csub-blue hover:text-csub-blue-dark disabled:text-gray-300 disabled:cursor-not-allowed transition-colors">
            <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M15 19l-7-7 7-7" /></svg>
            Prev
          </button>
          <span class="font-body text-xs text-csub-gray">{{ totalStudents > 0 ? `${rangeStart}–${rangeEnd} of ${totalStudents}` : '0 results' }}</span>
          <button @click="page = Math.min(totalPages, page + 1)" :disabled="page >= totalPages" class="flex items-center gap-1 font-body text-xs font-semibold text-csub-blue hover:text-csub-blue-dark disabled:text-gray-300 disabled:cursor-not-allowed transition-colors">
            Next
            <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M9 5l7 7-7 7" /></svg>
          </button>
        </div>
      </div>

      <!-- Right: Student detail -->
      <div class="md:sticky md:top-4 md:self-start">
        <StudentDetail :student="selectedStudent" :steps="steps" :api="api" :role="role" @progress-change="refreshStudents" />
      </div>
    </div>
  </div>
</template>
