<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'

const props = defineProps<{
  completedCount: number
  totalSteps: number
  percentage: number
  currentStepTitle?: string
  allComplete: boolean
}>()

// Animate the bar from width 0 to the completion percentage (easeOut, 0.8s).
const barWidth = ref('0%')
onMounted(() => {
  requestAnimationFrame(() => {
    barWidth.value = `${props.percentage}%`
  })
})

const barBackground = computed(() =>
  props.allComplete
    ? 'linear-gradient(90deg, #003594, #FFC72C)'
    : 'linear-gradient(90deg, #003594, #0052CC)',
)
</script>

<template>
  <div class="sticky top-0 z-40 bg-white border-b border-gray-200 shadow-sm">
    <div class="max-w-4xl mx-auto px-4 sm:px-6 py-3 sm:py-4">
      <!-- Progress text -->
      <div class="flex items-center justify-between mb-2">
        <div class="flex items-center gap-3">
          <span class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wider">
            Your Progress
          </span>
          <span
            class="font-body text-xs font-semibold text-white bg-csub-blue rounded-full px-2.5 py-0.5"
            :aria-label="`${percentage} percent complete`"
          >
            {{ percentage }}%
          </span>
        </div>
        <span class="font-body text-sm text-csub-gray">
          <span class="font-semibold text-csub-blue-dark">{{ completedCount }}</span> of
          {{ totalSteps }} steps
        </span>
      </div>

      <!-- Progress bar -->
      <div
        class="h-2.5 bg-gray-100 rounded-full overflow-hidden"
        role="progressbar"
        :aria-valuenow="percentage"
        :aria-valuemin="0"
        :aria-valuemax="100"
        :aria-label="`${completedCount} of ${totalSteps} steps completed`"
      >
        <div
          class="h-full rounded-full"
          :style="{
            background: barBackground,
            width: barWidth,
            transition: 'width 0.8s ease-out',
          }"
        />
      </div>

      <!-- Current step hint -->
      <div aria-live="polite" aria-atomic="true">
        <p v-if="currentStepTitle && !allComplete" class="font-body text-xs text-csub-gray mt-1.5">
          Next up: <span class="font-semibold text-csub-blue-dark">{{ currentStepTitle }}</span>
        </p>
        <p v-if="allComplete" class="font-body text-xs text-csub-blue font-semibold mt-1.5">
          All steps completed — you're all set!
        </p>
      </div>
    </div>
  </div>
</template>
