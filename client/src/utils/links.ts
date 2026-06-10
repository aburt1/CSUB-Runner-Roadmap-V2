// Admin-authored URLs (step links, primary actions) are rendered into student-facing
// anchors. Only safe schemes may reach an href: a stored "javascript:..." URL would
// otherwise execute in the student's session when clicked.
export function safeUrl(url: string | null | undefined): string {
  const value = (url ?? '').trim()
  if (!value) return '#'
  if (/^(https?:|mailto:|tel:)/i.test(value)) return value
  // No scheme at all (relative path, "#anchor") is fine.
  if (!/^[a-z][a-z0-9+.-]*:/i.test(value)) return value
  return '#'
}
