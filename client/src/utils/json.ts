// Several API fields arrive either as a JSON string (raw text from the DB) or as an
// already-parsed value, depending on the endpoint. Normalize with a fallback so one
// malformed row degrades to the fallback instead of throwing mid-render.
export function parseMaybeJson<T>(value: unknown, fallback: T): T {
  if (value === null || value === undefined || value === '') return fallback
  if (typeof value !== 'string') return value as T
  try {
    // Nullish coalesce: JSON.parse('null') === null, which should return the
    // fallback (not null typed as T) — same behaviour as the server's Json.SafeParse.
    // Using ?? preserves legitimate false/0 parsed values.
    return (JSON.parse(value) as T) ?? fallback
  } catch {
    return fallback
  }
}
