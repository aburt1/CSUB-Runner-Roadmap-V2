<script setup lang="ts">
import { ref, watch } from 'vue'
import { errorMessage } from '../../utils/errors'

interface Term {
  id: number
  name: string
  is_active: number
  start_date: string
  end_date: string
  step_count?: number
  student_count?: number
}

interface TermForm {
  name: string
  start_date: string
  end_date: string
}

interface Props {
  term: Term | null
  canEdit: boolean
  // NOTE: callback props (not emits) are used here so the parent can await the
  // save/delete operations and show a saving spinner during the async request.
  onSave: (termId: number, data: Partial<TermForm & { is_active: number }>) => Promise<void>
  onDelete: (term: Term) => void
}

const props = defineProps<Props>()

const editing = ref(false)
const form = ref<TermForm>({ name: '', start_date: '', end_date: '' })
const saving = ref(false)
const error = ref('')

watch(
  () => props.term,
  (term) => {
    if (!term) return
    form.value = {
      name: term.name || '',
      start_date: term.start_date || '',
      end_date: term.end_date || '',
    }
    editing.value = false
    error.value = ''
  },
  { immediate: true },
)

const handleSubmit = async () => {
  if (!props.term) return
  saving.value = true
  error.value = ''
  try {
    await props.onSave(props.term.id, form.value)
    editing.value = false
  } catch (err) {
    error.value = errorMessage(err, 'Could not save the term. Please try again.')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div v-if="term" class="bg-white rounded-xl border border-gray-200 shadow-sm p-5">
    <form v-if="editing" @submit.prevent="handleSubmit" class="space-y-4">
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
            >Term Name</label
          >
          <input
            type="text"
            required
            v-model="form.name"
            class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
          />
        </div>
        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
            >Start Date</label
          >
          <input
            type="date"
            v-model="form.start_date"
            class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
          />
        </div>
        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
            >End Date</label
          >
          <input
            type="date"
            v-model="form.end_date"
            class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
          />
        </div>
      </div>
      <p v-if="error" class="font-body text-sm text-red-600">{{ error }}</p>
      <div class="flex items-center gap-3">
        <button
          type="submit"
          :disabled="saving"
          class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display text-sm font-bold uppercase tracking-wider px-5 py-2 rounded-lg shadow transition-colors disabled:opacity-50"
        >
          {{ saving ? 'Saving...' : 'Save Term' }}
        </button>
        <button
          type="button"
          @click="editing = false"
          class="font-body text-sm text-csub-gray hover:text-csub-blue-dark transition-colors"
        >
          Cancel
        </button>
      </div>
    </form>
    <div v-else class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
      <div>
        <div class="flex items-center gap-3">
          <h2 class="font-display text-xl font-bold text-csub-blue-dark uppercase tracking-wide">
            {{ term.name }}
          </h2>
          <span
            v-if="term.is_active"
            class="text-[10px] font-body font-semibold text-emerald-600 bg-emerald-50 rounded-full px-2 py-0.5"
            >Active</span
          >
          <span
            v-else
            class="text-[10px] font-body font-semibold text-gray-500 bg-gray-100 rounded-full px-2 py-0.5"
            >Inactive</span
          >
        </div>
        <p class="font-body text-sm text-csub-gray mt-1">
          {{ term.start_date || 'No start date' }} - {{ term.end_date || 'No end date' }}
        </p>
        <p class="font-body text-xs text-csub-gray mt-1">
          {{ term.step_count || 0 }} steps · {{ term.student_count || 0 }} students
        </p>
      </div>
      <div v-if="canEdit" class="flex items-center gap-2">
        <button
          v-if="!term.is_active"
          @click="onSave(term.id, { is_active: 1 })"
          class="font-body text-xs font-semibold text-green-700 hover:text-green-900 px-2 py-1 rounded transition-colors"
        >
          Set Active
        </button>
        <button
          @click="editing = true"
          class="font-body text-xs text-csub-blue hover:text-csub-blue-dark px-2 py-1 rounded transition-colors"
        >
          Edit Term
        </button>
        <button
          @click="onDelete(term)"
          class="font-body text-xs text-red-600 hover:text-red-800 px-2 py-1 rounded transition-colors"
        >
          Delete Term
        </button>
      </div>
    </div>
  </div>
</template>
