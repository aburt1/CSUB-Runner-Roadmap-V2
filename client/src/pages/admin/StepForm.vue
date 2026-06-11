<script setup lang="ts">
import { ref, computed, watch, onUnmounted, nextTick } from 'vue'
import EmojiPicker from 'vue3-emoji-picker'
import type { EmojiExt } from 'vue3-emoji-picker'
import 'vue3-emoji-picker/css'
import TagEditor from './TagEditor.vue'
import RichTextEditor from './RichTextEditor.vue'
import ApiCheckConfig from './ApiCheckConfig.vue'
import { parseMaybeJson } from '../../utils/json'
import type { AdminApi } from '../../composables/useAdminApi'
import type { StepSavePayload } from '../../types/api'

interface StepData {
  id?: number
  title?: string
  icon?: string
  description?: string
  deadline?: string
  deadline_date?: string
  guide_content?: string
  required_tags?: string | string[] | null
  required_tag_mode?: string
  excluded_tags?: string | string[] | null
  sort_order?: number
  is_public?: number
  is_optional?: number
  contact_info?: string | { name?: string; email?: string; phone?: string }
  term_id?: number | null
}

const props = defineProps<{
  step: StepData | null
  selectedTermId?: number | null
  role?: string
  api?: AdminApi
}>()

const emit = defineEmits<{
  (e: 'save', data: StepSavePayload): void
  (e: 'cancel'): void
}>()

const TAG_PRESETS = [
  'freshman',
  'transfer',
  'first-gen',
  'honors',
  'athlete',
  'eop',
  'veteran',
  'out-of-state',
]

const parseJsonArray = (value: unknown): string[] => parseMaybeJson(value, [])

const iconPickerRef = ref<HTMLDivElement | null>(null)
const title = ref(props.step?.title || '')
const icon = ref(props.step?.icon || '📋')
const showEmojiPicker = ref(false)
const description = ref(props.step?.description || '')
const deadline = ref(props.step?.deadline || '')
const deadlineDate = ref(props.step?.deadline_date || '')
const guideContent = ref(props.step?.guide_content || '')
const requiredTags = ref<string[]>(parseJsonArray(props.step?.required_tags))
const requiredTagMode = ref(props.step?.required_tag_mode === 'all' ? 'all' : 'any')
const excludedTags = ref<string[]>(parseJsonArray(props.step?.excluded_tags))
const sortOrder = ref<string | number>(props.step?.sort_order ?? '')
const isPublic = ref(props.step?.is_public === 1)
const isOptional = ref(props.step?.is_optional === 1)
const showAdvancedRules = ref(parseJsonArray(props.step?.excluded_tags).length > 0)

const existingContact = parseMaybeJson<Record<string, string>>(props.step?.contact_info, {})
const contactName = ref(existingContact?.name || '')
const contactEmail = ref(existingContact?.email || '')
const contactPhone = ref(existingContact?.phone || '')
const termId = ref<number | null>(props.step?.term_id ?? props.selectedTermId ?? null)

watch(
  () => [props.step?.term_id, props.selectedTermId],
  () => {
    termId.value = props.step?.term_id ?? props.selectedTermId ?? null
  },
)

const handleClickOutside = (event: MouseEvent) => {
  if (!iconPickerRef.value?.contains(event.target as Node)) {
    showEmojiPicker.value = false
  }
}

watch(showEmojiPicker, (val) => {
  if (val) {
    nextTick(() => document.addEventListener('mousedown', handleClickOutside))
  } else {
    document.removeEventListener('mousedown', handleClickOutside)
  }
})

onUnmounted(() => {
  document.removeEventListener('mousedown', handleClickOutside)
})

const field =
  'w-full px-3 py-2 rounded-lg border border-gray-300 bg-white font-body text-sm focus:outline-none focus:ring-1 focus:ring-csub-blue'
const label = 'block font-body text-xs font-semibold text-csub-blue-dark mb-1'

const visibilitySummary = computed(() => {
  if (requiredTags.value.length === 0 && excludedTags.value.length === 0) {
    return 'This step is visible to all students in the selected term.'
  }

  const parts: string[] = []
  if (requiredTags.value.length > 0) {
    parts.push(
      requiredTagMode.value === 'all'
        ? `Visible only to students who have every selected tag: ${requiredTags.value.join(', ')}.`
        : `Visible to students who have at least one selected tag: ${requiredTags.value.join(', ')}.`,
    )
  } else {
    parts.push('Visible to all students.')
  }

  if (excludedTags.value.length > 0) {
    parts.push(`Hidden for students with: ${excludedTags.value.join(', ')}.`)
  }

  return parts.join(' ')
})

const addTagTo = (target: 'required' | 'excluded', tag: string) => {
  if (target === 'required') {
    if (!requiredTags.value.includes(tag)) requiredTags.value = [...requiredTags.value, tag]
  } else {
    if (!excludedTags.value.includes(tag)) excludedTags.value = [...excludedTags.value, tag]
  }
}

const onEmojiSelect = (emoji: EmojiExt) => {
  icon.value = emoji.i
  showEmojiPicker.value = false
}

const handleSubmit = () => {
  const contactInfo =
    contactName.value || contactEmail.value || contactPhone.value
      ? {
          name: contactName.value || null,
          email: contactEmail.value || null,
          phone: contactPhone.value || null,
        }
      : null

  emit('save', {
    title: title.value,
    icon: icon.value || null,
    description: description.value || null,
    deadline: deadline.value || null,
    deadline_date: deadlineDate.value || null,
    guide_content: guideContent.value || null,
    links: null,
    required_tags: requiredTags.value.length > 0 ? requiredTags.value : null,
    required_tag_mode: requiredTagMode.value as 'any' | 'all',
    excluded_tags: excludedTags.value.length > 0 ? excludedTags.value : null,
    sort_order: sortOrder.value !== '' ? parseInt(String(sortOrder.value), 10) : undefined,
    contact_info: contactInfo,
    term_id: termId.value,
    is_public: isPublic.value ? 1 : 0,
    is_optional: isOptional.value ? 1 : 0,
  })
}
</script>

<template>
  <form
    @submit.prevent="handleSubmit"
    class="space-y-4 bg-white border border-gray-200 rounded-xl p-5"
  >
    <!-- Section: Step Details -->
    <section class="bg-gray-50/70 border border-gray-200 rounded-xl p-4 space-y-4">
      <div>
        <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
          Step Details
        </h3>
        <p class="font-body text-xs text-csub-gray mt-1">
          Start with the content and icon students will see.
        </p>
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-[1fr_auto] gap-4 items-start">
        <div>
          <label :class="label">Title *</label>
          <input type="text" required v-model="title" :class="field" />
        </div>
        <div class="sm:w-48">
          <label :class="label">Icon</label>
          <div class="relative" ref="iconPickerRef">
            <div class="relative">
              <span
                class="absolute left-3 top-1/2 -translate-y-1/2 w-8 h-8 rounded-md bg-gray-50 border border-gray-200 flex items-center justify-center text-xl pointer-events-none"
              >
                {{ icon || '📋' }}
              </span>
              <input
                type="text"
                value=""
                @focus="showEmojiPicker = true"
                :class="`${field} pl-14`"
                :placeholder="icon ? 'Change emoji' : 'Choose emoji'"
                readonly
              />
            </div>
            <div v-if="showEmojiPicker" class="absolute z-20 top-full mt-2">
              <div class="rounded-2xl overflow-hidden shadow-xl border border-gray-200">
                <EmojiPicker :native="true" theme="light" @select="onEmojiSelect" />
              </div>
            </div>
          </div>
        </div>
      </div>
      <div>
        <label :class="label">Short Description</label>
        <input
          type="text"
          v-model="description"
          :class="field"
          placeholder="One sentence that explains what the student needs to do."
        />
      </div>
      <div>
        <label :class="label">Guide Content</label>
        <RichTextEditor :value="guideContent" @change="guideContent = $event" />
      </div>
    </section>

    <!-- Section: Visibility -->
    <section class="bg-gray-50/70 border border-gray-200 rounded-xl p-4 space-y-4">
      <div>
        <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
          Visibility
        </h3>
        <p class="font-body text-xs text-csub-gray mt-1">
          Describe who should see this step. The app still uses tags underneath.
        </p>
      </div>
      <div class="bg-white border border-csub-blue/10 rounded-xl p-3">
        <p class="font-body text-sm text-csub-blue-dark">{{ visibilitySummary }}</p>
      </div>
      <div>
        <label :class="label">Show This Step For</label>
        <p class="font-body text-xs text-csub-gray mb-2">
          Leave empty to show the step to everyone in the term.
        </p>
        <div class="flex flex-wrap gap-1.5 mb-2">
          <button
            v-for="tag in TAG_PRESETS"
            :key="tag"
            type="button"
            @click="addTagTo('required', tag)"
            :class="`text-xs font-body font-semibold rounded-full px-2 py-1 transition-colors ${
              requiredTags.includes(tag)
                ? 'bg-csub-blue text-white ring-2 ring-csub-blue/15'
                : 'bg-csub-blue/10 text-csub-blue-dark hover:bg-csub-blue/15'
            }`"
          >
            {{ requiredTags.includes(tag) ? `${tag} ✓` : tag }}
          </button>
        </div>
        <TagEditor :tags="requiredTags" @change="requiredTags = $event" />
      </div>
      <div v-if="requiredTags.length > 1" class="space-y-3">
        <div>
          <p class="font-body text-xs font-semibold text-csub-blue-dark">
            If a student has multiple audience tags
          </p>
          <p class="font-body text-xs text-csub-gray mt-1">
            Choose whether matching one tag is enough, or whether they need every selected tag.
          </p>
        </div>
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <button
            type="button"
            @click="requiredTagMode = 'any'"
            :class="`text-left rounded-xl border px-4 py-3 transition-colors ${requiredTagMode === 'any' ? 'border-csub-blue bg-csub-blue/5 ring-1 ring-csub-blue' : 'border-gray-300 bg-white hover:border-csub-blue/40'}`"
          >
            <div class="flex items-center justify-between gap-3">
              <p class="font-body text-sm font-semibold text-csub-blue-dark">
                Match any selected tag
              </p>
              <span
                v-if="requiredTagMode === 'any'"
                class="font-body text-xs font-semibold text-csub-blue"
                >Active</span
              >
            </div>
            <p class="font-body text-xs text-csub-gray mt-2">
              A student sees this step if they match at least one of the selected tags.
            </p>
          </button>
          <button
            type="button"
            @click="requiredTagMode = 'all'"
            :class="`text-left rounded-xl border px-4 py-3 transition-colors ${requiredTagMode === 'all' ? 'border-csub-blue bg-csub-blue/5 ring-1 ring-csub-blue' : 'border-gray-300 bg-white hover:border-csub-blue/40'}`"
          >
            <div class="flex items-center justify-between gap-3">
              <p class="font-body text-sm font-semibold text-csub-blue-dark">
                Match every selected tag
              </p>
              <span
                v-if="requiredTagMode === 'all'"
                class="font-body text-xs font-semibold text-csub-blue"
                >Active</span
              >
            </div>
            <p class="font-body text-xs text-csub-gray mt-2">
              A student only sees this step if they match all of the selected tags.
            </p>
          </button>
        </div>
        <div class="bg-white border border-gray-200 rounded-lg px-3 py-2">
          <p class="font-body text-xs text-csub-gray">
            {{
              requiredTagMode === 'all'
                ? 'Right now, a student would need every selected audience tag to see this step.'
                : 'Right now, a student would only need one of the selected audience tags to see this step.'
            }}
          </p>
        </div>
      </div>
      <div v-else class="bg-white border border-dashed border-gray-300 rounded-lg px-3 py-2">
        <p class="font-body text-xs text-csub-gray">
          Add two or more audience tags if you want to choose between matching one tag or all tags.
        </p>
      </div>
      <div>
        <button
          type="button"
          @click="showAdvancedRules = !showAdvancedRules"
          class="font-body text-sm font-semibold text-csub-blue hover:text-csub-blue-dark transition-colors"
        >
          {{ showAdvancedRules ? 'Hide advanced visibility' : 'Show advanced visibility' }}
        </button>
      </div>
      <div v-if="showAdvancedRules">
        <label :class="label">Hide This Step For</label>
        <p class="font-body text-xs text-csub-gray mb-2">
          Students with any of these tags will not see this step.
        </p>
        <div class="flex flex-wrap gap-1.5 mb-2">
          <button
            v-for="tag in TAG_PRESETS"
            :key="tag"
            type="button"
            @click="addTagTo('excluded', tag)"
            :class="`text-xs font-body font-semibold rounded-full px-2 py-1 transition-colors ${
              excludedTags.includes(tag)
                ? 'bg-csub-blue text-white ring-2 ring-csub-blue/15'
                : 'bg-csub-blue/10 text-csub-blue-dark hover:bg-csub-blue/15'
            }`"
          >
            {{ excludedTags.includes(tag) ? `${tag} ✓` : tag }}
          </button>
        </div>
        <TagEditor :tags="excludedTags" @change="excludedTags = $event" />
      </div>
    </section>

    <!-- Section: Settings -->
    <section class="bg-gray-50/70 border border-gray-200 rounded-xl p-4 space-y-4">
      <div>
        <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
          Settings
        </h3>
        <p class="font-body text-xs text-csub-gray mt-1">
          Secondary options for timing and preview behavior.
        </p>
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div>
          <label :class="label">Deadline Label</label>
          <input type="text" v-model="deadline" :class="field" placeholder="e.g. May 1" />
        </div>
        <div>
          <label :class="label">Deadline Date</label>
          <input type="date" v-model="deadlineDate" :class="field" />
        </div>
        <div>
          <label :class="label">Sort Order</label>
          <input type="number" v-model="sortOrder" :class="field" />
        </div>
      </div>
      <label class="flex items-center gap-2 cursor-pointer">
        <input
          type="checkbox"
          v-model="isPublic"
          class="w-4 h-4 rounded border-gray-300 text-csub-blue focus:ring-csub-blue"
        />
        <span class="font-body text-sm text-csub-blue-dark"
          >Show in the public preview before login</span
        >
      </label>
      <label class="flex items-center gap-2 cursor-pointer">
        <input
          type="checkbox"
          v-model="isOptional"
          class="w-4 h-4 rounded border-gray-300 text-csub-blue focus:ring-csub-blue"
        />
        <span class="font-body text-sm text-csub-blue-dark">Optional opportunity</span>
      </label>
    </section>

    <!-- Section: Support -->
    <section class="bg-gray-50/70 border border-gray-200 rounded-xl p-4 space-y-4">
      <div>
        <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
          Support
        </h3>
        <p class="font-body text-xs text-csub-gray mt-1">
          Optional contact details shown to students on this step.
        </p>
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <input type="text" v-model="contactName" :class="field" placeholder="Contact name" />
        <input type="email" v-model="contactEmail" :class="field" placeholder="Email" />
        <input type="tel" v-model="contactPhone" :class="field" placeholder="Phone" />
      </div>
    </section>

    <ApiCheckConfig v-if="role === 'sysadmin' && step?.id && api" :stepId="step.id" :api="api" />

    <div class="flex gap-3 pt-2">
      <button
        type="submit"
        class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider px-6 py-2.5 rounded-lg shadow transition-colors text-sm"
      >
        {{ step ? 'Save Changes' : 'Create Step' }}
      </button>
      <button
        type="button"
        @click="emit('cancel')"
        class="border border-gray-300 text-csub-gray hover:text-csub-blue-dark font-body px-6 py-2.5 rounded-lg transition-colors text-sm"
      >
        Cancel
      </button>
    </div>
  </form>
</template>
