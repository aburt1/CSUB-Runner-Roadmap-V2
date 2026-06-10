<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { safeUrl } from '../../utils/links'
import DeadlineCountdown from './DeadlineCountdown.vue'
import type { StepWithStatus, LinkItem } from '../../types/api'

interface StatusConfigEntry {
  nodeClass: string
  icon: 'check' | 'dash' | 'lock' | null
  badgeClass: string
  badgeLabel: string
  cardClass: string
}

const STATUS_CONFIG: Record<string, StatusConfigEntry> = {
  completed: {
    nodeClass: 'bg-csub-gold border-csub-gold text-csub-blue-dark',
    icon: 'check',
    badgeClass: 'bg-emerald-50 text-emerald-700',
    badgeLabel: 'Completed',
    cardClass: 'border-csub-gold/40 bg-white',
  },
  in_progress: {
    nodeClass: 'bg-csub-blue border-csub-blue text-white ring-4 ring-csub-blue/20',
    icon: null, // shows number
    badgeClass: 'bg-csub-blue/10 text-csub-blue',
    badgeLabel: 'In Progress',
    cardClass: 'border-csub-blue/30 bg-white shadow-md',
  },
  not_started: {
    nodeClass: 'bg-white border-gray-300 text-gray-400',
    icon: null,
    badgeClass: 'bg-gray-100 text-gray-500',
    badgeLabel: 'Not Started',
    cardClass: 'border-gray-200 bg-white',
  },
  waived: {
    nodeClass: 'bg-gray-100 border-gray-300 text-gray-400',
    icon: 'dash',
    badgeClass: 'bg-slate-100 text-slate-500',
    badgeLabel: 'Waived',
    cardClass: 'border-gray-200 bg-gray-50',
  },
  preview: {
    nodeClass: 'bg-white border-csub-blue text-csub-blue',
    icon: null,
    badgeClass: '',
    badgeLabel: '',
    cardClass: 'border-gray-200 bg-white',
  },
  locked: {
    nodeClass: 'bg-gray-100 border-gray-300 text-gray-400',
    icon: 'lock',
    badgeClass: 'bg-gray-100 text-gray-500',
    badgeLabel: 'Sign in to unlock',
    cardClass: 'border-gray-200 bg-gray-50/80',
  },
}

const props = withDefaults(
  defineProps<{
    step: StepWithStatus
    index: number
    completedAt?: string | null
    isLast: boolean
    compact?: boolean
  }>(),
  {
    compact: false,
  },
)

const emit = defineEmits<{
  (e: 'select'): void
}>()

const config = computed<StatusConfigEntry>(
  () => STATUS_CONFIG[props.step.status] ?? STATUS_CONFIG.not_started!,
)
const links = computed<LinkItem[]>(() =>
  props.step.links
    ? typeof props.step.links === 'string'
      ? JSON.parse(props.step.links)
      : props.step.links
    : [],
)
const primaryAction = computed<LinkItem | null>(() =>
  props.step.status === 'in_progress' && links.value.length > 0 ? links.value[0]! : null,
)
const isActive = computed(() => props.step.status === 'in_progress')

// Left accent border — visual scan aid for step status
const leftBorderClass = computed(() =>
  props.step.status === 'completed'
    ? 'border-l-4 border-l-csub-gold'
    : props.step.status === 'in_progress'
      ? 'border-l-4 border-l-csub-blue'
      : props.step.status === 'preview'
        ? 'border-l-4 border-l-csub-blue/40'
        : '',
)

const ariaLabel = computed(
  () =>
    `Step ${props.index + 1}, ${props.step.title}, ${config.value.badgeLabel || props.step.status}${
      props.step.deadline ? `, due ${props.step.deadline}` : ''
    }`,
)

const titleColorClass = computed(() =>
  props.step.status === 'completed'
    ? 'text-csub-blue-dark'
    : props.step.status === 'in_progress'
      ? 'text-csub-blue-dark'
      : props.step.status === 'locked'
        ? 'text-gray-500'
        : 'text-gray-600',
)

const descriptionColorClass = computed(() =>
  props.step.status === 'locked' || props.step.status === 'waived'
    ? 'text-gray-400'
    : 'text-csub-gray',
)

const completedDateLabel = computed(() =>
  props.completedAt
    ? new Date(props.completedAt).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
    : '',
)

// framer-motion whileInView -> IntersectionObserver-driven reveal (once, margin -20px)
const li = ref<HTMLElement | null>(null)
const inView = ref(false)
let observer: IntersectionObserver | null = null

onMounted(() => {
  if (!li.value) return
  observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          inView.value = true
          if (observer) {
            observer.disconnect()
            observer = null
          }
        }
      }
    },
    { rootMargin: '-20px' },
  )
  observer.observe(li.value)
})

onUnmounted(() => {
  if (observer) observer.disconnect()
})

const handleClick = () => {
  if (props.step.status === 'locked') return
  emit('select')
}
</script>

<template>
  <li
    ref="li"
    :class="`relative pl-14 sm:pl-16 ${isLast ? 'pb-0' : compact ? 'pb-4' : 'pb-8'}`"
    :style="{
      opacity: inView ? 1 : 0,
      transform: inView ? 'translateX(0)' : 'translateX(-8px)',
      transition: `opacity 0.3s ease ${index * 0.03}s, transform 0.3s ease ${index * 0.03}s`,
    }"
    :aria-label="ariaLabel"
  >
    <!-- Timeline node -->
    <div class="absolute left-3 sm:left-3.5 top-1 z-10">
      <div
        :class="`w-5 h-5 sm:w-6 sm:h-6 rounded-full border-2 flex items-center justify-center text-xs font-bold transition-all ${config.nodeClass}`"
      >
        <svg
          v-if="config.icon === 'check'"
          class="w-4 h-4"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          stroke-width="3"
        >
          <path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7" />
        </svg>
        <svg
          v-else-if="config.icon === 'dash'"
          class="w-4 h-4"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          stroke-width="2.5"
        >
          <path stroke-linecap="round" stroke-linejoin="round" d="M20 12H4" />
        </svg>
        <svg
          v-else-if="config.icon === 'lock'"
          class="w-3.5 h-3.5"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          stroke-width="2.5"
        >
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"
          />
        </svg>
        <span v-else class="text-xs">{{ index + 1 }}</span>
      </div>
    </div>

    <!-- Filled spine for completed steps -->
    <div
      v-if="step.status === 'completed' && !isLast"
      class="absolute left-5 sm:left-6 top-5 sm:top-6 bottom-0 w-0.5 bg-csub-gold z-[1]"
      aria-hidden="true"
    />

    <!-- Card -->
    <button
      :disabled="step.status === 'locked'"
      @click="handleClick"
      :class="`w-full text-left rounded-xl border transition-all duration-200 group
        ${compact ? 'px-4 py-3' : 'p-4 sm:p-5'}
        ${step.status === 'locked' ? 'cursor-default' : 'hover:shadow-md focus:outline-none focus:ring-2 focus:ring-csub-blue focus:ring-offset-2'}
        ${config.cardClass}
        ${leftBorderClass}
        ${isActive ? 'ring-1 ring-csub-blue/20' : ''}
      `"
      aria-haspopup="dialog"
    >
      <!-- Compact layout — title only for locked preview -->
      <div v-if="compact" class="flex items-center gap-2.5">
        <span class="text-lg flex-shrink-0" aria-hidden="true">
          {{ step.icon }}
        </span>
        <h3
          class="font-display text-sm font-bold uppercase tracking-wide text-gray-500 flex-1 min-w-0 truncate"
        >
          {{ step.title }}
        </h3>
        <span
          v-if="step.is_optional === 1"
          class="text-[10px] font-body font-semibold text-csub-blue bg-csub-blue/10 rounded-full px-1.5 py-0.5 flex-shrink-0"
        >
          Optional
        </span>
        <span class="text-[11px] font-body text-gray-400 flex-shrink-0 hidden sm:inline">
          Sign in to unlock
        </span>
      </div>

      <!-- Full layout -->
      <div v-else class="flex items-start gap-3">
        <!-- Icon -->
        <span class="text-xl sm:text-2xl flex-shrink-0 mt-0.5" aria-hidden="true">
          {{ step.icon }}
        </span>

        <div class="flex-1 min-w-0">
          <!-- Title row -->
          <div class="flex items-start justify-between gap-2">
            <div class="flex-1 min-w-0">
              <h3
                :class="`font-display text-sm sm:text-base font-bold uppercase tracking-wide leading-tight ${titleColorClass}`"
              >
                {{ step.title }}
              </h3>
              <span
                v-if="step.is_optional === 1"
                class="inline-flex mt-1 text-xs font-body font-semibold text-csub-blue bg-csub-blue/10 rounded-full px-2 py-0.5"
              >
                Optional
              </span>
            </div>

            <!-- View details arrow -->
            <svg
              v-if="step.status !== 'locked'"
              class="w-4 h-4 text-gray-300 group-hover:text-csub-blue transition-colors flex-shrink-0 mt-0.5"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M9 5l7 7-7 7"
              />
            </svg>
          </div>

          <!-- Description -->
          <p :class="`font-body text-xs sm:text-sm leading-relaxed mt-1 ${descriptionColorClass}`">
            {{ step.description }}
          </p>

          <!-- Meta row: badges, deadline, action -->
          <div class="flex flex-wrap items-center gap-2 mt-3">
            <!-- Status badge -->
            <span
              v-if="config.badgeLabel"
              :class="`inline-flex items-center gap-1 text-xs font-body font-semibold rounded-full px-2.5 py-0.5 ${config.badgeClass}`"
            >
              <span
                v-if="step.status === 'in_progress'"
                class="w-1.5 h-1.5 bg-csub-blue rounded-full animate-pulse"
                aria-hidden="true"
              />
              {{ config.badgeLabel }}
            </span>

            <!-- Deadline -->
            <span
              v-if="step.deadline"
              class="text-xs font-body font-medium text-amber-700 bg-amber-50 rounded-full px-2.5 py-0.5"
            >
              {{ step.deadline }}
            </span>

            <!-- Deadline countdown -->
            <DeadlineCountdown :deadline-date="step.deadline_date" :status="step.status" />

            <!-- Completed date -->
            <span
              v-if="step.status === 'completed' && completedAt"
              class="text-xs font-body text-gray-400"
            >
              {{ completedDateLabel }}
            </span>

            <!-- Spacer -->
            <div class="flex-1" />

            <!-- Action CTA for in-progress step -->
            <a
              v-if="primaryAction"
              :href="safeUrl(primaryAction.url)"
              target="_blank"
              rel="noopener noreferrer"
              @click.stop
              class="inline-flex items-center gap-1 text-xs font-display font-bold uppercase tracking-wider text-csub-blue hover:text-csub-blue-dark transition-colors"
            >
              {{ primaryAction.label || 'Get Started' }}
              <svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2.5"
                  d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
                />
              </svg>
            </a>
          </div>
        </div>
      </div>
    </button>
  </li>
</template>
