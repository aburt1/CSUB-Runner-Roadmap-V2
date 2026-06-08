import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Same dev setup as the old React client: serve on :3000 and proxy /api to the
// C# API on :3001.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: 'http://localhost:3001',
        changeOrigin: true,
      },
    },
  },
})
