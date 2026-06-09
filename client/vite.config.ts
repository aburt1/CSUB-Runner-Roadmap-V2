import { defineConfig, type UserConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Dev server: serve on :3000 and proxy /api to the C# API. The proxy target is
// configurable so you can run the frontend on its own (e.g. on a Windows desktop)
// pointing at a backend running elsewhere:
//   VITE_API_PROXY_TARGET=http://localhost:8080 npm run dev   (or a remote host)
// Defaults to the local `dotnet run` port (3001).
const apiTarget = process.env.VITE_API_PROXY_TARGET || 'http://localhost:3001'

// Vitest reads the `test` field from this file. We keep `defineConfig` from Vite
// (so the vue() plugin types line up with this project's Vite version) and type
// the object as a variable with an optional `test` key, since Vite's own config
// type doesn't declare `test`.
const config: UserConfig & { test?: Record<string, unknown> } = {
  plugins: [vue()],
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
      },
    },
  },
  // Unit tests run under jsdom so stores/composables that touch sessionStorage,
  // fetch, and timers behave like the browser.
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
  },
}

export default defineConfig(config)
