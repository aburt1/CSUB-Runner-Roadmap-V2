import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mockFetch } from '../test/helpers'
import { setActivePinia, createPinia } from 'pinia'

// Azure AD is "not configured" in tests, so init() skips MSAL entirely and the
// store exercises only the token-validation path. Mock the config + library so
// no real MSAL code runs.
vi.mock('../auth/msalConfig', () => ({
  msalInstance: null,
  isAzureAdConfigured: false,
  loginRequest: {},
}))
vi.mock('@azure/msal-browser', () => ({
  BrowserAuthError: class BrowserAuthError extends Error {},
}))

import { useAuthStore } from './auth'

describe('auth store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    sessionStorage.clear()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('devLogin stores the token and user', async () => {
    mockFetch(200, { token: 'jwt-abc', student: { id: 1, email: 'a@b.edu' } })
    const auth = useAuthStore()
    await auth.devLogin('Test', 'a@b.edu')
    expect(auth.token).toBe('jwt-abc')
    expect(auth.isAuthenticated).toBe(true)
    expect(sessionStorage.getItem('csub_token')).toBe('jwt-abc')
  })

  it('devLogin throws and stores nothing on failure', async () => {
    mockFetch(401, { error: 'nope' })
    const auth = useAuthStore()
    await expect(auth.devLogin('x', 'y@z.edu')).rejects.toThrow('Login failed')
    expect(auth.token).toBeNull()
    expect(sessionStorage.getItem('csub_token')).toBeNull()
  })

  it('logout clears the token and storage', async () => {
    mockFetch(200, { token: 'jwt-abc', student: { id: 1, email: 'a@b.edu' } })
    const auth = useAuthStore()
    await auth.devLogin('Test', 'a@b.edu')
    auth.logout()
    expect(auth.token).toBeNull()
    expect(auth.user).toBeNull()
    expect(sessionStorage.getItem('csub_token')).toBeNull()
  })

  it('init validates an existing token and loads the user', async () => {
    sessionStorage.setItem('csub_token', 'stored-token')
    mockFetch(200, { id: 7, email: 'me@csub.edu' })
    const auth = useAuthStore()
    await auth.init()
    expect(auth.user).toMatchObject({ id: 7, email: 'me@csub.edu' })
    expect(auth.loading).toBe(false)
  })

  it('init drops an invalid token', async () => {
    sessionStorage.setItem('csub_token', 'bad-token')
    mockFetch(401, { error: 'expired' })
    const auth = useAuthStore()
    await auth.init()
    expect(auth.token).toBeNull()
    expect(sessionStorage.getItem('csub_token')).toBeNull()
  })

  it('init keeps the token when the server is unreachable', async () => {
    sessionStorage.setItem('csub_token', 'keep-me')
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new Error('network down')
      }),
    )
    const auth = useAuthStore()
    await auth.init()
    expect(auth.token).toBe('keep-me')
    expect(auth.loading).toBe(false)
  })
})
