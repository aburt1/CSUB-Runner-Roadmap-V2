<script setup lang="ts">
interface Props {
  selectedTermName: string;
  canEdit: boolean;
}

defineProps<Props>();

const emit = defineEmits<{
  (e: 'new-term'): void;
  (e: 'clone-term'): void;
}>();
</script>

<template>
  <div class="bg-white rounded-xl border border-gray-200 shadow-sm p-4">
    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <p class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide">
          Terms and Steps
        </p>
        <p class="font-body text-sm text-csub-gray mt-1">
          {{ selectedTermName
            ? `Showing configuration for ${selectedTermName}. Change the term in the header to switch context.`
            : 'Select a term in the header to manage its steps.' }}
        </p>
      </div>

      <div v-if="canEdit" class="flex items-center gap-2">
        <button
          @click="emit('new-term')"
          class="flex items-center gap-1.5 bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors text-sm"
        >
          <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2.5">
            <path stroke-linecap="round" stroke-linejoin="round" d="M12 4v16m8-8H4" />
          </svg>
          New Term
        </button>
        <button
          @click="emit('clone-term')"
          :disabled="!selectedTermName"
          class="border border-csub-blue/20 text-csub-blue hover:bg-csub-blue/5 font-display font-bold uppercase tracking-wider px-4 py-2 rounded-lg transition-colors text-sm disabled:opacity-40"
        >
          Clone Term
        </button>
      </div>
    </div>
  </div>
</template>
