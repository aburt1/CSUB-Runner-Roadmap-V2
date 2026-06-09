import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Dev server: serve on :3000 and proxy /api to the C# API. The proxy target is
// configurable so you can run the frontend on its own (e.g. on a Windows desktop)
// pointing at a backend running elsewhere:
//   VITE_API_PROXY_TARGET=http://localhost:8080 npm run dev   (or a remote host)
// Defaults to the local `dotnet run` port (3001).
const apiTarget = process.env.VITE_API_PROXY_TARGET || 'http://localhost:3001'

export default defineConfig({
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
})
