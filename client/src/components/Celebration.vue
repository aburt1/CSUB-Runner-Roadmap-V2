<script setup lang="ts">
import { onMounted, onUnmounted, ref, useTemplateRef } from 'vue'
import confetti from 'canvas-confetti'

const emit = defineEmits<{ (e: 'close'): void }>()

function onClose(): void {
  emit('close')
}

const dialogRef = useTemplateRef<HTMLDivElement>('dialogRef')
const prefersReducedMotion =
  typeof window !== 'undefined' &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches

// Drives the CSS entrance transitions (matches framer-motion initial -> animate).
const entered = ref(false)

function handleKeyDown(e: KeyboardEvent): void {
  if (e.key === 'Escape') onClose()

  if (e.key === 'Tab' && dialogRef.value) {
    const focusable = dialogRef.value.querySelectorAll<HTMLElement>(
      'button:not([disabled]), [href], input:not([disabled])',
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

// Confetti animation — skip if user prefers reduced motion
onMounted(() => {
  if (!prefersReducedMotion) {
    const duration = 3000
    const end = Date.now() + duration
    const csubColors = ['#003594', '#FFC72C', '#ffffff']

    const frame = (): void => {
      confetti({
        particleCount: 3,
        angle: 60,
        spread: 55,
        origin: { x: 0 },
        colors: csubColors,
      })
      confetti({
        particleCount: 3,
        angle: 120,
        spread: 55,
        origin: { x: 1 },
        colors: csubColors,
      })

      if (Date.now() < end) {
        requestAnimationFrame(frame)
      }
    }

    frame()
  }

  // Focus trap + keyboard dismiss
  document.addEventListener('keydown', handleKeyDown)
  dialogRef.value?.focus()

  // Trigger entrance transitions on next frame
  requestAnimationFrame(() => {
    entered.value = true
  })
})

onUnmounted(() => {
  document.removeEventListener('keydown', handleKeyDown)
})
</script>

<template>
  <div
    ref="dialogRef"
    :tabindex="-1"
    role="dialog"
    aria-modal="true"
    aria-label="Celebration — all steps complete"
    class="fixed inset-0 z-50 flex items-center justify-center bg-csub-blue-dark/60 backdrop-blur-sm focus:outline-none"
    :class="prefersReducedMotion ? '' : ['celebration-overlay', { 'is-entered': entered }]"
    @click="onClose"
  >
    <div
      class="bg-white rounded-2xl p-8 md:p-12 max-w-md mx-4 text-center shadow-2xl border border-gray-100"
      :class="prefersReducedMotion ? '' : ['celebration-card', { 'is-entered': entered }]"
      @click.stop
    >
      <!-- Shield crest -->
      <div class="flex justify-center mb-6">
        <div :class="prefersReducedMotion ? '' : ['celebration-shield', { 'is-entered': entered }]">
          <svg width="80" height="96" viewBox="0 0 80 96" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
            <!-- Shield body -->
            <path
              d="M40 2 L76 16 L76 52 C76 72 60 88 40 94 C20 88 4 72 4 52 L4 16 Z"
              fill="#003594"
              stroke="#FFC72C"
              stroke-width="3"
            />
            <!-- Rising sun rays -->
            <g opacity="0.3">
              <line x1="40" y1="58" x2="20" y2="38" stroke="#FFC72C" stroke-width="1.5" />
              <line x1="40" y1="58" x2="28" y2="32" stroke="#FFC72C" stroke-width="1.5" />
              <line x1="40" y1="58" x2="40" y2="28" stroke="#FFC72C" stroke-width="1.5" />
              <line x1="40" y1="58" x2="52" y2="32" stroke="#FFC72C" stroke-width="1.5" />
              <line x1="40" y1="58" x2="60" y2="38" stroke="#FFC72C" stroke-width="1.5" />
            </g>
            <!-- Sun arc -->
            <path
              d="M18 62 Q40 42 62 62"
              fill="#FFC72C"
              opacity="0.9"
            />
            <!-- Horizon line -->
            <line x1="14" y1="62" x2="66" y2="62" stroke="#FFC72C" stroke-width="2" />
            <!-- Checkmark -->
            <path
              d="M28 48 L36 56 L54 34"
              stroke="#FFC72C"
              stroke-width="4"
              stroke-linecap="round"
              stroke-linejoin="round"
              fill="none"
            />
          </svg>
        </div>
      </div>

      <h2 class="font-display text-3xl font-bold text-csub-blue-dark uppercase tracking-wide mb-2">
        Congratulations!
      </h2>

      <div class="w-16 h-0.5 bg-csub-gold mx-auto my-4" aria-hidden="true" />

      <p class="font-body text-base text-csub-gray mb-1">
        All steps complete. You are officially ready for
      </p>
      <p class="font-display text-xl font-bold text-csub-blue uppercase tracking-wider mb-6">
        Your first day at CSU Bakersfield
      </p>

      <p class="font-display text-csub-gold text-lg font-bold uppercase tracking-widest mb-8">
        Go Runners!
      </p>

      <button
        @click="onClose"
        class="bg-csub-blue text-white font-display font-bold uppercase tracking-wider py-3 px-10 rounded
               hover:bg-csub-blue-dark transition-colors shadow-lg
               hover:shadow-xl active:scale-95 transform text-sm"
      >
        Continue
      </button>
    </div>
  </div>
</template>

<style scoped>
/* Overlay fade (initial opacity 0 -> animate opacity 1) */
.celebration-overlay {
  opacity: 0;
  transition: opacity 0.2s ease;
}
.celebration-overlay.is-entered {
  opacity: 1;
}

/* Card spring scale/opacity (initial scale 0.8 opacity 0 -> scale 1 opacity 1) */
.celebration-card {
  opacity: 0;
  transform: scale(0.8);
  transition:
    opacity 0.3s ease,
    transform 0.45s cubic-bezier(0.34, 1.56, 0.64, 1);
}
.celebration-card.is-entered {
  opacity: 1;
  transform: scale(1);
}

/* Shield drop (initial y -20 -> y 0) with a slight delay */
.celebration-shield {
  transform: translateY(-20px);
  transition: transform 0.45s cubic-bezier(0.34, 1.56, 0.64, 1) 0.2s;
}
.celebration-shield.is-entered {
  transform: translateY(0);
}
</style>
