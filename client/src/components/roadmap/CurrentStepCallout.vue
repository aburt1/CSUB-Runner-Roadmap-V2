<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { safeUrl } from '../../utils/links'
import type { StepWithStatus, LinkItem } from '../../types/api'

const props = defineProps<{
  step: StepWithStatus
  stepNumber: number
}>()

const emit = defineEmits<{
  (e: 'viewDetails'): void
}>()

const links = computed<LinkItem[]>(() =>
  props.step.links
    ? typeof props.step.links === 'string'
      ? JSON.parse(props.step.links)
      : props.step.links
    : [],
)
const primaryAction = computed<LinkItem | null>(() =>
  links.value.length > 0 ? links.value[0]! : null,
)

// framer-motion: initial opacity 0 / y 12 -> animate opacity 1 / y 0 (0.4s)
const shown = ref(false)
onMounted(() => {
  requestAnimationFrame(() => {
    shown.value = true
  })
})
</script>

<template>
  <section
    class="mt-6 mb-8"
    aria-label="Current step"
    :style="{
      opacity: shown ? 1 : 0,
      transform: shown ? 'translateY(0)' : 'translateY(12px)',
      transition: 'opacity 0.4s ease, transform 0.4s ease',
    }"
  >
    <div class="bg-white rounded-xl border-2 border-csub-blue shadow-lg overflow-hidden">
      <!-- Blue accent bar -->
      <div class="h-1.5 bg-gradient-to-r from-csub-blue to-csub-gold" aria-hidden="true" />

      <div class="p-5 sm:p-6">
        <div class="flex items-start gap-4">
          <!-- Step icon -->
          <div
            class="flex-shrink-0 w-12 h-12 rounded-full bg-csub-blue/10 flex items-center justify-center"
          >
            <span class="text-2xl" aria-hidden="true">{{ step.icon }}</span>
          </div>

          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2 mb-1">
              <span
                class="inline-flex items-center gap-1 text-xs font-body font-bold text-csub-blue bg-csub-blue/10 rounded-full px-2.5 py-0.5"
              >
                <span
                  class="w-1.5 h-1.5 bg-csub-blue rounded-full animate-pulse"
                  aria-hidden="true"
                />
                Step {{ stepNumber }} — Next Up
              </span>
              <span
                v-if="step.deadline"
                class="text-xs font-body font-semibold text-amber-700 bg-amber-50 rounded-full px-2.5 py-0.5"
              >
                {{ step.deadline }}
              </span>
            </div>

            <h2
              class="font-display text-lg sm:text-xl font-bold text-csub-blue-dark uppercase tracking-wide"
            >
              {{ step.title }}
            </h2>
            <p class="font-body text-sm text-csub-gray mt-1 leading-relaxed">
              {{ step.description }}
            </p>

            <!-- Actions -->
            <div class="flex flex-wrap items-center gap-3 mt-4">
              <a
                v-if="primaryAction"
                :href="safeUrl(primaryAction.url)"
                target="_blank"
                rel="noopener noreferrer"
                class="inline-flex items-center gap-2 bg-csub-blue hover:bg-csub-blue-dark text-white font-display font-bold uppercase tracking-wider text-sm px-5 py-2.5 rounded-lg shadow transition-colors"
              >
                {{ step.actionLabel || primaryAction.label || 'Get Started' }}
                <svg
                  class="w-4 h-4"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  aria-hidden="true"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M14 5l7 7m0 0l-7 7m7-7H3"
                  />
                </svg>
              </a>
              <button
                @click="emit('viewDetails')"
                :aria-label="`View details for ${step.title}`"
                class="inline-flex items-center gap-1.5 font-body text-sm font-semibold text-csub-blue hover:text-csub-blue-dark transition-colors"
              >
                View details
                <svg
                  class="w-3.5 h-3.5"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  aria-hidden="true"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2.5"
                    d="M9 5l7 7-7 7"
                  />
                </svg>
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>
