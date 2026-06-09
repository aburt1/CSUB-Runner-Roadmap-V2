<script setup lang="ts">
import TimelineStep from './TimelineStep.vue'
import type { StepWithStatus } from '../../types/api'

const props = defineProps<{
  steps: StepWithStatus[]
  completedDates: Record<number, string | null>
}>()

const emit = defineEmits<{
  (e: 'selectStep', step: StepWithStatus): void
}>()
</script>

<template>
  <section aria-label="Admissions roadmap steps">
    <ol class="relative" aria-label="Admissions steps in order">
      <!-- Vertical timeline spine -->
      <div class="absolute left-5 sm:left-6 top-0 bottom-0 w-0.5 bg-gray-200" aria-hidden="true" />

      <TimelineStep
        v-for="(step, index) in props.steps"
        :key="step.id"
        :step="step"
        :index="index"
        :completed-at="props.completedDates[step.id]"
        :is-last="index === props.steps.length - 1"
        @select="emit('selectStep', step)"
      />
    </ol>
  </section>
</template>
