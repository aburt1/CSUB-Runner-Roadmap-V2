<script setup lang="ts">
import { ref } from 'vue'

const props = defineProps<{
  tags: string[]
}>()

const emit = defineEmits<{
  (e: 'change', tags: string[]): void
}>()

const input = ref('')

const addTag = () => {
  const tag = input.value.trim().toLowerCase()
  if (tag && !props.tags.includes(tag)) {
    emit('change', [...props.tags, tag])
  }
  input.value = ''
}

const removeTag = (tag: string) => {
  emit(
    'change',
    props.tags.filter((t) => t !== tag),
  )
}

const handleKeydown = (e: KeyboardEvent) => {
  if (e.key === 'Enter') {
    e.preventDefault()
    addTag()
  }
}
</script>

<template>
  <div>
    <div class="flex gap-2">
      <div
        class="flex-1 min-h-[42px] px-3 py-2 rounded border border-gray-300 bg-white focus-within:ring-1 focus-within:ring-csub-blue"
      >
        <div class="flex flex-wrap gap-1.5 items-center">
          <span
            v-for="tag in tags"
            :key="tag"
            class="inline-flex items-center gap-1 bg-csub-blue text-white text-xs font-body font-semibold px-2 py-1 rounded-full"
          >
            {{ tag }}
            <button
              type="button"
              @click="removeTag(tag)"
              class="hover:text-white/80 ml-0.5"
              :aria-label="`Remove ${tag}`"
            >
              &times;
            </button>
          </span>
          <input
            type="text"
            v-model="input"
            @keydown="handleKeydown"
            :placeholder="tags.length === 0 ? 'Add tag...' : 'Add another tag...'"
            class="flex-1 min-w-[120px] border-0 p-0 font-body text-xs focus:outline-none focus:ring-0"
          />
        </div>
      </div>
      <button
        type="button"
        @click="addTag"
        class="bg-csub-blue text-white font-body text-xs px-3 py-1.5 rounded hover:bg-csub-blue-dark transition-colors"
      >
        Add
      </button>
    </div>
  </div>
</template>
