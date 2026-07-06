import { useAuthStore } from '../stores/auth'
import { useToastStore } from '../stores/toast'

const API_BASE = '/api'

interface RequestOptions {
  method?: string
  body?: unknown
  raw?: boolean
  headers?: Record<string, string>
}

export interface StudentApi {
  get: <T = unknown>(path: string) => Promise<T>
  post: <T = unknown>(path: string, body?: unknown) => Promise<T>
  put: <T = unknown>(path: string, body?: unknown) => Promise<T>
  // Returns the raw Response (401 already toasts + logs out). For callers that must
  // branch on the status themselves — e.g. keeping stale data on a 5xx.
  raw: (path: string, options?: Omit<RequestOptions, 'raw'>) => Promise<Response>
}

// Student-side fetch wrapper. One place decides the auth header and the 401 policy:
// an expired/invalid session toasts once and logs the student out (dropping them to the
// public/login view) rather than each call re-inventing that handling. Network-level
// rejections normalize to a single message. Reads the token live from the auth store on
// every call, so it stays correct across login/logout without re-instantiation.
export function useStudentApi(): StudentApi {
  const auth = useAuthStore()
  const toast = useToastStore()

  async function request<T = unknown>(
    path: string,
    options: RequestOptions = {},
  ): Promise<T | Response> {
    const { method = 'GET', body, raw: returnRaw, headers: extraHeaders } = options
    const headers: Record<string, string> = { ...extraHeaders }
    if (auth.token) headers['Authorization'] = `Bearer ${auth.token}`
    if (body !== undefined) headers['Content-Type'] = 'application/json'

    let res: Response
    try {
      res = await fetch(`${API_BASE}${path}`, {
        method,
        headers,
        body: body !== undefined ? JSON.stringify(body) : undefined,
      })
    } catch {
      throw new Error('Unable to connect. Please try again later.')
    }

    if (res.status === 401) {
      // Token expired/invalid — surface it once and drop back to the public/login view.
      // Fire even in raw mode so an expired session always logs out.
      toast.error('Your session expired — please sign in again.')
      auth.logout()
      if (!returnRaw) throw new Error('Session expired')
    }
    if (returnRaw) return res
    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: 'Request failed' }))
      throw new Error(err.error || 'Request failed')
    }
    return res.json() as Promise<T>
  }

  return {
    get: <T = unknown>(path: string) => request<T>(path) as Promise<T>,
    post: <T = unknown>(path: string, body?: unknown) =>
      request<T>(path, { method: 'POST', body }) as Promise<T>,
    put: <T = unknown>(path: string, body?: unknown) =>
      request<T>(path, { method: 'PUT', body }) as Promise<T>,
    raw: (path: string, options: Omit<RequestOptions, 'raw'> = {}) =>
      request(path, { ...options, raw: true }) as Promise<Response>,
  }
}
