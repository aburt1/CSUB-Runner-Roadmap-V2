<script setup lang="ts">
import { ref } from 'vue'

type ActionType = 'complete' | 'waive' | 'uncomplete'

interface ActionConfig {
  heading: string
  buttonClass: string
}

const ACTION_CONFIG: Record<ActionType, ActionConfig> = {
  complete: { heading: 'Mark Complete', buttonClass: 'bg-csub-blue hover:bg-csub-blue-dark text-white' },
  waive: { heading: 'Mark as Waived', buttonClass: 'bg-slate-600 hover:bg-slate-700 text-white' },
  uncomplete: { heading: 'Mark Incomplete', buttonClass: 'bg-red-500 hover:bg-red-600 text-white' },
}

const props = defineProps<{
  stepTitle: string
  stepIcon: string
  action: ActionType
}>()

const emit = defineEmits<{
  (e: 'confirm', note: string | null): void
  (e: 'cancel'): void
}>()

const note = ref('')
const config = ACTION_CONFIG[props.action] || ACTION_CONFIG.complete
</script>

<template>
  <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40" @click="emit('cancel')">
    <div
      class="bg-white rounded-xl shadow-xl max-w-sm w-full mx-4 p-5"
      @click.stop
    >
      <h3 class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-3">
        {{ config.heading }}
      </h3>
      <div class="flex items-center gap-2 mb-4">
        <span class="text-lg">{{ stepIcon || '📋' }}</span>
        <span class="font-body text-sm text-csub-blue-dark font-semibold">{{ stepTitle }}</span>
      </div>
      <p v-if="action === 'waive'" class="font-body text-xs text-csub-gray mb-3">
        Waiving a step means the student does not need to complete it. This is different from marking it complete.
      </p>
      <label class="block font-body text-xs font-semibold text-csub-gray mb-1">
        Note (optional)
      </label>
      <textarea
        v-model="note"
        :rows="2"
        :placeholder="action === 'waive' ? 'Reason for waiving...' : 'Reason for this change...'"
        class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-1 focus:ring-csub-blue mb-4 resize-none"
      />
      <div class="flex gap-2 justify-end">
        <button
          @click="emit('cancel')"
          class="border border-gray-300 text-csub-gray hover:text-csub-blue-dark font-body text-sm px-4 py-2 rounded-lg transition-colors"
        >
          Cancel
        </button>
        <button
          @click="emit('confirm', note.trim() || null)"
          :class="`font-display font-bold text-sm uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors ${config.buttonClass}`"
        >
          Confirm
        </button>
      </div>
    </div>
  </div>
</template>
