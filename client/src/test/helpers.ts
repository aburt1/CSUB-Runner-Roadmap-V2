import { vi } from 'vitest'

// Stub global fetch with a canned JSON response; returns the spy so tests can
// assert on the URL/init it was called with.
export function mockFetch(status: number, body: unknown) {
  const fn = vi.fn(
    async () =>
      new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
      }),
  )
  vi.stubGlobal('fetch', fn)
  return fn
}
