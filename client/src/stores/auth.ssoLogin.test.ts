import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'

// vi.mock factories are hoisted above all other code, so anything they close over must
// be created with vi.hoisted (also hoisted) rather than as ordinary top-level consts.
const { FakeBrowserAuthError, msalMock } = vi.hoisted(() => {
  // A stand-in for @azure/msal-browser's BrowserAuthError: carries an errorCode and
  // supports `instanceof` (the ssoLogin fallback narrows on both).
  class FakeBrowserAuthError extends Error {
    errorCode: string
    constructor(errorCode: string, message = errorCode) {
      super(message)
      this.errorCode = errorCode
    }
  }
  // A configurable mock MSAL instance. Each test sets loginPopup/loginRedirect behavior.
  const msalMock = {
    initialize: vi.fn(),
    handleRedirectPromise: vi.fn(),
    loginPopup: vi.fn(),
    loginRedirect: vi.fn(),
    clearCache: vi.fn(),
  }
  return { FakeBrowserAuthError, msalMock }
})

vi.mock('../auth/msalConfig', () => ({
  msalInstance: msalMock,
  isAzureAdConfigured: true,
  loginRequest: { scopes: ['openid'] },
}))
vi.mock('@azure/msal-browser', () => ({
  BrowserAuthError: FakeBrowserAuthError,
}))

import { useAuthStore } from './auth'

// init() flips the store's private msalReady flag; ssoLogin early-returns without it.
async function readyStore() {
  msalMock.handleRedirectPromise.mockResolvedValueOnce(null)
  const auth = useAuthStore()
  await auth.init()
  return auth
}

describe('auth store ssoLogin fallbacks', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    sessionStorage.clear()
    msalMock.initialize.mockResolvedValue(undefined)
    msalMock.handleRedirectPromise.mockResolvedValue(null)
    msalMock.loginPopup.mockReset()
    msalMock.loginRedirect.mockReset()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.clearAllMocks()
  })

  // Each popup errorCode that must degrade to a full-page redirect.
  for (const code of ['popup_window_error', 'empty_window_error', 'popup_timeout']) {
    it(`falls back to loginRedirect when the popup fails with ${code}`, async () => {
      const auth = await readyStore()
      msalMock.loginPopup.mockRejectedValueOnce(new FakeBrowserAuthError(code))
      msalMock.loginRedirect.mockResolvedValueOnce(undefined)

      await auth.ssoLogin()

      expect(msalMock.loginRedirect).toHaveBeenCalledOnce()
      // A pending redirect keeps the spinner up (it clears on navigation) and sets no error.
      expect(auth.ssoError).toBe('')
      expect(auth.ssoLoading).toBe(true)
    })
  }

  it('does NOT fall back to redirect for a non-popup auth error', async () => {
    const auth = await readyStore()
    // interaction_in_progress is a BrowserAuthError but not in the popup-fallback set.
    msalMock.loginPopup.mockRejectedValueOnce(new FakeBrowserAuthError('interaction_in_progress'))

    await auth.ssoLogin()

    expect(msalMock.loginRedirect).not.toHaveBeenCalled()
    // The error surfaces and the spinner is cleared (no pending redirect).
    expect(auth.ssoError).toBe('interaction_in_progress')
    expect(auth.ssoLoading).toBe(false)
  })

  it('clears the spinner if the redirect fallback itself throws', async () => {
    const auth = await readyStore()
    msalMock.loginPopup.mockRejectedValueOnce(new FakeBrowserAuthError('popup_window_error'))
    // The redirect never navigates away — the flag must reset so the spinner clears.
    msalMock.loginRedirect.mockRejectedValueOnce(new Error('redirect blew up'))

    await auth.ssoLogin()

    expect(msalMock.loginRedirect).toHaveBeenCalledOnce()
    expect(auth.ssoLoading).toBe(false)
    expect(auth.ssoError).toBe('redirect blew up')
  })

  it('treats a user_cancelled popup as a silent no-op (no error shown)', async () => {
    const auth = await readyStore()
    // user_cancelled carries an errorCode but is not a BrowserAuthError in the fallback set;
    // the outer catch maps it to an empty error string.
    const cancelled = Object.assign(new Error('cancelled'), { errorCode: 'user_cancelled' })
    msalMock.loginPopup.mockRejectedValueOnce(cancelled)

    await auth.ssoLogin()

    expect(msalMock.loginRedirect).not.toHaveBeenCalled()
    expect(auth.ssoError).toBe('')
    expect(auth.ssoLoading).toBe(false)
  })

  it('completes the login when the popup returns an idToken', async () => {
    const auth = await readyStore()
    msalMock.loginPopup.mockResolvedValueOnce({ idToken: 'popup-id-token' })
    // handleSsoResponse posts the idToken to /api/auth/sso.
    const fetchFn = vi.fn(
      async () =>
        new Response(
          JSON.stringify({ token: 'jwt-xyz', student: { id: 3, email: 's@csub.edu' } }),
          {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          },
        ),
    )
    vi.stubGlobal('fetch', fetchFn)

    await auth.ssoLogin()

    expect(auth.token).toBe('jwt-xyz')
    expect(sessionStorage.getItem('csub_token')).toBe('jwt-xyz')
    expect(auth.ssoLoading).toBe(false)
    const [url] = fetchFn.mock.calls[0]
    expect(url).toBe('/api/auth/sso')
  })
})
