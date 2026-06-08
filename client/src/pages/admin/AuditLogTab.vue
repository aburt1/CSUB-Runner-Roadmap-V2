<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import type { AdminApi } from '../../composables/useAdminApi';
import AuditTimeline from './AuditTimeline.vue';

interface AuditLog {
  id: number;
  entity_type: string;
  action: string;
  changed_by: string;
  created_at: string;
  details: string | Record<string, any>;
}

interface StudentResult {
  id: number;
  display_name: string;
  email: string;
}

interface FilterOption {
  value: string;
  label: string;
}

const props = defineProps<{
  api: AdminApi;
}>();

const LIMIT = 30;

const ENTITY_OPTIONS: FilterOption[] = [
  { value: '', label: 'All entities' },
  { value: 'student_progress', label: 'Student progress' },
  { value: 'student_tags', label: 'Student tags' },
  { value: 'student_profile', label: 'Student profiles' },
  { value: 'step', label: 'Steps' },
  { value: 'term', label: 'Terms' },
  { value: 'admin_user', label: 'Admin users' },
];

const ACTION_OPTIONS: FilterOption[] = [
  { value: '', label: 'All actions' },
  { value: 'complete', label: 'Completed' },
  { value: 'waive', label: 'Waived' },
  { value: 'uncomplete', label: 'Marked incomplete' },
  { value: 'tags_update', label: 'Tags updated' },
  { value: 'step_create', label: 'Step created' },
  { value: 'step_update', label: 'Step updated' },
  { value: 'step_delete', label: 'Step deactivated' },
  { value: 'step_restore', label: 'Step restored' },
  { value: 'term_create', label: 'Term created' },
  { value: 'term_update', label: 'Term updated' },
  { value: 'term_delete', label: 'Term deleted' },
  { value: 'admin_create', label: 'Admin created' },
  { value: 'admin_update', label: 'Admin updated' },
];

interface FetchFilters {
  studentId?: string;
  entityType?: string;
  action?: string;
  changedBy?: string;
  q?: string;
}

const logs = ref<AuditLog[]>([]);
const total = ref(0);
const loading = ref(false);
const studentSearch = ref('');
const selectedStudentId = ref('');
const studentResults = ref<StudentResult[]>([]);
const entityFilter = ref('');
const actionFilter = ref('');
const actorFilter = ref('');
const query = ref('');
const offset = ref(0);

const fetchLogs = async (filters: FetchFilters = {}, off = 0, append = false) => {
  loading.value = true;
  try {
    const studentId = filters.studentId ?? selectedStudentId.value;
    const entityType = filters.entityType ?? entityFilter.value;
    const action = filters.action ?? actionFilter.value;
    const changedBy = filters.changedBy ?? actorFilter.value;
    const q = filters.q ?? query.value;

    const params = new URLSearchParams({
      limit: String(LIMIT),
      offset: String(off),
    });
    if (studentId) params.set('studentId', studentId);
    if (entityType) params.set('entityType', entityType);
    if (action) params.set('action', action);
    if (changedBy.trim()) params.set('changedBy', changedBy.trim());
    if (q.trim()) params.set('q', q.trim());

    const data = await props.api.get<{ logs: AuditLog[]; total: number }>(`/audit?${params.toString()}`);
    logs.value = append ? [...logs.value, ...data.logs] : data.logs;
    total.value = data.total;
    offset.value = off + LIMIT;
  } catch {
    // ignore
  } finally {
    loading.value = false;
  }
};

onMounted(() => {
  fetchLogs({}, 0);
});

const handleStudentSearch = async () => {
  if (!studentSearch.value.trim()) {
    selectedStudentId.value = '';
    studentResults.value = [];
    return fetchLogs({ studentId: '' }, 0);
  }

  try {
    const data = await props.api.get<{ students?: StudentResult[] }>(`/students?search=${encodeURIComponent(studentSearch.value)}`);
    studentResults.value = data.students || [];
  } catch {
    studentResults.value = [];
  }
};

const selectStudent = (student: StudentResult) => {
  selectedStudentId.value = String(student.id);
  studentSearch.value = student.display_name;
  studentResults.value = [];
  fetchLogs({ studentId: String(student.id) }, 0);
};

const clearStudent = () => {
  selectedStudentId.value = '';
  studentSearch.value = '';
  studentResults.value = [];
  fetchLogs({ studentId: '' }, 0);
};

const handleApplyFilters = () => {
  fetchLogs({}, 0);
};

const handleLoadMore = () => {
  fetchLogs({}, offset.value, true);
};

const stats = computed(() => ({
  shown: logs.value.length,
  total: total.value,
  filtered: Boolean(
    selectedStudentId.value ||
    entityFilter.value ||
    actionFilter.value ||
    actorFilter.value.trim() ||
    query.value.trim(),
  ),
}));

const onEntityChange = (e: Event) => {
  const value = (e.target as HTMLSelectElement).value;
  entityFilter.value = value;
  fetchLogs({ entityType: value }, 0);
};

const onActionChange = (e: Event) => {
  const value = (e.target as HTMLSelectElement).value;
  actionFilter.value = value;
  fetchLogs({ action: value }, 0);
};

const resetAll = () => {
  selectedStudentId.value = '';
  studentSearch.value = '';
  studentResults.value = [];
  entityFilter.value = '';
  actionFilter.value = '';
  actorFilter.value = '';
  query.value = '';
  fetchLogs({ studentId: '', entityType: '', action: '', changedBy: '', q: '' }, 0);
};
</script>

<template>
  <div>
    <div class="flex items-center justify-between gap-4 mb-4">
      <div>
        <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
          Audit Log
        </h2>
        <p class="font-body text-sm text-csub-gray mt-1">
          Track who changed what, when it happened, and what was affected.
        </p>
      </div>
      <div class="flex items-center gap-2">
        <span class="font-body text-xs text-csub-gray bg-gray-100 rounded-full px-2.5 py-1">
          {{ stats.shown }} shown
        </span>
        <span class="font-body text-xs text-csub-gray bg-gray-100 rounded-full px-2.5 py-1">
          {{ stats.total }} total
        </span>
      </div>
    </div>

    <div class="bg-white border border-gray-200 rounded-xl shadow-sm p-4 mb-5 space-y-4">
      <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div class="relative">
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1">Student</label>
          <div class="flex gap-2">
            <input
              type="text"
              v-model="studentSearch"
              @keydown="(e) => e.key === 'Enter' && handleStudentSearch()"
              placeholder="Search by student name or email..."
              class="flex-1 px-4 py-2.5 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
            />
            <button
              @click="handleStudentSearch"
              class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-4 py-2.5 rounded-lg shadow transition-colors text-sm"
            >
              Search
            </button>
          </div>

          <div
            v-if="studentResults.length > 0"
            class="absolute z-20 top-full left-0 right-0 mt-1 bg-white border border-gray-200 rounded-lg shadow-lg max-h-48 overflow-y-auto"
          >
            <button
              v-for="student in studentResults"
              :key="student.id"
              @click="selectStudent(student)"
              class="w-full text-left px-4 py-2 hover:bg-csub-blue/5 transition-colors"
            >
              <p class="font-body text-sm font-semibold text-csub-blue-dark">{{ student.display_name }}</p>
              <p class="font-body text-xs text-csub-gray">{{ student.email }}</p>
            </button>
          </div>
        </div>

        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1">Search audit content</label>
          <input
            type="text"
            v-model="query"
            @keydown="(e) => e.key === 'Enter' && handleApplyFilters()"
            placeholder="Search notes, titles, actors, and details..."
            class="w-full px-4 py-2.5 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
          />
        </div>
      </div>

      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1">Entity</label>
          <select
            :value="entityFilter"
            @change="onEntityChange"
            class="w-full px-4 py-2.5 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent bg-white"
          >
            <option v-for="option in ENTITY_OPTIONS" :key="option.value" :value="option.value">{{ option.label }}</option>
          </select>
        </div>

        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1">Action</label>
          <select
            :value="actionFilter"
            @change="onActionChange"
            class="w-full px-4 py-2.5 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent bg-white"
          >
            <option v-for="option in ACTION_OPTIONS" :key="option.value" :value="option.value">{{ option.label }}</option>
          </select>
        </div>

        <div>
          <label class="block font-body text-xs font-semibold text-csub-blue-dark mb-1">Changed by</label>
          <input
            type="text"
            v-model="actorFilter"
            @keydown="(e) => e.key === 'Enter' && handleApplyFilters()"
            placeholder="Admin name or email..."
            class="w-full px-4 py-2.5 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
          />
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <button
          v-if="query.trim() || actorFilter.trim()"
          @click="handleApplyFilters"
          class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-5 py-2.5 rounded-lg shadow transition-colors text-sm"
        >
          Apply
        </button>
        <button
          v-if="selectedStudentId"
          @click="clearStudent"
          class="font-body text-sm text-red-600 hover:text-red-800 transition-colors"
        >
          Clear student
        </button>
        <button
          v-if="stats.filtered"
          @click="resetAll"
          class="font-body text-sm text-csub-gray hover:text-csub-blue-dark transition-colors"
        >
          Reset all
        </button>
      </div>
    </div>

    <div v-if="logs.length === 0 && !loading" class="text-center py-10">
      <p class="font-body text-sm text-csub-gray">
        No audit entries match the current filters.
      </p>
    </div>
    <AuditTimeline v-else :logs="logs" />

    <div v-if="logs.length < total" class="text-center mt-4">
      <button
        @click="handleLoadMore"
        :disabled="loading"
        class="font-body text-sm text-csub-blue hover:text-csub-blue-dark font-semibold transition-colors disabled:opacity-50"
      >
        {{ loading ? 'Loading...' : `Load more (${logs.length} of ${total})` }}
      </button>
    </div>
  </div>
</template>
