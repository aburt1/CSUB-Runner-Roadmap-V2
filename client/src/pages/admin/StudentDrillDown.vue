<script setup lang="ts">
import { ref, computed, watch, onUnmounted } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'

interface DrillDownStudent {
  id: number
  display_name: string
  email: string
  completion_pct: number
}

interface DrillDownResponse {
  students: DrillDownStudent[]
  title: string
  total: number
}

const props = defineProps<{
  open: boolean
  filterType?: string
  filterValue?: string | number
  termId: number | null
  api: AdminApi
}>()

const emit = defineEmits<{
  (e: 'close'): void
}>()

const students = ref<DrillDownStudent[]>([])
const title = ref('')
const total = ref(0)
const page = ref(1)
const loading = ref(false)
const loadingMore = ref(false)
const panelRef = ref<HTMLDivElement | null>(null)

watch(
  () => [props.open, props.filterType, props.filterValue, props.termId, props.api] as const,
  () => {
    if (!props.open || !props.filterType) return
    students.value = []
    page.value = 1
    loading.value = true
    props.api
      .get<DrillDownResponse>('/analytics/students', {
        term_id: props.termId,
        filter_type: props.filterType,
        filter_value: props.filterValue,
        page: 1,
        per_page: 50,
      })
      .then((data) => {
        students.value = data.students
        title.value = data.title
        total.value = data.total
      })
      .catch(() => {})
      .finally(() => {
        loading.value = false
      })
  },
  { immediate: true },
)

const loadMore = async () => {
  const nextPage = page.value + 1
  loadingMore.value = true
  try {
    const data = await props.api.get<DrillDownResponse>('/analytics/students', {
      term_id: props.termId,
      filter_type: props.filterType,
      filter_value: props.filterValue,
      page: nextPage,
      per_page: 50,
    })
    students.value = [...students.value, ...data.students]
    page.value = nextPage
  } catch {
    // ignore
  } finally {
    loadingMore.value = false
  }
}

const hasMore = computed(() => students.value.length < total.value)

// Close on click outside
const handleClick = (e: globalThis.MouseEvent) => {
  if (panelRef.value && !panelRef.value.contains(e.target as Node)) emit('close')
}

// Close on Escape
const handleKey = (e: globalThis.KeyboardEvent) => {
  if (e.key === 'Escape') emit('close')
}

watch(
  () => props.open,
  (isOpen) => {
    if (isOpen) {
      document.addEventListener('mousedown', handleClick)
      document.addEventListener('keydown', handleKey)
    } else {
      document.removeEventListener('mousedown', handleClick)
      document.removeEventListener('keydown', handleKey)
    }
  },
  { immediate: true },
)

onUnmounted(() => {
  document.removeEventListener('mousedown', handleClick)
  document.removeEventListener('keydown', handleKey)
})
</script>

<template>
  <Teleport to="body">
    <Transition name="drill-down-fade">
      <div v-if="open" class="fixed inset-0 z-40">
        <div class="absolute inset-0 bg-black/30" />
        <Transition name="drill-down-panel" appear>
          <div
            ref="panelRef"
            class="absolute right-0 top-0 bottom-0 w-full max-w-md bg-white shadow-xl z-50 flex flex-col"
          >
            <!-- Header -->
            <div class="flex items-start justify-between p-5 border-b border-gray-200">
              <div class="pr-4">
                <h2
                  class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide"
                >
                  {{ title }}
                </h2>
                <span class="font-body text-xs text-csub-gray mt-1 block"
                  >{{ total }} {{ total === 1 ? 'student' : 'students' }}</span
                >
              </div>
              <button
                @click="emit('close')"
                class="p-1 rounded-lg hover:bg-gray-100 text-gray-400 hover:text-gray-600 transition-colors"
                aria-label="Close"
              >
                <svg
                  class="w-5 h-5"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  :stroke-width="2"
                >
                  <path stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>
            <!-- Student list -->
            <div class="flex-1 overflow-y-auto p-5">
              <div v-if="loading" class="flex items-center justify-center py-8">
                <div
                  class="w-6 h-6 border-2 border-csub-blue border-t-transparent rounded-full animate-spin"
                />
              </div>
              <p
                v-else-if="students.length === 0"
                class="font-body text-sm text-csub-gray text-center py-8"
              >
                No students match this filter
              </p>
              <template v-else>
                <div class="space-y-2">
                  <div
                    v-for="s in students"
                    :key="s.id"
                    class="flex items-center gap-3 p-3 rounded-lg border border-gray-100 hover:bg-gray-50"
                  >
                    <div class="flex-1 min-w-0">
                      <p class="font-body text-sm font-semibold text-csub-blue-dark truncate">
                        {{ s.display_name }}
                      </p>
                      <p class="font-body text-xs text-csub-gray truncate">{{ s.email }}</p>
                    </div>
                    <div class="flex items-center gap-2 flex-shrink-0">
                      <div class="w-16 h-1.5 bg-gray-100 rounded-full overflow-hidden">
                        <div
                          class="h-full bg-csub-blue rounded-full"
                          :style="{ width: `${s.completion_pct}%` }"
                        />
                      </div>
                      <span class="font-body text-xs text-csub-gray w-8 text-right"
                        >{{ s.completion_pct }}%</span
                      >
                    </div>
                  </div>
                </div>
                <button
                  v-if="hasMore"
                  @click="loadMore"
                  :disabled="loadingMore"
                  class="w-full mt-4 py-2.5 font-body text-sm font-semibold text-csub-blue border border-csub-blue/20 rounded-lg hover:bg-csub-blue/5 transition-colors disabled:opacity-50"
                >
                  {{ loadingMore ? 'Loading...' : `Load more (${students.length} of ${total})` }}
                </button>
              </template>
            </div>
          </div>
        </Transition>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.drill-down-fade-enter-active,
.drill-down-fade-leave-active {
  transition: opacity 0.2s ease;
}
.drill-down-fade-enter-from,
.drill-down-fade-leave-to {
  opacity: 0;
}

.drill-down-panel-enter-active,
.drill-down-panel-leave-active {
  transition: transform 0.3s cubic-bezier(0.22, 1, 0.36, 1);
}
.drill-down-panel-enter-from,
.drill-down-panel-leave-to {
  transform: translateX(100%);
}
</style>
