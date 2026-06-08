<script setup lang="ts">
import { ref, onMounted } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'

interface TermItem {
  id: number
  name: string
  is_active: number
  start_date: string
  end_date: string
  student_count?: number
  step_count?: number
}

interface TermForm {
  name: string
  start_date: string
  end_date: string
}

const props = defineProps<{
  api: AdminApi
}>()

const emit = defineEmits<{
  termsChange: [terms: TermItem[]]
}>()

const terms = ref<TermItem[]>([])
const showForm = ref(false)
const editingId = ref<number | null>(null)
const form = ref<TermForm>({ name: '', start_date: '', end_date: '' })
const error = ref('')
const saving = ref(false)

const loadTerms = () => {
  props.api.get<TermItem[]>('/terms').then((data) => {
    terms.value = data
    emit('termsChange', data)
  }).catch(() => {})
}

onMounted(() => { loadTerms() })

const resetForm = () => {
  form.value = { name: '', start_date: '', end_date: '' }
  showForm.value = false
  editingId.value = null
  error.value = ''
}

const handleSubmit = async () => {
  error.value = ''
  saving.value = true
  try {
    if (editingId.value) {
      await props.api.put(`/terms/${editingId.value}`, form.value)
    } else {
      await props.api.post('/terms', form.value)
    }
    resetForm()
    loadTerms()
  } catch (err: any) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}

const startEdit = (term: TermItem) => {
  form.value = { name: term.name, start_date: term.start_date || '', end_date: term.end_date || '' }
  editingId.value = term.id
  showForm.value = true
}

const toggleActive = async (term: TermItem) => {
  try {
    await props.api.put(`/terms/${term.id}`, { is_active: term.is_active ? 0 : 1 })
    loadTerms()
  } catch (err: any) {
    error.value = err.message
  }
}
</script>

<template>
  <div class="space-y-6">
    <div class="flex items-center justify-between">
      <div>
        <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
          Admission Terms
        </h2>
        <p class="font-body text-xs text-csub-gray mt-1">Manage enrollment periods and their date ranges</p>
      </div>
      <button
        v-if="!showForm"
        @click="() => { resetForm(); showForm = true }"
        class="flex items-center gap-2 bg-csub-blue hover:bg-csub-blue-dark text-white font-display text-sm font-bold uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors"
      >
        <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d="M12 4v16m8-8H4" />
        </svg>
        New Term
      </button>
    </div>

    <form v-if="showForm" @submit.prevent="handleSubmit" class="bg-white rounded-xl border border-gray-200 shadow-sm p-5 space-y-4">
      <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
        {{ editingId ? 'Edit Term' : 'Create Term' }}
      </h3>
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <input
          type="text"
          required
          placeholder="Term Name (e.g. Fall 2027)"
          v-model="form.name"
          class="px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
        />
        <div>
          <label class="font-body text-xs text-csub-gray block mb-1">Start Date</label>
          <input
            type="date"
            v-model="form.start_date"
            class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
          />
        </div>
        <div>
          <label class="font-body text-xs text-csub-gray block mb-1">End Date</label>
          <input
            type="date"
            v-model="form.end_date"
            class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
          />
        </div>
      </div>
      <p v-if="error" class="text-red-600 text-sm font-body">{{ error }}</p>
      <div class="flex gap-3">
        <button
          type="submit"
          :disabled="saving"
          class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display text-sm font-bold uppercase tracking-wider px-5 py-2 rounded-lg shadow transition-colors disabled:opacity-50"
        >
          {{ saving ? 'Saving...' : editingId ? 'Update' : 'Create' }}
        </button>
        <button type="button" @click="resetForm" class="font-body text-sm text-csub-gray hover:text-csub-blue-dark transition-colors">
          Cancel
        </button>
      </div>
    </form>

    <div class="space-y-2">
      <div
        v-for="term in terms"
        :key="term.id"
        :class="`flex items-center justify-between bg-white rounded-xl border border-gray-200 shadow-sm px-5 py-4 ${
          !term.is_active ? 'opacity-50' : ''
        }`"
      >
        <div>
          <div class="flex items-center gap-3">
            <p class="font-body text-sm font-semibold text-csub-blue-dark">{{ term.name }}</p>
            <span v-if="term.is_active" class="text-[10px] font-body font-semibold text-emerald-600 bg-emerald-50 rounded-full px-2 py-0.5">Active</span>
            <span v-else class="text-[10px] font-body font-semibold text-gray-500 bg-gray-100 rounded-full px-2 py-0.5">Inactive</span>
          </div>
          <p class="font-body text-xs text-csub-gray mt-0.5">
            {{ term.start_date || '?' }} {{ '—' }} {{ term.end_date || '?' }}
            <span class="ml-3">{{ term.student_count }} students</span>
            <span class="ml-3">{{ term.step_count }} steps</span>
          </p>
        </div>
        <div class="flex items-center gap-2">
          <button @click="startEdit(term)" class="font-body text-xs text-csub-blue hover:text-csub-blue-dark transition-colors">Edit</button>
          <button
            @click="toggleActive(term)"
            :class="`font-body text-xs transition-colors ${
              term.is_active ? 'text-red-500 hover:text-red-700' : 'text-green-600 hover:text-green-800'
            }`"
          >
            {{ term.is_active ? 'Deactivate' : 'Activate' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
