<script setup lang="ts">
withDefaults(defineProps<{ className?: string }>(), {
  className: '',
})

const prefersReducedMotion =
  typeof window !== 'undefined' &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches
</script>

<template>
  <div
    :class="['inline-block', className, { 'roadrunner-bob': !prefersReducedMotion }]"
    aria-hidden="true"
  >
    <svg
      width="48"
      height="48"
      viewBox="0 0 64 64"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <!-- Body -->
      <ellipse cx="30" cy="36" rx="16" ry="14" fill="#003594" />
      <!-- Belly -->
      <ellipse cx="30" cy="40" rx="10" ry="9" fill="#FFC72C" />
      <!-- Head -->
      <circle cx="44" cy="24" r="10" fill="#003594" />
      <!-- Eye -->
      <circle cx="47" cy="22" r="4" fill="white" />
      <circle cx="48" cy="21.5" r="2" fill="#1a1a1a" />
      <circle cx="49" cy="20.5" r="0.8" fill="white" />
      <!-- Beak -->
      <polygon points="54,23 64,21 54,25" fill="#FFC72C" />
      <!-- Crest/head feathers -->
      <path
        d="M 40,16 Q 42,8 48,12 Q 44,10 44,16"
        fill="#003594"
        stroke="#003594"
        stroke-width="1"
      />
      <path
        d="M 43,14 Q 46,7 50,10 Q 47,9 46,14"
        fill="#0047B3"
        stroke="#0047B3"
        stroke-width="1"
      />
      <!-- Tail feathers -->
      <path d="M 14,30 Q 4,24 8,32 Q 2,30 10,36" fill="#0047B3" />
      <path d="M 14,33 Q 6,30 10,36" fill="#003594" />
      <!-- Legs -->
      <line x1="26" y1="48" x2="22" y2="58" stroke="#FFC72C" stroke-width="3" stroke-linecap="round" />
      <line x1="34" y1="48" x2="36" y2="58" stroke="#FFC72C" stroke-width="3" stroke-linecap="round" />
      <!-- Feet -->
      <path d="M 18,57 L 22,58 L 18,60" stroke="#FFC72C" stroke-width="2" stroke-linecap="round" fill="none" />
      <path d="M 32,57 L 36,58 L 32,60" stroke="#FFC72C" stroke-width="2" stroke-linecap="round" fill="none" />
      <!-- Wing -->
      <ellipse cx="24" cy="34" rx="8" ry="5" fill="#0047B3" transform="rotate(-15, 24, 34)" />
    </svg>
  </div>
</template>

<style scoped>
@keyframes roadrunner-bob {
  0% {
    transform: translateY(0) rotate(0deg);
  }
  25% {
    transform: translateY(-6px) rotate(-3deg);
  }
  50% {
    transform: translateY(0) rotate(0deg);
  }
  75% {
    transform: translateY(-6px) rotate(3deg);
  }
  100% {
    transform: translateY(0) rotate(0deg);
  }
}

.roadrunner-bob {
  animation: roadrunner-bob 2s ease-in-out infinite;
}
</style>
