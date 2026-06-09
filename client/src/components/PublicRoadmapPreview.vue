<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { storeToRefs } from 'pinia'
import TimelineStep from './roadmap/TimelineStep.vue'
import StepDetailPanel from './roadmap/StepDetailPanel.vue'
import HelpSection from './roadmap/HelpSection.vue'
import { useAuthStore } from '../stores/auth'
import type { Step, StepWithStatus } from '../types/api'

const props = defineProps<{
  onLogin: (name: string, email: string) => Promise<void>
}>()

const authStore = useAuthStore()
const { ssoLoading, ssoError, isAzureAdConfigured } = storeToRefs(authStore)
const { ssoLogin } = authStore

const steps = ref<Step[]>([])
const loading = ref<boolean>(true)
const fetchError = ref<boolean>(false)
const loginName = ref<string>('')
const loginEmail = ref<string>('')
const loginError = ref<string>('')
const loggingIn = ref<boolean>(false)
const selectedStep = ref<Step | null>(null)

onMounted(() => {
  fetch('/api/steps')
    .then((r) => r.json())
    .then((data: Step[]) => {
      steps.value = data.sort((a, b) => a.sort_order - b.sort_order)
    })
    .catch(() => {
      fetchError.value = true
    })
    .finally(() => {
      loading.value = false
    })
})

const publicSteps = computed(() => steps.value.filter((s) => s.is_public === 1))
const lockedSteps = computed(() => steps.value.filter((s) => s.is_public !== 1))

async function handleLogin(e: Event): Promise<void> {
  e.preventDefault()
  loginError.value = ''
  loggingIn.value = true
  try {
    await props.onLogin(loginName.value, loginEmail.value)
  } catch {
    loginError.value = 'Login failed. Make sure the server is running.'
  } finally {
    loggingIn.value = false
  }
}

// Dev-login is a development convenience (the server 404s the endpoint in Production).
// Show it only in dev builds, or when explicitly opted in at build time.
const showDevLogin = import.meta.env.DEV || import.meta.env.VITE_ALLOW_DEV_LOGIN === 'true'

// Step detail panel navigation/index helpers (public steps only)
const selectedIndex = computed(() =>
  selectedStep.value ? publicSteps.value.findIndex((s) => s.id === selectedStep.value!.id) : -1,
)

function onPanelNavigate(direction: 'prev' | 'next'): void {
  if (!selectedStep.value) return
  const idx = publicSteps.value.findIndex((s) => s.id === selectedStep.value!.id)
  const next = direction === 'next' ? publicSteps.value[idx + 1] : publicSteps.value[idx - 1]
  if (next) selectedStep.value = next
}

function asPreview(step: Step): StepWithStatus {
  return { ...step, status: 'preview' } as StepWithStatus
}

function asLocked(step: Step): StepWithStatus {
  return { ...step, status: 'locked' } as StepWithStatus
}
</script>

<template>
  <div
    v-if="loading"
    class="min-h-screen flex items-center justify-center bg-gray-50"
    role="status"
    aria-label="Loading"
  >
    <div class="text-center">
      <div class="w-10 h-10 border-4 border-csub-blue border-t-transparent rounded-full animate-spin mx-auto mb-4" aria-hidden="true" />
      <p class="text-csub-blue font-display text-lg font-semibold uppercase tracking-wider">
        Loading...
      </p>
    </div>
  </div>

  <div v-else class="min-h-screen bg-gray-50">
    <!-- Skip link -->
    <a href="#public-steps" class="skip-link">Skip to steps</a>

    <!-- Header -->
    <header class="bg-csub-blue-dark text-white">
      <div class="max-w-4xl mx-auto px-4 sm:px-6 py-8 sm:py-10 text-center">
        <p class="font-body text-csub-gold text-sm font-semibold tracking-wide mb-2">
          Welcome, Future Roadrunner!
        </p>
        <h1 class="font-display text-3xl sm:text-4xl md:text-5xl font-bold uppercase tracking-wide">
          Road to Becoming a
          <span class="text-csub-gold">Roadrunner</span>
        </h1>
        <p class="font-body text-white/70 text-sm sm:text-base mt-3 max-w-lg mx-auto">
          Complete the first steps below to activate your account, then sign in to track your full admissions journey.
        </p>
      </div>
    </header>

    <main id="public-steps" class="max-w-4xl mx-auto px-4 sm:px-6 pb-16">
      <!-- Error/empty state -->
      <section v-if="fetchError || steps.length === 0" class="mt-8">
        <div class="text-center py-8">
          <p class="font-body text-csub-gray text-sm">
            {{
              fetchError
                ? "We couldn't load the admissions checklist right now. Please check back soon."
                : 'No steps available yet. Check back soon!'
            }}
          </p>
        </div>

        <!-- Login form -->
        <div class="p-5 sm:p-6 bg-white rounded-xl border-2 border-csub-blue/20 shadow-sm">
          <div class="flex items-center gap-2 mb-1">
            <svg class="w-5 h-5 text-csub-blue flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
              <path stroke-linecap="round" stroke-linejoin="round" d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z" />
            </svg>
            <p class="font-display text-sm font-bold uppercase tracking-wider text-csub-blue-dark">
              {{
                isAzureAdConfigured
                  ? 'Sign in to track your progress'
                  : 'Activated your account? Sign in below'
              }}
            </p>
          </div>
          <p class="font-body text-xs text-csub-gray mb-4">
            Once you've completed the steps above, sign in to unlock your full admissions checklist.
          </p>

          <!-- SSO Button — only when Azure AD is configured -->
          <template v-if="isAzureAdConfigured">
            <button
              type="button"
              @click="ssoLogin"
              :disabled="ssoLoading"
              class="w-full px-5 py-3 bg-csub-blue hover:bg-csub-blue-dark text-white rounded-lg font-body text-sm font-bold transition-colors duration-200 disabled:opacity-50 flex items-center justify-center gap-2"
            >
              <template v-if="ssoLoading">
                <div class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin" aria-hidden="true" />
                Signing in...
              </template>
              <template v-else>
                Sign in with CSUB Account
              </template>
            </button>
            <p v-if="ssoError" role="alert" class="text-red-600 text-sm font-body mt-2">{{ ssoError }}</p>
          </template>

          <!-- Divider — shown when both SSO and dev login are visible -->
          <div v-if="isAzureAdConfigured && showDevLogin" class="flex items-center gap-3 my-3">
            <div class="flex-1 h-px bg-gray-200" />
            <span class="text-xs font-body text-gray-400">or</span>
            <div class="flex-1 h-px bg-gray-200" />
          </div>

          <!-- Dev login form -->
          <template v-if="showDevLogin">
            <form
              @submit="handleLogin"
              class="flex flex-wrap items-end gap-3"
              :aria-describedby="loginError ? 'login-error' : undefined"
            >
              <div class="flex-1 min-w-[120px]">
                <label for="login-name" class="block font-body text-xs font-semibold text-csub-blue-dark/70 mb-1">Name</label>
                <input
                  id="login-name"
                  type="text"
                  required
                  v-model="loginName"
                  placeholder="Jane Doe"
                  class="w-full px-3 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
                />
              </div>
              <div class="flex-1 min-w-[160px]">
                <label for="login-email" class="block font-body text-xs font-semibold text-csub-blue-dark/70 mb-1">Email</label>
                <input
                  id="login-email"
                  type="email"
                  required
                  v-model="loginEmail"
                  placeholder="jdoe@csub.edu"
                  class="w-full px-3 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
                />
              </div>
              <button
                type="submit"
                :disabled="loggingIn"
                class="px-5 py-3 bg-csub-blue hover:bg-csub-blue-dark text-white rounded-lg font-body text-sm font-semibold transition-colors duration-200 disabled:opacity-50 whitespace-nowrap"
              >
                {{ loggingIn ? 'Signing in...' : 'Sign In' }}
              </button>
            </form>
            <p v-if="loginError" id="login-error" role="alert" class="text-red-600 text-sm font-body mt-2">{{ loginError }}</p>
          </template>
        </div>
      </section>

      <template v-else>
        <!-- Phase 1: Get Started -->
        <section class="mt-8" aria-label="Get started steps">
          <div class="flex items-center gap-3 mb-1">
            <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wider">
              Get Started
            </h2>
            <span class="font-body text-xs font-semibold text-csub-blue bg-csub-blue/10 rounded-full px-2.5 py-0.5">
              {{ publicSteps.length }} {{ publicSteps.length === 1 ? 'step' : 'steps' }}
            </span>
          </div>
          <p class="font-body text-sm text-csub-gray mb-4">
            Complete these steps to activate your CSUB account.
          </p>

          <ol class="relative" role="list">
            <!-- Timeline spine -->
            <div
              class="absolute left-5 sm:left-6 top-4 bottom-4 w-0.5 bg-csub-blue/20"
              aria-hidden="true"
            />

            <!-- Public steps — shown as preview -->
            <TimelineStep
              v-for="(step, i) in publicSteps"
              :key="step.id"
              :step="asPreview(step)"
              :index="i"
              :is-last="i === publicSteps.length - 1"
              @select="selectedStep = step"
            />
          </ol>
        </section>

        <!-- Phase 2: Sign-in milestone -->
        <div class="my-6 prp-milestone">
          <!-- Login form -->
          <div class="p-5 sm:p-6 bg-white rounded-xl border-2 border-csub-blue/20 shadow-sm">
            <div class="flex items-center gap-2 mb-1">
              <svg class="w-5 h-5 text-csub-blue flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" :stroke-width="2">
                <path stroke-linecap="round" stroke-linejoin="round" d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z" />
              </svg>
              <p class="font-display text-sm font-bold uppercase tracking-wider text-csub-blue-dark">
                {{
                  isAzureAdConfigured
                    ? 'Sign in to track your progress'
                    : 'Activated your account? Sign in below'
                }}
              </p>
            </div>
            <p class="font-body text-xs text-csub-gray mb-4">
              Once you've completed the steps above, sign in to unlock your full admissions checklist.
            </p>

            <!-- SSO Button — only when Azure AD is configured -->
            <template v-if="isAzureAdConfigured">
              <button
                type="button"
                @click="ssoLogin"
                :disabled="ssoLoading"
                class="w-full px-5 py-3 bg-csub-blue hover:bg-csub-blue-dark text-white rounded-lg font-body text-sm font-bold transition-colors duration-200 disabled:opacity-50 flex items-center justify-center gap-2"
              >
                <template v-if="ssoLoading">
                  <div class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin" aria-hidden="true" />
                  Signing in...
                </template>
                <template v-else>
                  Sign in with CSUB Account
                </template>
              </button>
              <p v-if="ssoError" role="alert" class="text-red-600 text-sm font-body mt-2">{{ ssoError }}</p>
            </template>

            <!-- Divider — shown when both SSO and dev login are visible -->
            <div v-if="isAzureAdConfigured && showDevLogin" class="flex items-center gap-3 my-3">
              <div class="flex-1 h-px bg-gray-200" />
              <span class="text-xs font-body text-gray-400">or</span>
              <div class="flex-1 h-px bg-gray-200" />
            </div>

            <!-- Dev login form -->
            <template v-if="showDevLogin">
              <form
                @submit="handleLogin"
                class="flex flex-wrap items-end gap-3"
                :aria-describedby="loginError ? 'login-error' : undefined"
              >
                <div class="flex-1 min-w-[120px]">
                  <label for="login-name-milestone" class="block font-body text-xs font-semibold text-csub-blue-dark/70 mb-1">Name</label>
                  <input
                    id="login-name-milestone"
                    type="text"
                    required
                    v-model="loginName"
                    placeholder="Jane Doe"
                    class="w-full px-3 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
                  />
                </div>
                <div class="flex-1 min-w-[160px]">
                  <label for="login-email-milestone" class="block font-body text-xs font-semibold text-csub-blue-dark/70 mb-1">Email</label>
                  <input
                    id="login-email-milestone"
                    type="email"
                    required
                    v-model="loginEmail"
                    placeholder="jdoe@csub.edu"
                    class="w-full px-3 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
                  />
                </div>
                <button
                  type="submit"
                  :disabled="loggingIn"
                  class="px-5 py-3 bg-csub-blue hover:bg-csub-blue-dark text-white rounded-lg font-body text-sm font-semibold transition-colors duration-200 disabled:opacity-50 whitespace-nowrap"
                >
                  {{ loggingIn ? 'Signing in...' : 'Sign In' }}
                </button>
              </form>
              <p v-if="loginError" id="login-error" role="alert" class="text-red-600 text-sm font-body mt-2">{{ loginError }}</p>
            </template>
          </div>
        </div>

        <!-- Phase 3: What's ahead preview -->
        <section v-if="lockedSteps.length > 0" aria-label="Upcoming steps preview">
          <div class="flex items-center gap-3 mb-1">
            <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wider">
              What's Ahead
            </h2>
            <span class="font-body text-xs font-semibold text-gray-500 bg-gray-100 rounded-full px-2.5 py-0.5">
              {{ lockedSteps.length }} more {{ lockedSteps.length === 1 ? 'step' : 'steps' }}
            </span>
          </div>
          <p class="font-body text-sm text-csub-gray mb-4">
            Sign in to unlock these steps and track your progress.
          </p>

          <div class="relative">
            <ol class="relative" role="list">
              <div
                class="absolute left-5 sm:left-6 top-4 bottom-4 w-0.5 bg-gray-200"
                aria-hidden="true"
              />
              <TimelineStep
                v-for="(step, i) in lockedSteps"
                :key="step.id"
                :step="asLocked(step)"
                :index="publicSteps.length + i"
                :is-last="i === lockedSteps.length - 1"
                compact
                @select="() => {}"
              />
            </ol>
            <!-- Fade-out gradient -->
            <div class="absolute bottom-0 left-0 right-0 h-24 bg-gradient-to-t from-gray-50 to-transparent pointer-events-none" />
          </div>
        </section>
      </template>

      <!-- Help footer — same as authenticated roadmap -->
      <HelpSection />
    </main>

    <!-- Step Detail Panel — public steps only -->
    <StepDetailPanel
      v-if="selectedStep"
      :step="asPreview(selectedStep)"
      :step-number="selectedIndex + 1"
      :total-steps="publicSteps.length"
      :has-prev="selectedIndex > 0"
      :has-next="selectedIndex < publicSteps.length - 1"
      @close="selectedStep = null"
      @navigate="onPanelNavigate"
    />
  </div>
</template>

<style scoped>
/* Sign-in milestone fade/slide-up (initial opacity 0 y 8 -> opacity 1 y 0) */
@keyframes prp-milestone-in {
  from {
    opacity: 0;
    transform: translateY(8px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.prp-milestone {
  animation: prp-milestone-in 0.3s ease 0.2s both;
}
</style>
