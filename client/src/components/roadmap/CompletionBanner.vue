<script setup lang="ts">
import { ref, onMounted } from 'vue'

defineProps<{
  firstName: string
}>()

// Entrance transition: fade in and slide up from y 12 -> 0 over 0.5s.
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
    aria-label="All steps completed"
    :style="{
      opacity: shown ? 1 : 0,
      transform: shown ? 'translateY(0)' : 'translateY(12px)',
      transition: 'opacity 0.5s ease, transform 0.5s ease',
    }"
  >
    <div
      class="bg-gradient-to-br from-csub-blue-dark to-csub-blue rounded-xl p-6 sm:p-8 text-center text-white shadow-lg overflow-hidden relative"
    >
      <!-- Decorative gold accent -->
      <div
        class="absolute top-0 left-0 right-0 h-1 bg-gradient-to-r from-csub-gold via-amber-300 to-csub-gold"
      />

      <div class="relative z-10">
        <div class="text-4xl mb-3" aria-hidden="true">🎉</div>
        <h2 class="font-display text-2xl sm:text-3xl font-bold uppercase tracking-wide mb-2">
          Congratulations, {{ firstName }}!
        </h2>
        <p class="font-body text-white/80 text-sm sm:text-base max-w-md mx-auto mb-1">
          You've completed all required admissions steps.
        </p>
        <p class="font-display text-csub-gold text-lg font-bold uppercase tracking-widest">
          Welcome to CSUB, Runner!
        </p>
      </div>

      <!-- Subtle background pattern -->
      <div
        class="absolute inset-0 opacity-5"
        :style="{
          backgroundImage:
            'radial-gradient(circle at 20% 80%, white 1px, transparent 1px), radial-gradient(circle at 80% 20%, white 1px, transparent 1px)',
          backgroundSize: '60px 60px',
        }"
      />
    </div>
  </section>
</template>
