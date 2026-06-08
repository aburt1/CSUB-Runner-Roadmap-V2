<script setup lang="ts">
interface AuditLog {
  id: number
  entity_type: string
  action: string
  changed_by: string
  created_at: string
  details: string | Record<string, any>
}

interface ActionMeta {
  label: string
  color: string
}

const ACTION_META: Record<string, ActionMeta> = {
  complete: { label: 'Step completed', color: 'bg-green-500' },
  waive: { label: 'Step waived', color: 'bg-amber-500' },
  uncomplete: { label: 'Step marked incomplete', color: 'bg-red-400' },
  student_optional_complete: { label: 'Optional step completed by student', color: 'bg-green-600' },
  student_optional_uncomplete: { label: 'Optional step marked incomplete by student', color: 'bg-orange-500' },
  tags_update: { label: 'Tags updated', color: 'bg-indigo-500' },
  step_create: { label: 'Step created', color: 'bg-csub-blue' },
  step_update: { label: 'Step updated', color: 'bg-sky-600' },
  step_delete: { label: 'Step deactivated', color: 'bg-red-500' },
  step_restore: { label: 'Step restored', color: 'bg-green-600' },
  term_create: { label: 'Term created', color: 'bg-violet-600' },
  term_update: { label: 'Term updated', color: 'bg-violet-500' },
  term_delete: { label: 'Term deleted', color: 'bg-red-700' },
  admin_create: { label: 'Admin user created', color: 'bg-slate-700' },
  admin_update: { label: 'Admin user updated', color: 'bg-slate-500' },
  student_profile_update: { label: 'Student profile updated', color: 'bg-cyan-600' },
}

function parseDetails(details: string | Record<string, any> | null | undefined): Record<string, any> {
  if (!details) return {}
  try {
    return typeof details === 'string' ? JSON.parse(details) : details
  } catch {
    return {}
  }
}

function formatTime(ts: string): string {
  const d = new Date(ts.endsWith('Z') ? ts : ts + 'Z')
  const now = new Date()
  const diffMs = now.getTime() - d.getTime()
  const diffMins = Math.floor(diffMs / 60000)
  if (diffMins < 1) return 'Just now'
  if (diffMins < 60) return `${diffMins}m ago`
  const diffHours = Math.floor(diffMins / 60)
  if (diffHours < 24) return `${diffHours}h ago`
  return d.toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })
}

function prettyEntity(entityType: string): string {
  return entityType.replace(/_/g, ' ')
}

function formatArray(value: unknown): string {
  if (!Array.isArray(value) || value.length === 0) return 'none'
  return value.join(', ')
}

function getSummary(log: AuditLog, details: Record<string, any>): string {
  const fallback = ACTION_META[log.action]?.label || log.action.replace(/_/g, ' ')

  switch (log.action) {
    case 'complete':
    case 'waive':
    case 'uncomplete':
    case 'student_optional_complete':
    case 'student_optional_uncomplete':
      return `${fallback}${details.stepTitle ? `: ${details.stepTitle}` : ''}${details.studentName ? ` for ${details.studentName}` : ''}`
    case 'tags_update':
      return `Updated tags${details.studentName ? ` for ${details.studentName}` : ''}`
    case 'step_create':
    case 'step_update':
    case 'step_delete':
    case 'step_restore':
      return `${fallback}${details.title ? `: ${details.title}` : ''}`
    case 'term_create':
    case 'term_delete':
      return `${fallback}${details.name ? `: ${details.name}` : ''}`
    case 'term_update':
      return `${fallback}${details.name ? `: ${details.name}` : ''}`
    case 'admin_create':
    case 'admin_update':
      return `${fallback}${details.email ? `: ${details.email}` : ''}`
    case 'student_profile_update':
      return `${fallback}${details.studentName ? `: ${details.studentName}` : ''}`
    default:
      return fallback
  }
}

function getDetailRows(_log: AuditLog, details: Record<string, any>): [string, string][] {
  const rows: [string, string][] = []

  if (details.studentName) rows.push(['Student', details.studentName])
  if (details.emplid) rows.push(['Student ID #', details.emplid])
  if (details.student_id_number) rows.push(['Student ID #', details.student_id_number])
  if (details.stepTitle) rows.push(['Step', details.stepTitle])
  if (details.step_key) rows.push(['Step Key', details.step_key])
  if (details.title && !details.stepTitle) rows.push(['Title', details.title])
  if (details.name) rows.push(['Term', details.name])
  if (details.email) rows.push(['Email', details.email])
  if (details.role) rows.push(['Role', details.role])
  if (details.displayName) rows.push(['Display Name', details.displayName])
  if (details.note) rows.push(['Note', details.note])
  if (details.oldTags || details.newTags) {
    rows.push(['Tags', `${formatArray(details.oldTags)} -> ${formatArray(details.newTags)}`])
  }
  if (details.fields?.length) rows.push(['Fields', details.fields.join(', ')])
  if (details.duplicatedFrom) rows.push(['Duplicated From', `Step ${details.duplicatedFrom}`])
  if (details.clonedFrom) rows.push(['Cloned From', `Term ${details.clonedFrom}`])
  if (details.stepCount !== undefined) rows.push(['Step Count', String(details.stepCount)])
  if (details.deletedStepCount !== undefined) rows.push(['Deleted Steps', String(details.deletedStepCount)])
  if (details.bulk) rows.push(['Bulk Change', 'Yes'])

  return rows
}

defineProps<{
  logs: AuditLog[]
}>()
</script>

<template>
  <p v-if="!logs || logs.length === 0" class="font-body text-sm text-csub-gray text-center py-4">No audit entries yet.</p>

  <div v-else class="space-y-3">
    <template v-for="log in logs" :key="log.id">
      <div class="bg-white border border-gray-200 rounded-xl shadow-sm p-4">
        <div class="flex gap-3 items-start">
          <div :class="`w-2.5 h-2.5 rounded-full mt-1.5 flex-shrink-0 ${(ACTION_META[log.action] || { label: log.action, color: 'bg-gray-400' }).color}`" />
          <div class="flex-1 min-w-0">
            <div class="flex flex-wrap items-center gap-2 mb-1.5">
              <p class="font-body text-sm font-semibold text-csub-blue-dark">
                {{ getSummary(log, parseDetails(log.details)) }}
              </p>
              <span class="text-[10px] uppercase tracking-wider font-display font-bold text-csub-blue-dark bg-csub-blue/10 px-2 py-0.5 rounded-full">
                {{ prettyEntity(log.entity_type) }}
              </span>
              <span class="text-[10px] uppercase tracking-wider font-display font-bold text-gray-600 bg-gray-100 px-2 py-0.5 rounded-full">
                {{ log.action.replace(/_/g, ' ') }}
              </span>
            </div>

            <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-csub-gray mb-2">
              <span>By {{ log.changed_by }}</span>
              <span>{{ formatTime(log.created_at) }}</span>
            </div>

            <div v-if="getDetailRows(log, parseDetails(log.details)).length > 0" class="grid grid-cols-1 sm:grid-cols-2 gap-2 mt-2">
              <div v-for="[label, value] in getDetailRows(log, parseDetails(log.details))" :key="`${log.id}-${label}`" class="bg-gray-50 rounded-lg px-3 py-2">
                <p class="font-body text-[10px] uppercase tracking-wide text-csub-gray">{{ label }}</p>
                <p class="font-body text-xs text-csub-blue-dark mt-0.5 break-words">{{ value }}</p>
              </div>
            </div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>
