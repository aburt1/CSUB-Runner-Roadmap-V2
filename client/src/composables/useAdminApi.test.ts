import { describe, it, expect, afterEach, vi } from 'vitest'
import { mockFetch } from '../test/helpers'
import { useAdminApi } from './useAdminApi'

describe('useAdminApi', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('sends the bearer token and parses JSON on success', async () => {
    const fetchFn = mockFetch(200, { ok: true })
    const api = useAdminApi('tok123')
    const result = await api.get('/students')
    expect(result).toEqual({ ok: true })
    const [url, init] = fetchFn.mock.calls[0]
    expect(url).toBe('/api/admin/students')
    expect((init as RequestInit).headers).toMatchObject({ Authorization: 'Bearer tok123' })
  })

  it('builds a query string from get params and skips null/undefined', async () => {
    const fetchFn = mockFetch(200, [])
    const api = useAdminApi('t')
    await api.get('/audit', { studentId: 5, limit: 20, missing: null, gone: undefined })
    const [url] = fetchFn.mock.calls[0]
    expect(url).toBe('/api/admin/audit?studentId=5&limit=20')
  })

  it('sets Content-Type and serializes the body on post', async () => {
    const fetchFn = mockFetch(200, { success: true })
    const api = useAdminApi('t')
    await api.post('/students/1/tags', { tags: ['transfer'] })
    const [, init] = fetchFn.mock.calls[0] as [string, RequestInit]
    expect(init.method).toBe('POST')
    expect(init.body).toBe(JSON.stringify({ tags: ['transfer'] }))
    expect(init.headers).toMatchObject({ 'Content-Type': 'application/json' })
  })

  it('calls onAuthError and throws on a 401', async () => {
    mockFetch(401, { error: 'unauthorized' })
    const onAuthError = vi.fn()
    const api = useAdminApi('expired', onAuthError)
    await expect(api.get('/students')).rejects.toThrow('Session expired')
    expect(onAuthError).toHaveBeenCalledOnce()
  })

  it('throws the server error message on a non-ok response', async () => {
    mockFetch(400, { error: 'bad request' })
    const api = useAdminApi('t')
    await expect(api.post('/terms', {})).rejects.toThrow('bad request')
  })

  it('raw returns the Response without throwing on non-ok', async () => {
    mockFetch(500, { error: 'boom' })
    const api = useAdminApi('t')
    const res = await api.raw('/export')
    expect(res.status).toBe(500)
  })
})
