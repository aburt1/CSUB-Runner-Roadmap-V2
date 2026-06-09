import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ToastType = 'error' | 'success' | 'info'

export interface Toast {
  id: number
  message: string
  type: ToastType
}

// Lightweight transient-notification store. Used by the global error handlers and by
// any component that needs to surface a success/failure to the user.
export const useToastStore = defineStore('toast', () => {
  const toasts = ref<Toast[]>([])
  let nextId = 1

  function show(message: string, type: ToastType = 'info', timeoutMs = 5000): number {
    const id = nextId++
    toasts.value.push({ id, message, type })
    if (timeoutMs > 0) setTimeout(() => dismiss(id), timeoutMs)
    return id
  }

  function dismiss(id: number): void {
    toasts.value = toasts.value.filter((t) => t.id !== id)
  }

  return {
    toasts,
    show,
    dismiss,
    error: (message: string) => show(message, 'error', 8000),
    success: (message: string) => show(message, 'success'),
    info: (message: string) => show(message, 'info'),
  }
})
