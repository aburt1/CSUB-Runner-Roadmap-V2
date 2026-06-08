<script setup lang="ts">
import { ref, computed, watch, onUnmounted } from 'vue'
import draggable from 'vuedraggable'
import StepForm from './StepForm.vue'
import TermBar from './TermBar.vue'
import TermHeader from './TermHeader.vue'
import CloneTermModal from './CloneTermModal.vue'
import type { AdminApi } from '../../composables/useAdminApi'

interface TermItem {
  id: number
  name: string
  is_active: number
  start_date: string
  end_date: string
  step_count?: number
  student_count?: number
}

interface StepItem {
  id: number
  title: string
  description?: string
  icon?: string
  deadline?: string
  is_active: number
  is_public?: number
  is_optional?: number
  sort_order: number
  required_tags?: string | string[]
  required_tag_mode?: string
  excluded_tags?: string | string[]
  term_id?: number
}

const props = withDefaults(defineProps<{
  api: AdminApi
  role?: string
  terms: TermItem[]
  selectedTermId: number | null
}>(), {
  role: 'viewer',
})

const emit = defineEmits<{
  termsChange: [terms: TermItem[], selectedTermId?: number | null]
  selectTerm: [termId: number | null]
}>()

const canEdit = computed(() => props.role === 'admissions_editor' || props.role === 'sysadmin')
const steps = ref<StepItem[]>([])
const loading = ref(true)
const editingStep = ref<Partial<StepItem> | null>(null)
const showInactive = ref(false)
const selected = ref<Set<number>>(new Set())
const showCloneModal = ref(false)
const saveTimerRef = ref<ReturnType<typeof setTimeout> | null>(null)

const selectedTerm = computed(
  () => props.terms.find((term) => term.id === props.selectedTermId) || null
)

const refreshTerms = async (nextSelectedTermId: number | null = props.selectedTermId) => {
  const data = await props.api.get<TermItem[]>('/terms')
  emit('termsChange', data, nextSelectedTermId)
  return data
}

const fetchSteps = async () => {
  if (!props.selectedTermId) {
    steps.value = []
    loading.value = false
    return
  }

  loading.value = true
  try {
    const data = await props.api.get<StepItem[]>(`/steps?term_id=${props.selectedTermId}`)
    steps.value = data
  } catch {
    steps.value = []
  } finally {
    loading.value = false
  }
}

watch(() => props.selectedTermId, () => { fetchSteps() }, { immediate: true })

const handleSaveStep = async (data: any) => {
  try {
    if (editingStep.value?.id) {
      await props.api.put(`/steps/${editingStep.value.id}`, data)
    } else {
      await props.api.post('/steps', data)
    }
    editingStep.value = null
    await fetchSteps()
    await refreshTerms()
  } catch (err: any) {
    alert(err.message || 'Failed to save step.')
  }
}

const handleDeleteStep = async (id: number) => {
  if (!confirm('Deactivate this step? Students will no longer see it.')) return
  try {
    await props.api.del(`/steps/${id}`)
    await fetchSteps()
    await refreshTerms()
  } catch {
    // ignore
  }
}

const handleRestoreStep = async (id: number) => {
  try {
    await props.api.put(`/steps/${id}`, { is_active: 1 })
    await fetchSteps()
    await refreshTerms()
  } catch {
    // ignore
  }
}

const handleDuplicateStep = async (id: number) => {
  try {
    await props.api.post(`/steps/${id}/duplicate`)
    await fetchSteps()
    await refreshTerms()
  } catch {
    // ignore
  }
}

const moveStep = async (index: number, direction: number) => {
  const sorted = [...steps.value].sort((a, b) => a.sort_order - b.sort_order)
  const swapIndex = index + direction
  if (swapIndex < 0 || swapIndex >= sorted.length) return

  const order = sorted.map((step) => ({ id: step.id, sort_order: step.sort_order }))
  const tempOrder = order[index]!.sort_order
  order[index]!.sort_order = order[swapIndex]!.sort_order
  order[swapIndex]!.sort_order = tempOrder

  try {
    await props.api.put('/steps/reorder', { order })
    await fetchSteps()
  } catch {
    // ignore
  }
}

const handleDragReorder = (reorderedVisible: StepItem[]) => {
  const updatedSteps = steps.value.map((step) => {
    const newIndex = reorderedVisible.findIndex((visible) => visible.id === step.id)
    return newIndex !== -1 ? { ...step, sort_order: newIndex + 1 } : step
  })
  steps.value = updatedSteps

  if (saveTimerRef.value) clearTimeout(saveTimerRef.value)
  saveTimerRef.value = setTimeout(() => {
    const order = reorderedVisible.map((step, index) => ({ id: step.id, sort_order: index + 1 }))
    props.api.put('/steps/reorder', { order }).catch(() => fetchSteps())
  }, 500)
}

onUnmounted(() => {
  if (saveTimerRef.value) clearTimeout(saveTimerRef.value)
})

const toggleSelect = (id: number) => {
  const next = new Set(selected.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  selected.value = next
}

const sortedSteps = computed(() => [...steps.value].sort((a, b) => a.sort_order - b.sort_order))
const visibleSteps = computed(() => showInactive.value ? sortedSteps.value : sortedSteps.value.filter((step) => step.is_active !== 0))
const activeCount = computed(() => steps.value.filter((step) => step.is_active !== 0).length)

const toggleSelectAll = () => {
  if (selected.value.size === visibleSteps.value.length) selected.value = new Set()
  else selected.value = new Set(visibleSteps.value.map((step) => step.id))
}

const handleBulkAction = async (isActive: number) => {
  if (selected.value.size === 0) return
  try {
    await props.api.put('/steps/bulk-status', { stepIds: [...selected.value], is_active: isActive })
    selected.value = new Set()
    await fetchSteps()
    await refreshTerms()
  } catch {
    // ignore
  }
}

const handleCreateTerm = async () => {
  const name = window.prompt('New term name')
  if (!name) return

  try {
    const result = await props.api.post<{ id: number }>('/terms', { name })
    const data = await refreshTerms(result.id)
    const createdTerm = data.find((term) => term.id === result.id)
    if (createdTerm) emit('selectTerm', createdTerm.id)
  } catch (err: any) {
    alert(err.message || 'Failed to create term.')
  }
}

const handleSaveTerm = async (termId: number, data: any) => {
  await props.api.put(`/terms/${termId}`, data)
  const refreshed = await refreshTerms(termId)
  const term = refreshed.find((item) => item.id === termId)
  if (term) emit('selectTerm', term.id)
}

const handleDeleteTerm = async (term: TermItem) => {
  if ((term.student_count ?? 0) > 0) {
    alert('This term still has students assigned and cannot be deleted.')
    return
  }
  if (!confirm(`Delete ${term.name}? All steps in this term will be removed.`)) return

  try {
    await props.api.del(`/terms/${term.id}`)
    const refreshed = await refreshTerms()
    const nextTerm = refreshed.find((item) => item.is_active) || refreshed[0] || null
    emit('selectTerm', nextTerm?.id || null)
  } catch (err: any) {
    alert(err.message || 'Failed to delete term.')
  }
}

const handleCloned = async (result: any) => {
  const refreshed = await refreshTerms(result.term.id)
  const term = refreshed.find((item) => item.id === result.term.id)
  emit('selectTerm', term?.id || result.term.id)
  steps.value = result.steps || []
}

const parseTags = (value: string | string[]): string[] =>
  typeof value === 'string' ? JSON.parse(value) : value

// vuedraggable v-model proxy for the visible list
const draggableSteps = computed<StepItem[]>({
  get: () => visibleSteps.value,
  set: (val) => handleDragReorder(val),
})
</script>

<template>
  <div class="space-y-5">
    <TermBar
      :selected-term-name="selectedTerm?.name || ''"
      :can-edit="canEdit"
      @new-term="handleCreateTerm"
      @clone-term="showCloneModal = true"
    />

    <TermHeader
      :term="selectedTerm"
      :can-edit="canEdit"
      @save="handleSaveTerm"
      @delete="handleDeleteTerm"
    />

    <div class="bg-white rounded-xl border border-gray-200 shadow-sm p-5">
      <div class="flex items-center justify-between mb-4">
        <div class="flex items-center gap-3">
          <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
            {{ selectedTerm ? `${selectedTerm.name} Steps` : 'Manage Steps' }}
          </h2>
          <span class="font-body text-xs text-csub-gray bg-gray-100 rounded-full px-2.5 py-0.5">
            {{ activeCount }} active
          </span>
        </div>
        <div class="flex items-center gap-3">
          <label class="flex items-center gap-2 font-body text-xs text-csub-gray cursor-pointer">
            <input
              type="checkbox"
              v-model="showInactive"
              class="rounded"
            />
            Show inactive
          </label>
          <button
            v-if="canEdit"
            @click="editingStep = { term_id: selectedTermId ?? undefined }"
            :disabled="!selectedTermId"
            class="flex items-center gap-1.5 bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors text-sm disabled:opacity-40"
          >
            <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2.5">
              <path stroke-linecap="round" stroke-linejoin="round" d="M12 4v16m8-8H4" />
            </svg>
            Add Step
          </button>
        </div>
      </div>

      <div v-if="canEdit && selected.size > 0" class="flex items-center gap-3 mb-4 bg-csub-blue/5 border border-csub-blue/20 rounded-xl px-4 py-2.5">
        <span class="font-body text-sm text-csub-blue-dark font-semibold">{{ selected.size }} selected</span>
        <button @click="handleBulkAction(1)" class="font-body text-xs text-green-700 hover:text-green-900 font-semibold transition-colors">
          Activate
        </button>
        <button @click="handleBulkAction(0)" class="font-body text-xs text-red-600 hover:text-red-800 font-semibold transition-colors">
          Deactivate
        </button>
        <button @click="selected = new Set()" class="font-body text-xs text-csub-gray hover:text-csub-blue-dark ml-auto transition-colors">
          Clear
        </button>
      </div>

      <div v-if="canEdit && editingStep && !editingStep.id" class="mb-6">
        <StepForm
          :step="null"
          :selected-term-id="selectedTermId"
          :role="role"
          :api="api"
          @save="handleSaveStep"
          @cancel="editingStep = null"
        />
      </div>

      <p v-if="loading" class="font-body text-sm text-csub-gray text-center py-8">Loading steps...</p>
      <template v-else>
        <div v-if="canEdit && visibleSteps.length > 0" class="flex items-center gap-2 mb-2 px-1">
          <input
            type="checkbox"
            :checked="selected.size === visibleSteps.length && visibleSteps.length > 0"
            @change="toggleSelectAll"
            class="rounded"
          />
          <span class="font-body text-xs text-csub-gray">Select all</span>
          <span class="font-body text-[10px] text-csub-gray/60 ml-2">Drag the grip handle to reorder</span>
        </div>

        <template v-if="visibleSteps.length > 0">
          <draggable
            v-if="canEdit"
            v-model="draggableSteps"
            item-key="id"
            handle=".drag-handle"
            :animation="200"
            ghost-class="opacity-60"
            class="space-y-2"
          >
            <template #item="{ element: step, index }">
              <div>
                <div
                  :class="`flex items-center gap-3 px-4 py-3.5 rounded-xl border shadow-sm transition-all hover:shadow-md ${
                    step.is_active === 0 ? 'border-gray-200 bg-gray-50 opacity-60' : 'border-gray-200 bg-white'
                  }`"
                >
                  <div
                    class="drag-handle cursor-grab active:cursor-grabbing touch-none text-gray-300 hover:text-csub-blue transition-colors flex-shrink-0"
                    title="Drag to reorder"
                  >
                    <svg class="w-4 h-4" viewBox="0 0 16 16" fill="currentColor">
                      <circle cx="5" cy="3" r="1.5" />
                      <circle cx="11" cy="3" r="1.5" />
                      <circle cx="5" cy="8" r="1.5" />
                      <circle cx="11" cy="8" r="1.5" />
                      <circle cx="5" cy="13" r="1.5" />
                      <circle cx="11" cy="13" r="1.5" />
                    </svg>
                  </div>

                  <input type="checkbox" :checked="selected.has(step.id)" @change="toggleSelect(step.id)" class="rounded flex-shrink-0" />

                  <div class="flex flex-col gap-0.5">
                    <button
                      @click="moveStep(index, -1)"
                      :disabled="index === 0"
                      class="text-xs text-csub-gray hover:text-csub-blue disabled:opacity-30 transition-colors"
                      title="Move up"
                    >
                      {{ '▲' }}
                    </button>
                    <button
                      @click="moveStep(index, 1)"
                      :disabled="index === visibleSteps.length - 1"
                      class="text-xs text-csub-gray hover:text-csub-blue disabled:opacity-30 transition-colors"
                      title="Move down"
                    >
                      {{ '▼' }}
                    </button>
                  </div>

                  <div class="w-10 h-10 rounded-lg bg-gray-50 flex items-center justify-center text-xl flex-shrink-0">
                    {{ step.icon || '📋' }}
                  </div>

                  <div class="flex-1 min-w-0">
                    <p class="font-body text-sm font-semibold text-csub-blue-dark truncate">
                      {{ step.title }}
                      <span v-if="step.is_public === 1" class="ml-2 text-[10px] bg-emerald-50 text-emerald-700 px-1.5 py-0.5 rounded-full font-body font-medium align-middle">
                        Public
                      </span>
                      <span v-if="step.is_optional === 1" class="ml-2 text-[10px] bg-csub-blue/10 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body font-medium align-middle">
                        Optional
                      </span>
                    </p>
                    <p class="font-body text-xs text-csub-gray truncate">
                      {{ step.description || 'No description' }}
                      <span v-if="step.deadline" class="text-amber-600 ml-1">- {{ step.deadline }}</span>
                    </p>
                    <div v-if="step.required_tags" class="flex flex-wrap gap-1 mt-1">
                      <span class="text-[10px] bg-csub-blue/5 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">
                        match {{ step.required_tag_mode === 'all' ? 'all' : 'any' }}
                      </span>
                      <span v-for="tag in parseTags(step.required_tags)" :key="tag" class="text-[10px] bg-csub-blue/10 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">
                        {{ tag }}
                      </span>
                    </div>
                    <div v-if="step.excluded_tags" class="flex flex-wrap gap-1 mt-1">
                      <span class="text-[10px] bg-red-50 text-red-700 px-1.5 py-0.5 rounded-full font-body">
                        hide if
                      </span>
                      <span v-for="tag in parseTags(step.excluded_tags)" :key="tag" class="text-[10px] bg-red-50 text-red-700 px-1.5 py-0.5 rounded-full font-body">
                        {{ tag }}
                      </span>
                    </div>
                  </div>

                  <div class="flex items-center gap-1 flex-shrink-0">
                    <button
                      @click="editingStep = step"
                      class="font-body text-xs text-csub-blue hover:text-csub-blue-dark px-2 py-1 rounded hover:bg-csub-blue/5 transition-colors"
                    >
                      Edit
                    </button>
                    <button
                      @click="handleDuplicateStep(step.id)"
                      class="font-body text-xs text-csub-blue hover:text-csub-blue-dark px-2 py-1 rounded hover:bg-csub-blue/5 transition-colors"
                    >
                      Duplicate
                    </button>
                    <button
                      v-if="step.is_active === 0"
                      @click="handleRestoreStep(step.id)"
                      class="font-body text-xs text-green-600 hover:text-green-800 px-2 py-1 rounded hover:bg-green-50 transition-colors"
                    >
                      Restore
                    </button>
                    <button
                      v-else
                      @click="handleDeleteStep(step.id)"
                      class="font-body text-xs text-red-500 hover:text-red-700 px-2 py-1 rounded hover:bg-red-50 transition-colors"
                    >
                      Delete
                    </button>
                  </div>
                </div>

                <div v-if="editingStep?.id === step.id" class="mt-2 mb-4">
                  <StepForm
                    :step="step"
                    :selected-term-id="selectedTermId"
                    :role="role"
                    :api="api"
                    @save="handleSaveStep"
                    @cancel="editingStep = null"
                  />
                </div>
              </div>
            </template>
          </draggable>
          <div v-else class="space-y-2">
            <div v-for="step in visibleSteps" :key="step.id">
              <div
                :class="`flex items-center gap-3 px-4 py-3.5 rounded-xl border shadow-sm transition-all hover:shadow-md ${
                  step.is_active === 0 ? 'border-gray-200 bg-gray-50 opacity-60' : 'border-gray-200 bg-white'
                }`"
              >
                <div class="w-10 h-10 rounded-lg bg-gray-50 flex items-center justify-center text-xl flex-shrink-0">
                  {{ step.icon || '📋' }}
                </div>

                <div class="flex-1 min-w-0">
                  <p class="font-body text-sm font-semibold text-csub-blue-dark truncate">
                    {{ step.title }}
                    <span v-if="step.is_public === 1" class="ml-2 text-[10px] bg-emerald-50 text-emerald-700 px-1.5 py-0.5 rounded-full font-body font-medium align-middle">
                      Public
                    </span>
                    <span v-if="step.is_optional === 1" class="ml-2 text-[10px] bg-csub-blue/10 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body font-medium align-middle">
                      Optional
                    </span>
                  </p>
                  <p class="font-body text-xs text-csub-gray truncate">
                    {{ step.description || 'No description' }}
                    <span v-if="step.deadline" class="text-amber-600 ml-1">- {{ step.deadline }}</span>
                  </p>
                  <div v-if="step.required_tags" class="flex flex-wrap gap-1 mt-1">
                    <span class="text-[10px] bg-csub-blue/5 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">
                      match {{ step.required_tag_mode === 'all' ? 'all' : 'any' }}
                    </span>
                    <span v-for="tag in parseTags(step.required_tags)" :key="tag" class="text-[10px] bg-csub-blue/10 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">
                      {{ tag }}
                    </span>
                  </div>
                  <div v-if="step.excluded_tags" class="flex flex-wrap gap-1 mt-1">
                    <span class="text-[10px] bg-red-50 text-red-700 px-1.5 py-0.5 rounded-full font-body">
                      hide if
                    </span>
                    <span v-for="tag in parseTags(step.excluded_tags)" :key="tag" class="text-[10px] bg-red-50 text-red-700 px-1.5 py-0.5 rounded-full font-body">
                      {{ tag }}
                    </span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </template>
        <div v-else class="text-center py-10">
          <p class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
            No steps yet
          </p>
          <p class="font-body text-sm text-csub-gray mt-1">
            Add one or clone from another term.
          </p>
        </div>
      </template>
    </div>

    <CloneTermModal
      :open="showCloneModal"
      :terms="terms"
      :api="api"
      :default-source-term-id="selectedTermId"
      @close="showCloneModal = false"
      @cloned="handleCloned"
    />
  </div>
</template>
