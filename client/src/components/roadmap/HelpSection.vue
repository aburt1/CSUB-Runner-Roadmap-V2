<script setup lang="ts"></script>

<template>
  <section class="mt-12 mb-4" aria-label="Help and support">
    <div class="bg-white rounded-xl border border-gray-200 p-5 sm:p-6">
      <div class="flex items-start gap-4">
        <div
          class="flex-shrink-0 w-10 h-10 rounded-full bg-csub-blue/10 flex items-center justify-center"
        >
          <svg class="w-5 h-5 text-csub-blue" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
        </div>
        <div class="flex-1">
          <h2
            class="font-display text-base font-bold text-csub-blue-dark uppercase tracking-wide mb-1"
          >
            Need Help?
          </h2>
          <p class="font-body text-sm text-csub-gray leading-relaxed mb-4">
            If something looks wrong or you're stuck on a step, we're here to help. Don't hesitate
            to reach out.
          </p>

          <div class="grid sm:grid-cols-2 gap-3">
            <div class="flex items-start gap-3 p-3 rounded-lg bg-gray-50">
              <svg
                class="w-4 h-4 text-csub-blue flex-shrink-0 mt-0.5"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                />
              </svg>
              <div>
                <p class="font-body text-xs font-semibold text-csub-blue-dark">Admissions Office</p>
                <a
                  href="mailto:admissions@csub.edu"
                  class="font-body text-xs text-csub-blue hover:underline"
                >
                  admissions@csub.edu
                </a>
              </div>
            </div>

            <div class="flex items-start gap-3 p-3 rounded-lg bg-gray-50">
              <svg
                class="w-4 h-4 text-csub-blue flex-shrink-0 mt-0.5"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M3 5a2 2 0 012-2h3.28a1 1 0 01.948.684l1.498 4.493a1 1 0 01-.502 1.21l-2.257 1.13a11.042 11.042 0 005.516 5.516l1.13-2.257a1 1 0 011.21-.502l4.493 1.498a1 1 0 01.684.949V19a2 2 0 01-2 2h-1C9.716 21 3 14.284 3 6V5z"
                />
              </svg>
              <div>
                <p class="font-body text-xs font-semibold text-csub-blue-dark">Phone</p>
                <a href="tel:6616542160" class="font-body text-xs text-csub-blue hover:underline">
                  (661) 654-2160
                </a>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>
