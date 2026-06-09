<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'

interface Term {
  id: number
  name: string
  is_active: number
  start_date: string
  end_date: string
}

interface Step {
  id: number
  title: string
  description: string | null
  icon: string | null
}

interface CloneResult {
  term: Term
  steps: Step[]
}

const props = defineProps<{
  open: boolean
  terms: Term[]
  api: AdminApi
  defaultSourceTermId: number | null
}>()

const emit = defineEmits<{
  close: []
  cloned: [result: CloneResult]
}>()

const sourceTermId = ref<number | null>(props.defaultSourceTermId || props.terms[0]?.id || null)
const steps = ref<Step[]>([])
const selectedStepIds = ref<Set<number>>(new Set())
const name = ref('')
const startDate = ref('')
const endDate = ref('')
const loadingSteps = ref(false)
const saving = ref(false)
const error = ref('')

watch(
  () => [props.open, props.defaultSourceTermId, props.terms] as const,
  () => {
    if (!props.open) return
    sourceTermId.value = props.defaultSourceTermId || props.terms[0]?.id || null
    name.value = ''
    startDate.value = ''
    endDate.value = ''
    error.value = ''
  },
)

watch(
  () => [props.api, props.open, sourceTermId.value] as const,
  () => {
    if (!props.open || !sourceTermId.value) return
    loadingSteps.value = true
    props.api
      .get<Step[]>(`/steps?term_id=${sourceTermId.value}`)
      .then((data) => {
        steps.value = data
        selectedStepIds.value = new Set(data.map((step) => step.id))
      })
      .catch((err: any) => {
        error.value = err.message || 'Failed to load steps'
      })
      .finally(() => {
        loadingSteps.value = false
      })
  },
)

const selectedCount = computed(() => selectedStepIds.value.size)
const allSelected = computed(
  () => steps.value.length > 0 && selectedCount.value === steps.value.length,
)

const sourceTerm = computed(
  () => props.terms.find((term) => term.id === sourceTermId.value) || null,
)

const toggleStep = (stepId: number) => {
  const next = new Set(selectedStepIds.value)
  if (next.has(stepId)) next.delete(stepId)
  else next.add(stepId)
  selectedStepIds.value = next
}

const toggleSelectAll = () => {
  selectedStepIds.value = allSelected.value
    ? new Set()
    : new Set(steps.value.map((step) => step.id))
}

const handleSubmit = async () => {
  saving.value = true
  error.value = ''
  try {
    const result = await props.api.post<CloneResult>(`/terms/${sourceTermId.value}/clone`, {
      name: name.value,
      start_date: startDate.value || null,
      end_date: endDate.value || null,
      step_ids: [...selectedStepIds.value],
    })
    emit('cloned', result)
    emit('close')
  } catch (err: any) {
    error.value = err.message || 'Failed to clone term'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div
    v-if="open"
    class="fixed inset-0 z-50 flex items-center justify-center bg-csub-blue-dark/40 px-4"
  >
    <div
      class="w-full max-w-2xl bg-white rounded-2xl shadow-xl border border-gray-200 p-6 max-h-[90vh] overflow-y-auto"
    >
      <div class="flex items-start justify-between gap-4 mb-5">
        <div>
          <h3 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
            Clone Term
          </h3>
          <p class="font-body text-sm text-csub-gray mt-1">
            Create a new term by copying selected steps from an existing one.
          </p>
        </div>
        <button
          @click="emit('close')"
          class="text-csub-gray hover:text-csub-blue-dark transition-colors"
        >
          Close
        </button>
      </div>

      <form @submit.prevent="handleSubmit" class="space-y-5">
        <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <div>
            <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
              >Source Term</label
            >
            <select
              :value="sourceTermId || ''"
              @change="(e) => (sourceTermId = parseInt((e.target as HTMLSelectElement).value, 10))"
              class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue bg-white"
            >
              <option v-for="term in terms" :key="term.id" :value="term.id">{{ term.name }}</option>
            </select>
          </div>
          <div>
            <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
              >New Term Name</label
            >
            <input
              type="text"
              required
              v-model="name"
              class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
              placeholder="Fall 2027"
            />
          </div>
          <div class="grid grid-cols-2 gap-2 sm:grid-cols-1">
            <div>
              <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
                >Start Date</label
              >
              <input
                type="date"
                v-model="startDate"
                class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
              />
            </div>
            <div>
              <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1"
                >End Date</label
              >
              <input
                type="date"
                v-model="endDate"
                class="w-full px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
              />
            </div>
          </div>
        </div>

        <div class="bg-gray-50 rounded-xl border border-gray-200 p-4">
          <div class="flex items-center justify-between gap-3 mb-3">
            <div>
              <p class="font-body text-sm font-semibold text-csub-blue-dark">
                {{ sourceTerm?.name || 'Source term' }} steps
              </p>
              <p class="font-body text-xs text-csub-gray">
                Select which steps to copy into the new term.
              </p>
            </div>
            <button
              v-if="steps.length > 0"
              type="button"
              @click="toggleSelectAll"
              class="font-body text-xs text-csub-blue hover:text-csub-blue-dark transition-colors"
            >
              {{ allSelected ? 'Select None' : 'Select All' }}
            </button>
          </div>

          <p v-if="loadingSteps" class="font-body text-sm text-csub-gray">Loading steps...</p>
          <p v-else-if="steps.length === 0" class="font-body text-sm text-csub-gray">
            No steps available in this term.
          </p>
          <div v-else class="space-y-2 max-h-72 overflow-y-auto">
            <label
              v-for="step in steps"
              :key="step.id"
              class="flex items-start gap-3 bg-white rounded-lg border border-gray-200 px-3 py-2 cursor-pointer"
            >
              <input
                type="checkbox"
                :checked="selectedStepIds.has(step.id)"
                @change="toggleStep(step.id)"
                class="mt-1 rounded"
              />
              <div class="min-w-0">
                <p class="font-body text-sm font-semibold text-csub-blue-dark">
                  {{ step.icon || '📋' }} {{ step.title }}
                </p>
                <p class="font-body text-xs text-csub-gray truncate">
                  {{ step.description || 'No description' }}
                </p>
              </div>
            </label>
          </div>
        </div>

        <p v-if="error" class="font-body text-sm text-red-600">{{ error }}</p>

        <div class="flex items-center gap-3">
          <button
            type="submit"
            :disabled="saving || selectedCount === 0"
            class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display text-sm font-bold uppercase tracking-wider px-5 py-2 rounded-lg shadow transition-colors disabled:opacity-50"
          >
            {{
              saving ? 'Cloning...' : `Clone ${selectedCount} Step${selectedCount === 1 ? '' : 's'}`
            }}
          </button>
          <button
            type="button"
            @click="emit('close')"
            class="font-body text-sm text-csub-gray hover:text-csub-blue-dark transition-colors"
          >
            Cancel
          </button>
        </div>
      </form>
    </div>
  </div>
</template>
