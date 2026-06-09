import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useToastStore } from './toast'

describe('toast store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('adds a toast and returns its id', () => {
    const toast = useToastStore()
    const id = toast.show('hello', 'info', 0)
    expect(toast.toasts).toHaveLength(1)
    expect(toast.toasts[0]).toMatchObject({ id, message: 'hello', type: 'info' })
  })

  it('dismiss removes only the matching toast', () => {
    const toast = useToastStore()
    const a = toast.show('a', 'info', 0)
    toast.show('b', 'info', 0)
    toast.dismiss(a)
    expect(toast.toasts.map((t) => t.message)).toEqual(['b'])
  })

  it('auto-dismisses after the timeout', () => {
    vi.useFakeTimers()
    try {
      const toast = useToastStore()
      toast.show('temp', 'info', 5000)
      expect(toast.toasts).toHaveLength(1)
      vi.advanceTimersByTime(5000)
      expect(toast.toasts).toHaveLength(0)
    } finally {
      vi.useRealTimers()
    }
  })

  it('a zero timeout never auto-dismisses', () => {
    vi.useFakeTimers()
    try {
      const toast = useToastStore()
      toast.show('sticky', 'error', 0)
      vi.advanceTimersByTime(60_000)
      expect(toast.toasts).toHaveLength(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it('error/success/info helpers set the right type', () => {
    const toast = useToastStore()
    toast.error('e')
    toast.success('s')
    toast.info('i')
    expect(toast.toasts.map((t) => t.type)).toEqual(['error', 'success', 'info'])
  })
})
