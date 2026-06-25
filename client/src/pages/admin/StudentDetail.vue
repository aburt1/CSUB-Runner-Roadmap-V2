<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import TagEditor from './TagEditor.vue'
import StepToggle from './StepToggle.vue'
import AuditTimeline from './AuditTimeline.vue'
import { useToastStore } from '../../stores/toast'
import type { AdminApi } from '../../composables/useAdminApi'
import { parseMaybeJson } from '../../utils/json'
import type { AuditLog } from '../../types/api'
import { getInitials } from '../../utils/initials'

const toast = useToastStore()

interface Student {
  id: number
  display_name: string
  email: string
}

interface Step {
  id: number
  title: string
  icon: string | null
  is_optional: number
}

interface ProgressInfo {
  completed_at: string | null
  status: string
  note: string | null
}

interface StudentProfile {
  emplid?: string
  preferred_name?: string
  phone?: string
  applicant_type?: string
  major?: string
  residency?: string
  admit_term?: string
  created_at?: string
  last_synced_at?: string
  tags?: string | string[]
  [key: string]: unknown
}

interface StudentProgressItem {
  step_id: number
  completed_at: string | null
  status: string | null
  note?: string | null
}

interface StudentProgressResponse {
  progress: StudentProgressItem[]
  manualTags?: string[]
  derivedTags?: string[]
  student?: StudentProfile
}

interface ProfileField {
  key: string
  label: string
}

const props = withDefaults(
  defineProps<{
    student: Student | null
    steps: Step[]
    api: AdminApi
    role?: string
  }>(),
  {
    role: 'viewer',
  },
)

const emit = defineEmits<{
  (e: 'progressChange'): void
}>()

const parseTags = (rawTags: unknown): string[] => parseMaybeJson(rawTags, [])

const PROFILE_FIELDS: ProfileField[] = [
  { key: 'emplid', label: 'Student ID #' },
  { key: 'preferred_name', label: 'Preferred Name' },
  { key: 'phone', label: 'Phone' },
  { key: 'applicant_type', label: 'Applicant Type' },
  { key: 'major', label: 'Major' },
  { key: 'residency', label: 'Residency' },
  { key: 'admit_term', label: 'Admit Term' },
]

function displayDate(value: string | null | undefined, withTime = false): string {
  if (!value) return 'Not set'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return withTime
    ? date.toLocaleString('en-US', {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
      })
    : date.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })
}

const canEdit = computed(
  () =>
    props.role === 'admissions' || props.role === 'admissions_editor' || props.role === 'sysadmin',
)
const progress = ref<Map<number, ProgressInfo>>(new Map())
const studentTags = ref<string[]>([])
const derivedTags = ref<string[]>([])
const auditLogs = ref<AuditLog[]>([])
const studentProfile = ref<StudentProfile | null>(null)

watch(
  () => [props.student, props.api] as const,
  () => {
    const student = props.student
    if (!student) return

    props.api
      .get<StudentProgressResponse>(`/students/${student.id}/progress`)
      .then((data) => {
        // Stale-response guard: the admin may have switched students while this
        // request was in flight — rendering it now would show student A's progress
        // under student B's name.
        if (props.student?.id !== student.id) return
        const map = new Map<number, ProgressInfo>()
        for (const p of data.progress) {
          map.set(p.step_id, {
            completed_at: p.completed_at,
            status: p.status || 'completed',
            note: p.note || null,
          })
        }
        progress.value = map
        studentTags.value = data.manualTags || parseTags(data.student?.tags)
        derivedTags.value = data.derivedTags || []
        studentProfile.value = data.student || null
      })
      .catch(() => {
        toast.error('Could not load student progress. Please try again.')
      })

    props.api
      .get<{ logs: AuditLog[] }>(`/audit?studentId=${student.id}&limit=20`)
      .then((data) => {
        if (props.student?.id !== student.id) return
        auditLogs.value = data.logs
      })
      .catch(() => {})
  },
  { immediate: true },
)

const refreshAudit = () => {
  props.api
    .get<{ logs: AuditLog[] }>(`/audit?studentId=${props.student!.id}&limit=20`)
    .then((data) => {
      auditLogs.value = data.logs
    })
    .catch(() => {})
}

const handleStepToggle = (stepId: number, newStatus: string | null, note: string | null = null) => {
  const next = new Map(progress.value)
  if (newStatus === null) {
    next.delete(stepId)
  } else {
    next.set(stepId, {
      completed_at: new Date().toISOString(),
      status: newStatus,
      note,
    })
  }
  progress.value = next
  emit('progressChange')
  refreshAudit()
}

let tagSaveSeq = 0
const saveTags = async (newTags: string[]) => {
  // Optimistically show the new tags, but remember the old set so we can roll
  // back (and tell the admin) if the save fails. The sequence number stops a
  // SLOW failure from rolling the UI back past a NEWER successful save.
  const previousTags = studentTags.value
  const seq = ++tagSaveSeq
  studentTags.value = newTags
  try {
    await props.api.put(`/students/${props.student!.id}/tags`, { tags: newTags })
    refreshAudit()
  } catch {
    if (seq === tagSaveSeq) studentTags.value = previousTags
    toast.error('Could not save tags. Please try again.')
  }
}

const requiredSteps = computed(() => props.steps.filter((step) => step.is_optional !== 1))
const optionalSteps = computed(() => props.steps.filter((step) => step.is_optional === 1))
const doneCount = computed(
  () =>
    requiredSteps.value.filter((step) => {
      const value = progress.value.get(step.id)
      return value?.status === 'completed' || value?.status === 'waived'
    }).length,
)
const optionalDoneCount = computed(
  () =>
    optionalSteps.value.filter((step) => {
      const value = progress.value.get(step.id)
      return value?.status === 'completed' || value?.status === 'waived'
    }).length,
)
const totalCount = computed(() => requiredSteps.value.length)
const pct = computed(() =>
  totalCount.value > 0 ? Math.round((doneCount.value / totalCount.value) * 100) : 0,
)
</script>

<template>
  <div v-if="!student" class="flex items-center justify-center h-full">
    <p class="font-body text-sm text-csub-gray">
      Select a student to manage their progress, imported profile, and tags.
    </p>
  </div>

  <div v-else>
    <div class="bg-white rounded-xl border border-gray-200 shadow-xs p-5 mb-5">
      <div class="flex items-center gap-4">
        <div
          class="w-12 h-12 rounded-full bg-csub-blue flex items-center justify-center text-white font-display font-bold text-lg shrink-0"
        >
          {{ getInitials(student.display_name) }}
        </div>
        <div class="flex-1 min-w-0">
          <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
            {{ studentProfile?.preferred_name || student.display_name }}
          </h2>
          <p class="font-body text-sm text-csub-gray">{{ student.email }}</p>
          <div class="flex flex-wrap items-center gap-2 mt-1">
            <span
              v-if="studentProfile?.emplid"
              class="text-[10px] bg-csub-blue/10 text-csub-blue-dark rounded-full px-2 py-0.5 font-body font-semibold"
            >
              Student ID # {{ studentProfile.emplid }}
            </span>
            <span
              v-if="studentProfile?.applicant_type"
              class="text-[10px] bg-gray-100 text-csub-blue-dark rounded-full px-2 py-0.5 font-body font-semibold"
              >{{ studentProfile.applicant_type }}</span
            >
            <span
              v-if="studentProfile?.residency"
              class="text-[10px] bg-gray-100 text-csub-blue-dark rounded-full px-2 py-0.5 font-body font-semibold"
              >{{ studentProfile.residency }}</span
            >
          </div>
          <p v-if="studentProfile?.created_at" class="font-body text-xs text-csub-gray mt-1">
            Registered {{ displayDate(studentProfile.created_at) }}
          </p>
        </div>
      </div>
    </div>

    <div class="bg-white rounded-xl border border-gray-200 shadow-xs p-5 mb-5">
      <div class="flex items-center justify-between mb-4">
        <div>
          <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide">
            Imported Profile
          </h3>
        </div>
        <span
          v-if="studentProfile?.last_synced_at"
          class="font-body text-[10px] text-csub-gray bg-gray-100 rounded-full px-2 py-1"
          >Synced {{ displayDate(studentProfile.last_synced_at, true) }}</span
        >
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div
          v-for="f in PROFILE_FIELDS"
          :key="f.key"
          class="bg-gray-50 rounded-lg px-3 py-3 border border-gray-200"
        >
          <p class="font-body text-[10px] uppercase tracking-wide text-csub-gray">{{ f.label }}</p>
          <p class="font-body text-sm text-csub-blue-dark mt-1 wrap-break-word">
            {{ studentProfile?.[f.key] || 'Not set' }}
          </p>
        </div>
      </div>
    </div>

    <div v-if="canEdit" class="mb-5">
      <label class="font-body text-xs font-semibold text-csub-blue-dark block mb-1"
        >Manual Tags</label
      >
      <TagEditor :tags="studentTags" @change="saveTags" />
    </div>

    <div v-if="derivedTags.length > 0" class="mb-5">
      <label class="font-body text-xs font-semibold text-csub-blue-dark block mb-2"
        >Derived Tags</label
      >
      <div class="flex flex-wrap gap-1">
        <span
          v-for="tag in derivedTags"
          :key="tag"
          class="inline-flex items-center bg-amber-50 text-amber-800 text-xs font-body font-semibold px-2 py-1 rounded-full"
          >{{ tag }}</span
        >
      </div>
    </div>

    <div class="mb-5">
      <div class="flex items-center justify-between mb-1.5">
        <span class="font-body text-xs text-csub-blue-dark font-semibold"
          >{{ doneCount }} of {{ totalCount }} required steps done</span
        >
        <span class="font-display text-sm font-bold text-csub-blue">{{ pct }}%</span>
      </div>
      <div class="w-full h-2.5 bg-gray-100 rounded-full overflow-hidden">
        <div
          class="h-full rounded-full transition-all duration-500"
          :style="{
            width: `${pct}%`,
            background:
              pct === 100
                ? 'linear-gradient(90deg, #003594, #FFC72C)'
                : 'linear-gradient(90deg, #003594, #0052CC)',
          }"
        />
      </div>
      <p v-if="optionalSteps.length > 0" class="font-body text-xs text-csub-gray mt-2">
        Optional opportunities:
        <span class="font-semibold text-csub-blue-dark">{{ optionalDoneCount }}</span> of
        {{ optionalSteps.length }}
      </p>
    </div>

    <div class="flex items-center gap-3 mb-3">
      <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide">
        Steps
      </h3>
      <div class="flex-1 h-px bg-gray-200" />
    </div>
    <div class="space-y-2 mb-6">
      <StepToggle
        v-for="step in requiredSteps"
        :key="step.id"
        :student-id="student.id"
        :step-id="step.id"
        :step-title="step.title"
        :step-icon="step.icon || '📋'"
        :status="progress.get(step.id)?.status || null"
        :completed-at="progress.get(step.id)?.completed_at || null"
        :note="progress.get(step.id)?.note || null"
        :api="api"
        :read-only="!canEdit"
        :is-optional="false"
        @toggle="handleStepToggle"
      />
    </div>

    <template v-if="optionalSteps.length > 0">
      <div class="flex items-center gap-3 mb-3">
        <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide">
          Optional Opportunities
        </h3>
        <div class="flex-1 h-px bg-gray-200" />
      </div>
      <div class="space-y-2 mb-6">
        <StepToggle
          v-for="step in optionalSteps"
          :key="step.id"
          :student-id="student.id"
          :step-id="step.id"
          :step-title="step.title"
          :step-icon="step.icon || '📋'"
          :status="progress.get(step.id)?.status || null"
          :completed-at="progress.get(step.id)?.completed_at || null"
          :note="progress.get(step.id)?.note || null"
          :api="api"
          :read-only="!canEdit"
          :is-optional="true"
          @toggle="handleStepToggle"
        />
      </div>
    </template>

    <div class="flex items-center gap-3 mb-3">
      <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide">
        Recent Activity
      </h3>
      <div class="flex-1 h-px bg-gray-200" />
    </div>
    <AuditTimeline :logs="auditLogs" />
  </div>
</template>
