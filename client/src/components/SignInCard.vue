<script setup lang="ts">
// Sign-in card used in two places on the public roadmap preview:
// 1. The error/empty-state fallback at the top of the page
// 2. The milestone card shown between the public steps and the locked preview
//
// Props forward the auth store state so the card stays stateless — the parent
// (PublicRoadmapPreview) owns the reactive refs and login handler.

defineProps<{
  isAzureAdConfigured: boolean
  ssoLoading: boolean
  ssoError: string | null | undefined
  showDevLogin: boolean
  loggingIn: boolean
  loginError: string
  nameInputId: string
  emailInputId: string
  loginName: string
  loginEmail: string
}>()

const emit = defineEmits<{
  (e: 'ssoLogin'): void
  (e: 'update:loginName', value: string): void
  (e: 'update:loginEmail', value: string): void
  (e: 'submit', event: Event): void
}>()
</script>

<template>
  <div class="p-5 sm:p-6 bg-white rounded-xl border-2 border-csub-blue/20 shadow-sm">
    <div class="flex items-center gap-2 mb-1">
      <svg
        class="w-5 h-5 text-csub-blue flex-shrink-0"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
        :stroke-width="2"
      >
        <path
          stroke-linecap="round"
          stroke-linejoin="round"
          d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z"
        />
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
        @click="emit('ssoLogin')"
        :disabled="ssoLoading"
        class="w-full px-5 py-3 bg-csub-blue hover:bg-csub-blue-dark text-white rounded-lg font-body text-sm font-bold transition-colors duration-200 disabled:opacity-50 flex items-center justify-center gap-2"
      >
        <template v-if="ssoLoading">
          <div
            class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"
            aria-hidden="true"
          />
          Signing in...
        </template>
        <template v-else> Sign in with CSUB Account </template>
      </button>
      <p v-if="ssoError" role="alert" class="text-red-600 text-sm font-body mt-2">
        {{ ssoError }}
      </p>
    </template>

    <!-- Divider — shown when both SSO and dev login are visible -->
    <div v-if="isAzureAdConfigured && showDevLogin" class="flex items-center gap-3 my-3">
      <div class="flex-1 h-px bg-gray-200" />
      <span class="text-xs font-body text-gray-400">or</span>
      <div class="flex-1 h-px bg-gray-200" />
    </div>

    <!-- Dev login form — dev builds only -->
    <template v-if="showDevLogin">
      <form
        @submit="emit('submit', $event)"
        class="flex flex-wrap items-end gap-3"
        :aria-describedby="loginError ? `${emailInputId}-error` : undefined"
      >
        <div class="flex-1 min-w-[120px]">
          <label
            :for="nameInputId"
            class="block font-body text-xs font-semibold text-csub-blue-dark/70 mb-1"
            >Name</label
          >
          <input
            :id="nameInputId"
            type="text"
            required
            :value="loginName"
            @input="emit('update:loginName', ($event.target as HTMLInputElement).value)"
            placeholder="Jane Doe"
            class="w-full px-3 py-3 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue focus:border-transparent"
          />
        </div>
        <div class="flex-1 min-w-[160px]">
          <label
            :for="emailInputId"
            class="block font-body text-xs font-semibold text-csub-blue-dark/70 mb-1"
            >Email</label
          >
          <input
            :id="emailInputId"
            type="email"
            required
            :value="loginEmail"
            @input="emit('update:loginEmail', ($event.target as HTMLInputElement).value)"
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
      <p
        v-if="loginError"
        :id="`${emailInputId}-error`"
        role="alert"
        class="text-red-600 text-sm font-body mt-2"
      >
        {{ loginError }}
      </p>
    </template>
  </div>
</template>
