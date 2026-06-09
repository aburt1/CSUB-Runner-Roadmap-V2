import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'
import pluginVue from 'eslint-plugin-vue'
import skipFormatting from '@vue/eslint-config-prettier/skip-formatting'

// Standard Vue 3 + TypeScript flat config. `skipFormatting` turns off rules that
// would fight Prettier, so ESLint owns correctness and Prettier owns layout.
export default defineConfigWithVueTs(
  {
    name: 'app/files-to-lint',
    files: ['**/*.{ts,mts,tsx,vue}'],
  },
  {
    name: 'app/files-to-ignore',
    ignores: ['**/dist/**', '**/node_modules/**', '**/coverage/**'],
  },
  pluginVue.configs['flat/essential'],
  vueTsConfigs.recommended,
  skipFormatting,
  {
    name: 'app/rule-tweaks',
    rules: {
      // Analytics drill-downs and Chart.js payloads are intentionally dynamically
      // shaped (ported as-is from the original app). Track `any` as debt to tighten
      // later, but don't block the build on it — vue-tsc is the real type gate.
      '@typescript-eslint/no-explicit-any': 'warn',
      // Single-word component names (Celebration) are unambiguous here and won't
      // collide with HTML elements; the convention isn't worth renaming for.
      'vue/multi-word-component-names': 'off',
    },
  },
  {
    // Build-tool configs are Node modules, not app code.
    name: 'app/tooling-configs',
    files: ['*.config.{js,cjs,ts}'],
    rules: {
      '@typescript-eslint/no-require-imports': 'off',
    },
  },
)
