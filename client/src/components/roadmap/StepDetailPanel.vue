<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, useTemplateRef, watch } from 'vue'
import DOMPurify from 'dompurify'
import { safeUrl } from '../../utils/links'
import type { StepWithStatus, LinkItem, ContactInfo } from '../../types/api'

interface StatusLabelConfig {
  label: string
  class: string
}

const STATUS_LABELS: Record<string, StatusLabelConfig> = {
  completed: { label: 'Completed', class: 'bg-emerald-50 text-emerald-700' },
  in_progress: { label: 'In Progress', class: 'bg-csub-blue/10 text-csub-blue' },
  not_started: { label: 'Not Started', class: 'bg-gray-100 text-gray-500' },
  waived: { label: 'Waived', class: 'bg-slate-100 text-slate-500' },
  locked: { label: 'Locked', class: 'bg-gray-100 text-gray-500' },
}

const props = withDefaults(
  defineProps<{
    step: StepWithStatus
    stepNumber: number
    totalSteps: number
    completedAt?: string | null
    hasPrev: boolean
    hasNext: boolean
    onOptionalStepStatusChange?: ((status: string) => void) | null
    updatingOptionalStep?: boolean
  }>(),
  {
    completedAt: null,
    onOptionalStepStatusChange: null,
    updatingOptionalStep: false,
  },
)

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'navigate', direction: 'prev' | 'next'): void
}>()

function onClose(): void {
  emit('close')
}

function onNavigate(direction: 'prev' | 'next'): void {
  emit('navigate', direction)
}

const panelRef = useTemplateRef<HTMLDivElement>('panelRef')

// Guarded parse: these computeds run during render, so one malformed DB row must
// degrade to "no links", not throw and take down the whole component tree.
const links = computed<LinkItem[]>(() => {
  try {
    return props.step.links
      ? typeof props.step.links === 'string'
        ? JSON.parse(props.step.links)
        : props.step.links
      : []
  } catch {
    return []
  }
})
const isHtmlContent = computed(
  () => !!props.step.guide_content && /<[a-z][\s\S]*>/i.test(props.step.guide_content),
)
const statusConfig = computed(() => STATUS_LABELS[props.step.status] ?? STATUS_LABELS.not_started!)

const sanitizedGuideContent = computed(() =>
  props.step.guide_content ? DOMPurify.sanitize(props.step.guide_content) : '',
)

const contact = computed<ContactInfo | null>(() => {
  let c: ContactInfo | null = null
  try {
    c = props.step.contact_info
      ? typeof props.step.contact_info === 'string'
        ? JSON.parse(props.step.contact_info)
        : props.step.contact_info
      : null
  } catch {
    return null
  }
  if (!c || !c.name) return null
  return c
})

// Drives the CSS entrance transitions (matches framer-motion initial -> animate).
const entered = ref(false)

function handleKeyDown(e: KeyboardEvent): void {
  if (e.key === 'Escape') onClose()
  if (e.key === 'ArrowLeft' && props.hasPrev) onNavigate('prev')
  if (e.key === 'ArrowRight' && props.hasNext) onNavigate('next')

  // Focus trap: keep Tab within the dialog
  if (e.key === 'Tab' && panelRef.value) {
    const focusable = panelRef.value.querySelectorAll<HTMLElement>(
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )
    if (focusable.length === 0) return
    const first = focusable[0] as HTMLElement
    const last = focusable[focusable.length - 1] as HTMLElement
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault()
      last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault()
      first.focus()
    }
  }
}

onMounted(() => {
  document.addEventListener('keydown', handleKeyDown)
  document.body.style.overflow = 'hidden'
  panelRef.value?.focus()
  requestAnimationFrame(() => {
    entered.value = true
  })
})

onUnmounted(() => {
  document.removeEventListener('keydown', handleKeyDown)
  document.body.style.overflow = ''
})

// Focus panel on open / step change
watch(
  () => props.step.id,
  () => {
    panelRef.value?.focus()
  },
)

function formatCompletedAt(value: string): string {
  return new Date(value).toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  })
}
</script>

<template>
  <div
    class="fixed inset-0 z-50 flex items-end sm:items-center justify-center sdp-overlay"
    :class="{ 'is-entered': entered }"
  >
    <!-- Backdrop -->
    <div class="absolute inset-0 bg-black/40 backdrop-blur-[2px]" @click="onClose" />

    <!-- Panel -->
    <div
      ref="panelRef"
      :tabindex="-1"
      role="dialog"
      aria-modal="true"
      :aria-label="`Step ${stepNumber}: ${step.title}`"
      class="relative bg-white w-full sm:max-w-lg sm:rounded-xl rounded-t-2xl shadow-2xl max-h-[90vh] overflow-y-auto focus:outline-none sdp-panel"
      :class="{ 'is-entered': entered }"
    >
      <!-- Status accent bar -->
      <div
        aria-hidden="true"
        :class="`h-1.5 ${
          step.status === 'completed'
            ? 'bg-gradient-to-r from-csub-gold to-amber-300'
            : step.status === 'in_progress'
              ? 'bg-gradient-to-r from-csub-blue to-blue-400'
              : 'bg-gray-200'
        } sm:rounded-t-xl`"
      />

      <!-- Header -->
      <div class="px-5 sm:px-6 pt-5 pb-4">
        <!-- Nav + close -->
        <div class="flex items-center justify-between mb-4">
          <div class="flex items-center gap-1">
            <button
              @click="onNavigate('prev')"
              :disabled="!hasPrev"
              class="p-2.5 rounded-lg text-gray-400 hover:text-csub-blue hover:bg-gray-100 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
              aria-label="Previous step"
            >
              <svg
                class="w-4 h-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                :stroke-width="2.5"
              >
                <path stroke-linecap="round" stroke-linejoin="round" d="M15 19l-7-7 7-7" />
              </svg>
            </button>
            <span class="font-body text-xs text-gray-400 font-medium px-1">
              {{ stepNumber }} of {{ totalSteps }}
            </span>
            <button
              @click="onNavigate('next')"
              :disabled="!hasNext"
              class="p-2.5 rounded-lg text-gray-400 hover:text-csub-blue hover:bg-gray-100 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
              aria-label="Next step"
            >
              <svg
                class="w-4 h-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                :stroke-width="2.5"
              >
                <path stroke-linecap="round" stroke-linejoin="round" d="M9 5l7 7-7 7" />
              </svg>
            </button>
          </div>
          <button
            @click="onClose"
            class="p-2.5 rounded-lg text-gray-400 hover:text-csub-blue-dark hover:bg-gray-100 transition-colors"
            aria-label="Close details"
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

        <!-- Step header -->
        <div class="flex items-start gap-3">
          <div
            class="flex-shrink-0 w-12 h-12 rounded-xl bg-gray-50 flex items-center justify-center"
          >
            <span class="text-2xl" aria-hidden="true">{{ step.icon }}</span>
          </div>
          <div class="flex-1 min-w-0">
            <h2
              class="font-display text-lg sm:text-xl font-bold text-csub-blue-dark uppercase tracking-wide"
            >
              {{ step.title }}
            </h2>
            <div class="flex flex-wrap items-center gap-2 mt-1.5">
              <span
                v-if="step.status !== 'preview'"
                :class="`inline-flex items-center gap-1 text-xs font-body font-semibold rounded-full px-2.5 py-0.5 ${statusConfig.class}`"
              >
                <span
                  v-if="step.status === 'in_progress'"
                  class="w-1.5 h-1.5 bg-csub-blue rounded-full animate-pulse"
                  aria-hidden="true"
                />
                {{ statusConfig.label }}
              </span>
              <span
                v-if="step.deadline"
                class="text-xs font-body font-medium text-amber-700 bg-amber-50 rounded-full px-2.5 py-0.5"
              >
                {{ step.deadline }}
              </span>
              <span
                v-if="step.is_optional === 1"
                class="text-xs font-body font-medium text-csub-blue bg-csub-blue/10 rounded-full px-2.5 py-0.5"
              >
                Optional
              </span>
              <span v-if="completedAt" class="text-xs font-body text-gray-400">
                Completed {{ formatCompletedAt(completedAt) }}
              </span>
            </div>
          </div>
        </div>
      </div>

      <!-- Divider -->
      <div class="border-t border-gray-100 mx-5 sm:mx-6" />

      <!-- Content -->
      <div class="px-5 sm:px-6 py-5 space-y-5">
        <!-- Description -->
        <p class="font-body text-sm text-csub-gray leading-relaxed">
          {{ step.description }}
        </p>

        <!-- Guide content -->
        <div
          v-if="step.guide_content"
          class="bg-blue-50/50 rounded-xl p-4 border border-blue-100/50"
        >
          <h3
            class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-2 flex items-center gap-2"
          >
            <svg
              class="w-4 h-4 text-csub-blue"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                :stroke-width="2"
                d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
              />
            </svg>
            How to Complete This Step
          </h3>
          <div
            v-if="isHtmlContent"
            class="prose prose-sm max-w-none font-body prose-headings:font-display prose-headings:text-csub-blue-dark prose-headings:uppercase prose-headings:tracking-wide prose-a:text-csub-blue prose-a:font-semibold prose-p:text-csub-gray prose-li:text-csub-gray prose-blockquote:text-csub-gray prose-strong:text-csub-blue-dark"
            v-html="sanitizedGuideContent"
          />
          <div v-else class="font-body text-sm text-csub-gray leading-relaxed whitespace-pre-wrap">
            {{ step.guide_content }}
          </div>
        </div>

        <!-- Locked reason -->
        <div
          v-if="step.status === 'locked' && step.lockedReason"
          class="bg-gray-50 rounded-xl p-4 border border-gray-200"
        >
          <p class="font-body text-sm text-gray-500 flex items-start gap-2">
            <svg
              class="w-4 h-4 text-gray-400 flex-shrink-0 mt-0.5"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                :stroke-width="2"
                d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"
              />
            </svg>
            {{ step.lockedReason }}
          </p>
        </div>

        <!-- Action links — only show for old-format steps (non-HTML guide content) -->
        <div v-if="!isHtmlContent && links.length > 0 && step.status !== 'locked'">
          <h3
            class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-3"
          >
            Helpful Links
          </h3>
          <div class="space-y-2">
            <a
              v-for="(link, i) in links"
              :key="i"
              :href="safeUrl(link.url)"
              target="_blank"
              rel="noopener noreferrer"
              class="flex items-center gap-3 px-4 py-3 rounded-xl border border-gray-200 bg-white hover:bg-csub-blue/5 hover:border-csub-blue/20 transition-all font-body text-sm text-csub-blue font-semibold group"
            >
              <svg
                class="w-4 h-4 text-csub-blue/50 group-hover:text-csub-blue transition-colors"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  :stroke-width="2"
                  d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
                />
              </svg>
              {{ link.label }}
            </a>
          </div>
        </div>

        <!-- Waived note -->
        <div
          v-if="step.status === 'waived'"
          class="bg-slate-50 rounded-xl p-4 border border-slate-200"
        >
          <p class="font-body text-sm text-slate-500">
            This step has been waived by your admissions counselor. No action is needed from you.
          </p>
        </div>

        <!-- Step-specific contact -->
        <div v-if="contact && contact.name">
          <h3
            class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wide mb-2"
          >
            Need Help With This Step?
          </h3>
          <div class="flex items-start gap-3 p-3 rounded-xl bg-gray-50 border border-gray-100">
            <svg
              class="w-4 h-4 text-csub-blue flex-shrink-0 mt-0.5"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                :stroke-width="2"
                d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z"
              />
            </svg>
            <div>
              <p class="font-body text-sm font-semibold text-csub-blue-dark">{{ contact.name }}</p>
              <a
                v-if="contact.email"
                :href="`mailto:${contact.email}`"
                class="font-body text-xs text-csub-blue hover:underline block"
              >
                {{ contact.email }}
              </a>
              <a
                v-if="contact.phone"
                :href="`tel:${contact.phone.replace(/[^+\d]/g, '')}`"
                class="font-body text-xs text-csub-blue hover:underline block"
              >
                {{ contact.phone }}
              </a>
            </div>
          </div>
        </div>
      </div>

      <!-- Footer -->
      <div class="px-5 sm:px-6 pb-6 pt-2">
        <button
          v-if="
            onOptionalStepStatusChange &&
            step.is_optional === 1 &&
            step.status !== 'waived' &&
            step.status !== 'locked' &&
            step.status !== 'preview'
          "
          @click="
            onOptionalStepStatusChange(step.status === 'completed' ? 'not_completed' : 'completed')
          "
          :disabled="updatingOptionalStep"
          :class="`w-full font-display font-bold uppercase tracking-wider text-sm px-6 py-3.5 rounded-xl shadow transition-colors mb-3 ${
            step.status === 'completed'
              ? 'bg-white border border-gray-300 text-csub-blue-dark hover:bg-gray-50'
              : 'bg-csub-blue hover:bg-csub-blue-dark text-white'
          } disabled:opacity-50`"
        >
          {{
            updatingOptionalStep
              ? 'Saving...'
              : step.status === 'completed'
                ? 'Mark Optional Step Incomplete'
                : 'Mark Optional Step Complete'
          }}
        </button>

        <!-- Primary action for in-progress -->
        <a
          v-if="step.status === 'in_progress' && !isHtmlContent && links.length > 0"
          :href="safeUrl(links[0]!.url)"
          target="_blank"
          rel="noopener noreferrer"
          class="flex items-center justify-center gap-2 w-full bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider text-sm px-6 py-3.5 rounded-xl shadow transition-colors mb-3"
        >
          {{ links[0]!.label || 'Get Started' }}
          <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              :stroke-width="2"
              d="M14 5l7 7m0 0l-7 7m7-7H3"
            />
          </svg>
        </a>

        <button
          @click="onClose"
          class="w-full font-body text-sm font-semibold text-csub-gray hover:text-csub-blue-dark py-2 transition-colors"
        >
          Close
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* Backdrop/overlay fade (initial opacity 0 -> animate opacity 1) */
.sdp-overlay {
  opacity: 0;
  transition: opacity 0.2s ease;
}
.sdp-overlay.is-entered {
  opacity: 1;
}

/* Panel spring slide-up (initial y 100% opacity 0.5 -> y 0 opacity 1) */
.sdp-panel {
  opacity: 0.5;
  transform: translateY(100%);
  transition:
    opacity 0.4s ease,
    transform 0.45s cubic-bezier(0.22, 1, 0.36, 1);
}
.sdp-panel.is-entered {
  opacity: 1;
  transform: translateY(0);
}
</style>
