<script setup lang="ts">
import { ref, computed } from 'vue'
import NoteModal from './NoteModal.vue'
import { useToastStore } from '../../stores/toast'
import type { AdminApi } from '../../composables/useAdminApi'

const toast = useToastStore()

type ModalAction = 'complete' | 'waive' | 'uncomplete'

const props = withDefaults(
  defineProps<{
    studentId: number
    stepId: number
    stepTitle: string
    stepIcon: string
    status: string | null
    completedAt: string | null
    note: string | null
    api: AdminApi
    readOnly?: boolean
    isOptional?: boolean
  }>(),
  {
    readOnly: false,
    isOptional: false,
  },
)

const emit = defineEmits<{
  (e: 'toggle', stepId: number, newStatus: string | null, note: string | null): void
}>()

const loading = ref(false)
const showModal = ref<ModalAction | null>(null)

const isDone = computed(() => props.status === 'completed' || props.status === 'waived')

const handleConfirm = async (note: string | null) => {
  const action = showModal.value
  showModal.value = null
  loading.value = true
  try {
    if (action === 'uncomplete') {
      await props.api.del(`/students/${props.studentId}/steps/${props.stepId}/complete`, { note })
      emit('toggle', props.stepId, null, note)
    } else {
      const progressStatus = action === 'waive' ? 'waived' : 'completed'
      await props.api.post(`/students/${props.studentId}/steps/${props.stepId}/complete`, {
        note,
        status: progressStatus,
      })
      emit('toggle', props.stepId, progressStatus, note)
    }
  } catch {
    // The emit (and any parent state change) only happens on success above, so
    // there's nothing to roll back — just tell the admin it didn't take.
    toast.error('Could not update that step. Please try again.')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div
    :class="`rounded-xl border transition-all duration-150 ${
      status === 'completed'
        ? 'border-csub-gold bg-csub-gold-light/30'
        : status === 'waived'
          ? 'border-slate-300 bg-slate-50'
          : 'border-gray-200 bg-white'
    }`"
  >
    <div class="flex items-center gap-3 px-4 py-3">
      <!-- Status indicator -->
      <div
        :class="`w-6 h-6 rounded-full border-2 flex items-center justify-center text-xs font-bold flex-shrink-0 ${
          status === 'completed'
            ? 'bg-csub-gold border-csub-gold text-csub-blue-dark'
            : status === 'waived'
              ? 'bg-slate-200 border-slate-300 text-slate-500'
              : 'border-gray-300 text-transparent'
        }`"
      >
        {{ status === 'completed' ? '✓' : status === 'waived' ? '—' : '' }}
      </div>

      <span class="text-base flex-shrink-0" aria-hidden="true">{{ stepIcon }}</span>

      <div class="flex-1 min-w-0">
        <span
          :class="`font-body text-sm ${isDone ? 'text-csub-blue-dark font-semibold' : 'text-csub-gray'}`"
        >
          {{ stepTitle }}
        </span>
        <div class="flex items-center gap-2 mt-0.5">
          <span
            v-if="isOptional"
            class="inline-flex items-center text-[10px] font-body font-semibold text-csub-blue bg-csub-blue/10 rounded px-1.5 py-0.5"
          >
            Optional
          </span>
          <span
            v-if="status === 'completed'"
            class="inline-flex items-center text-[10px] font-body font-semibold text-emerald-600 bg-emerald-50 rounded px-1.5 py-0.5"
          >
            Completed
          </span>
          <span
            v-if="status === 'waived'"
            class="inline-flex items-center text-[10px] font-body font-semibold text-slate-500 bg-slate-100 rounded px-1.5 py-0.5"
          >
            Waived
          </span>
          <span v-if="isDone && completedAt" class="font-body text-[10px] text-csub-gray">
            {{
              new Date(completedAt).toLocaleDateString('en-US', {
                month: 'short',
                day: 'numeric',
                year: 'numeric',
              })
            }}
          </span>
        </div>
        <p v-if="note" class="font-body text-[10px] text-csub-gray/70 mt-0.5 italic truncate">
          Note: {{ note }}
        </p>
      </div>

      <!-- Action buttons -->
      <div v-if="!readOnly" class="flex items-center gap-1 flex-shrink-0">
        <button
          v-if="isDone"
          @click="showModal = 'uncomplete'"
          :disabled="loading"
          class="font-body text-xs text-red-500 hover:text-red-700 font-semibold px-2 py-1 rounded hover:bg-red-50 transition-colors disabled:opacity-50"
        >
          Undo
        </button>
        <template v-else>
          <button
            @click="showModal = 'complete'"
            :disabled="loading"
            class="font-body text-xs text-csub-blue hover:text-csub-blue-dark font-semibold px-2 py-1 rounded hover:bg-csub-blue/5 transition-colors disabled:opacity-50"
          >
            Complete
          </button>
          <button
            @click="showModal = 'waive'"
            :disabled="loading"
            class="font-body text-xs text-slate-500 hover:text-slate-700 font-semibold px-2 py-1 rounded hover:bg-slate-50 transition-colors disabled:opacity-50"
          >
            Waive
          </button>
        </template>
      </div>
    </div>
  </div>

  <NoteModal
    v-if="showModal"
    :step-title="stepTitle"
    :step-icon="stepIcon"
    :action="showModal"
    @confirm="handleConfirm"
    @cancel="showModal = null"
  />
</template>
