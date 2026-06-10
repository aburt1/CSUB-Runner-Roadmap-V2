const API_BASE = '/api/admin'

interface RequestOptions {
  method?: string
  body?: unknown
  raw?: boolean
  headers?: Record<string, string>
}

export interface AdminApi {
  get: <T = unknown>(
    path: string,
    params?: Record<string, string | number | boolean | null | undefined>,
  ) => Promise<T>
  post: <T = unknown>(path: string, body?: unknown) => Promise<T>
  put: <T = unknown>(path: string, body?: unknown) => Promise<T>
  del: <T = unknown>(path: string, body?: unknown) => Promise<T>
  raw: (path: string, options?: Omit<RequestOptions, 'raw'>) => Promise<Response>
}

// Admin fetch wrapper, ported from the old useAdminApi hook. Pass the current
// admin token; onAuthError fires on a 401 (e.g. to send the user back to login).
// Create a fresh instance when the token changes.
export function useAdminApi(token: string | null, onAuthError?: () => void): AdminApi {
  async function request<T = unknown>(
    path: string,
    options: RequestOptions = {},
  ): Promise<T | Response> {
    const { method = 'GET', body, raw: returnRaw, headers: extraHeaders } = options
    const headers: Record<string, string> = { Authorization: `Bearer ${token}`, ...extraHeaders }
    if (body !== undefined) headers['Content-Type'] = 'application/json'

    const res = await fetch(`${API_BASE}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })

    if (res.status === 401) {
      // Fire the auth handler even in raw mode (CSV export etc.) — an expired
      // session should always send the admin back to login.
      onAuthError?.()
      if (!returnRaw) throw new Error('Session expired')
    }
    if (returnRaw) return res
    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: 'Request failed' }))
      throw new Error(err.error || 'Request failed')
    }
    return res.json() as Promise<T>
  }

  function get<T = unknown>(
    path: string,
    params?: Record<string, string | number | boolean | null | undefined>,
  ): Promise<T> {
    if (params) {
      const qs = new URLSearchParams(
        Object.entries(params)
          .filter((entry): entry is [string, string | number | boolean] => entry[1] != null)
          .map(([k, v]) => [k, String(v)]),
      ).toString()
      if (qs) return request<T>(`${path}?${qs}`) as Promise<T>
    }
    return request<T>(path) as Promise<T>
  }

  return {
    get,
    post: <T = unknown>(path: string, body?: unknown) =>
      request<T>(path, { method: 'POST', body }) as Promise<T>,
    put: <T = unknown>(path: string, body?: unknown) =>
      request<T>(path, { method: 'PUT', body }) as Promise<T>,
    del: <T = unknown>(path: string, body?: unknown) =>
      request<T>(path, { method: 'DELETE', body }) as Promise<T>,
    raw: (path: string, options: Omit<RequestOptions, 'raw'> = {}) =>
      request(path, { ...options, raw: true }) as Promise<Response>,
  }
}
