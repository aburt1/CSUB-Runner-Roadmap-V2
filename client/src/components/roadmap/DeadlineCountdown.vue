<script setup lang="ts">
import { computed } from 'vue'
import type { StepStatus } from '../../types/api'

type DeadlineLevel = 'overdue' | 'urgent' | 'warning' | 'normal'

interface DeadlineInfo {
  text: string
  level: DeadlineLevel
}

const props = defineProps<{
  deadlineDate: string | null
  status: StepStatus
}>()

const info = computed<DeadlineInfo | null>(() => {
  if (!props.deadlineDate || props.status === 'completed' || props.status === 'waived') return null

  const now = new Date()
  now.setHours(0, 0, 0, 0)
  const deadline = new Date(props.deadlineDate + 'T00:00:00')
  const diffMs = deadline.getTime() - now.getTime()
  const days = Math.ceil(diffMs / 86400000)

  if (days < 0) {
    return { text: `${Math.abs(days)}d overdue`, level: 'overdue' }
  } else if (days === 0) {
    return { text: 'Due today', level: 'urgent' }
  } else if (days <= 7) {
    return { text: `${days}d left`, level: 'urgent' }
  } else if (days <= 14) {
    return { text: `${days}d left`, level: 'warning' }
  }
  return { text: `${days}d left`, level: 'normal' }
})

const styles: Record<DeadlineLevel, string> = {
  overdue: 'bg-red-100 text-red-700 border-red-200',
  urgent: 'bg-amber-100 text-amber-700 border-amber-200',
  warning: 'bg-yellow-50 text-yellow-700 border-yellow-200',
  normal: 'bg-gray-50 text-gray-500 border-gray-200',
}
</script>

<template>
  <span
    v-if="info"
    :class="`inline-flex items-center text-xs font-body font-semibold rounded-full px-2 py-0.5 border ${styles[info.level]}`"
    aria-live="polite"
  >
    <svg
      v-if="info.level === 'overdue'"
      class="w-3 h-3 mr-0.5"
      fill="currentColor"
      viewBox="0 0 20 20"
      aria-hidden="true"
    >
      <path
        fill-rule="evenodd"
        d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z"
        clip-rule="evenodd"
      />
    </svg>
    {{ info.text }}
  </span>
</template>
