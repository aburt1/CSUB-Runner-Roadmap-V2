<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import draggable from 'vuedraggable'
import StepForm from './StepForm.vue'
import type { AdminApi } from '../../composables/useAdminApi'

interface StepItem {
  id: number
  title: string
  description?: string
  icon?: string
  deadline?: string
  is_active: number
  is_public?: number
  sort_order: number
  required_tags?: string | string[]
}

const props = withDefaults(defineProps<{
  api: AdminApi
  role?: string
  termId: number | null
}>(), {
  role: 'viewer',
})

const canEdit = computed(() => props.role === 'admissions_editor' || props.role === 'sysadmin')
const steps = ref<StepItem[]>([])
const loading = ref(true)
const editingStep = ref<Partial<StepItem> | null>(null)
const showInactive = ref(false)
const selected = ref<Set<number>>(new Set())

const fetchSteps = async () => {
  try {
    const query = props.termId ? `/steps?term_id=${props.termId}` : '/steps'
    const data = await props.api.get<StepItem[]>(query)
    steps.value = data
  } catch {
    // ignore
  } finally {
    loading.value = false
  }
}

onMounted(() => { fetchSteps() })

const handleSave = async (data: any) => {
  try {
    if (editingStep.value?.id) {
      await props.api.put(`/steps/${editingStep.value.id}`, data)
    } else {
      await props.api.post('/steps', data)
    }
    editingStep.value = null
    fetchSteps()
  } catch {
    alert('Failed to save step.')
  }
}

const handleDelete = async (id: number) => {
  if (!confirm('Deactivate this step? Students will no longer see it.')) return
  try {
    await props.api.del(`/steps/${id}`)
    fetchSteps()
  } catch { /* ignore */ }
}

const handleRestore = async (id: number) => {
  try {
    await props.api.put(`/steps/${id}`, { is_active: 1 })
    fetchSteps()
  } catch { /* ignore */ }
}

const handleDuplicate = async (id: number) => {
  try {
    await props.api.post(`/steps/${id}/duplicate`)
    fetchSteps()
  } catch { /* ignore */ }
}

const moveStep = async (index: number, direction: number) => {
  const sorted = [...steps.value].sort((a, b) => a.sort_order - b.sort_order)
  const swapIndex = index + direction
  if (swapIndex < 0 || swapIndex >= sorted.length) return

  const order = sorted.map((s) => ({ id: s.id, sort_order: s.sort_order }))
  const tempOrder = order[index]!.sort_order
  order[index]!.sort_order = order[swapIndex]!.sort_order
  order[swapIndex]!.sort_order = tempOrder

  try {
    await props.api.put('/steps/reorder', { order })
    fetchSteps()
  } catch { /* ignore */ }
}

// Drag-and-drop reorder handler — debounced API call
const saveTimerRef = ref<ReturnType<typeof setTimeout> | null>(null)
const handleDragReorder = (reorderedVisible: StepItem[]) => {
  // Optimistic local update on every frame (smooth visual)
  const updatedSteps = steps.value.map((s) => {
    const newIndex = reorderedVisible.findIndex((v) => v.id === s.id)
    return newIndex !== -1 ? { ...s, sort_order: newIndex + 1 } : s
  })
  steps.value = updatedSteps

  // Debounce the API save — only fires 500ms after dragging stops
  if (saveTimerRef.value) clearTimeout(saveTimerRef.value)
  saveTimerRef.value = setTimeout(() => {
    const order = reorderedVisible.map((s, i) => ({ id: s.id, sort_order: i + 1 }))
    props.api.put('/steps/reorder', { order }).catch(() => fetchSteps())
  }, 500)
}

onUnmounted(() => {
  if (saveTimerRef.value) clearTimeout(saveTimerRef.value)
})

const toggleSelect = (id: number) => {
  const next = new Set(selected.value)
  next.has(id) ? next.delete(id) : next.add(id)
  selected.value = next
}

const sortedSteps = computed(() => [...steps.value].sort((a, b) => a.sort_order - b.sort_order))
const visibleSteps = computed(() => showInactive.value ? sortedSteps.value : sortedSteps.value.filter((s) => s.is_active !== 0))
const activeCount = computed(() => steps.value.filter((s) => s.is_active !== 0).length)

const toggleSelectAll = () => {
  if (selected.value.size === visibleSteps.value.length) {
    selected.value = new Set()
  } else {
    selected.value = new Set(visibleSteps.value.map((s) => s.id))
  }
}

const handleBulkAction = async (isActive: number) => {
  if (selected.value.size === 0) return
  try {
    await props.api.put('/steps/bulk-status', { stepIds: [...selected.value], is_active: isActive })
    selected.value = new Set()
    fetchSteps()
  } catch { /* ignore */ }
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
  <p v-if="loading" class="font-body text-sm text-csub-gray text-center py-8">Loading steps...</p>
  <div v-else>
    <div class="flex items-center justify-between mb-4">
      <div class="flex items-center gap-3">
        <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
          Manage Steps
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
          @click="editingStep = {}"
          class="flex items-center gap-1.5 bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors text-sm"
        >
          <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2.5">
            <path stroke-linecap="round" stroke-linejoin="round" d="M12 4v16m8-8H4" />
          </svg>
          New Step
        </button>
      </div>
    </div>

    <!-- Bulk action bar -->
    <div v-if="canEdit && selected.size > 0" class="flex items-center gap-3 mb-4 bg-csub-blue/5 border border-csub-blue/20 rounded-xl px-4 py-2.5">
      <span class="font-body text-sm text-csub-blue-dark font-semibold">
        {{ selected.size }} selected
      </span>
      <button
        @click="handleBulkAction(1)"
        class="font-body text-xs text-green-700 hover:text-green-900 font-semibold transition-colors"
      >
        Activate
      </button>
      <button
        @click="handleBulkAction(0)"
        class="font-body text-xs text-red-600 hover:text-red-800 font-semibold transition-colors"
      >
        Deactivate
      </button>
      <button
        @click="selected = new Set()"
        class="font-body text-xs text-csub-gray hover:text-csub-blue-dark ml-auto transition-colors"
      >
        Clear
      </button>
    </div>

    <div v-if="canEdit && editingStep && !editingStep.id" class="mb-6">
      <StepForm :step="null" @save="handleSave" @cancel="editingStep = null" />
    </div>

    <!-- Select all -->
    <div v-if="canEdit && visibleSteps.length > 0" class="flex items-center gap-2 mb-2 px-1">
      <input
        type="checkbox"
        :checked="selected.size === visibleSteps.length && visibleSteps.length > 0"
        @change="toggleSelectAll"
        class="rounded"
      />
      <span class="font-body text-xs text-csub-gray">Select all</span>
      <span v-if="canEdit" class="font-body text-[10px] text-csub-gray/60 ml-2">
        Drag the grip handle to reorder
      </span>
    </div>

    <!-- Step list — draggable when canEdit -->
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
              step.is_active === 0
                ? 'border-gray-200 bg-gray-50 opacity-60'
                : 'border-gray-200 bg-white'
            }`"
          >
            <!-- Drag handle -->
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

            <input
              type="checkbox"
              :checked="selected.has(step.id)"
              @change="toggleSelect(step.id)"
              class="rounded flex-shrink-0"
            />

            <!-- Arrow buttons (keyboard accessibility fallback) -->
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

            <!-- Icon -->
            <div class="w-10 h-10 rounded-lg bg-gray-50 flex items-center justify-center text-xl flex-shrink-0">
              {{ step.icon || '📋' }}
            </div>

            <div class="flex-1 min-w-0">
              <p class="font-body text-sm font-semibold text-csub-blue-dark truncate">
                {{ step.title }}
                <span v-if="step.is_public === 1" class="ml-2 text-[10px] bg-emerald-50 text-emerald-700 px-1.5 py-0.5 rounded-full font-body font-medium align-middle">
                  Public
                </span>
              </p>
              <p class="font-body text-xs text-csub-gray truncate">
                {{ step.description || 'No description' }}
                <span v-if="step.deadline" class="text-amber-600 ml-1">{{ '—' }} {{ step.deadline }}</span>
              </p>
              <div v-if="step.required_tags" class="flex flex-wrap gap-1 mt-1">
                <span v-for="tag in parseTags(step.required_tags)" :key="tag" class="text-[10px] bg-csub-blue/10 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">
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
                @click="handleDuplicate(step.id)"
                class="font-body text-xs text-csub-blue hover:text-csub-blue-dark px-2 py-1 rounded hover:bg-csub-blue/5 transition-colors"
              >
                Duplicate
              </button>
              <button
                v-if="step.is_active === 0"
                @click="handleRestore(step.id)"
                class="font-body text-xs text-green-600 hover:text-green-800 px-2 py-1 rounded hover:bg-green-50 transition-colors"
              >
                Restore
              </button>
              <button
                v-else
                @click="handleDelete(step.id)"
                class="font-body text-xs text-red-500 hover:text-red-700 px-2 py-1 rounded hover:bg-red-50 transition-colors"
              >
                Delete
              </button>
            </div>
          </div>

          <div v-if="editingStep?.id === step.id" class="mt-2 mb-4">
            <StepForm :step="step" @save="handleSave" @cancel="editingStep = null" />
          </div>
        </div>
      </template>
    </draggable>
    <div v-else class="space-y-2">
      <div v-for="step in visibleSteps" :key="step.id">
        <div>
          <div
            :class="`flex items-center gap-3 px-4 py-3.5 rounded-xl border shadow-sm transition-all hover:shadow-md ${
              step.is_active === 0
                ? 'border-gray-200 bg-gray-50 opacity-60'
                : 'border-gray-200 bg-white'
            }`"
          >
            <!-- Icon -->
            <div class="w-10 h-10 rounded-lg bg-gray-50 flex items-center justify-center text-xl flex-shrink-0">
              {{ step.icon || '📋' }}
            </div>

            <div class="flex-1 min-w-0">
              <p class="font-body text-sm font-semibold text-csub-blue-dark truncate">
                {{ step.title }}
                <span v-if="step.is_public === 1" class="ml-2 text-[10px] bg-emerald-50 text-emerald-700 px-1.5 py-0.5 rounded-full font-body font-medium align-middle">
                  Public
                </span>
              </p>
              <p class="font-body text-xs text-csub-gray truncate">
                {{ step.description || 'No description' }}
                <span v-if="step.deadline" class="text-amber-600 ml-1">{{ '—' }} {{ step.deadline }}</span>
              </p>
              <div v-if="step.required_tags" class="flex flex-wrap gap-1 mt-1">
                <span v-for="tag in parseTags(step.required_tags)" :key="tag" class="text-[10px] bg-csub-blue/10 text-csub-blue-dark px-1.5 py-0.5 rounded-full font-body">
                  {{ tag }}
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <p v-if="visibleSteps.length === 0" class="font-body text-sm text-csub-gray text-center py-8">No steps yet. Create one above.</p>
  </div>
</template>
