// All client-thrown request errors are Error instances — either from useAdminApi
// (which always throws new Error(msg)) or from fetch itself (TypeError). This
// helper narrows the catch (err: unknown) pattern at every call site so we don't
// need 14 identical (err: any) casts scattered across the admin components.
export function errorMessage(err: unknown, fallback = 'Request failed'): string {
  return err instanceof Error && err.message ? err.message : fallback
}
