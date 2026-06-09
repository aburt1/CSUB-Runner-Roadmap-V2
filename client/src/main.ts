import { createApp } from 'vue'
import { createPinia } from 'pinia'
import './index.css'
import App from './App.vue'
import { router } from './router'
import { useToastStore } from './stores/toast'

const app = createApp(App)
const pinia = createPinia()
app.use(pinia).use(router)

// Surface otherwise-silent failures to the user instead of leaving a blank/broken UI.
app.config.errorHandler = (err) => {
  console.error('[vue error]', err)
  useToastStore(pinia).error('Something went wrong. Please try again.')
}
window.addEventListener('unhandledrejection', (event) => {
  console.error('[unhandled rejection]', event.reason)
  useToastStore(pinia).error('Something went wrong. Please try again.')
})

app.mount('#app')
