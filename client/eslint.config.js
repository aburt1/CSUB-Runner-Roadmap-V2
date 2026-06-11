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
      // All `any` uses have been replaced with typed interfaces or `unknown`.
      // Promoting to 'error' so new `any` doesn't slip in unnoticed.
      '@typescript-eslint/no-explicit-any': 'error',
      // Single-word component names (Celebration) are unambiguous here and won't
      // collide with HTML elements; the convention isn't worth renaming for.
      'vue/multi-word-component-names': 'off',
    },
  },
)
