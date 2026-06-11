// Shared helper for rendering user avatar initials. Used in StudentsTab,
// AdminUsersTab, and StudentDetail — extracted from three near-identical copies.
export function getInitials(name: string | undefined | null): string {
  if (!name) return '?'
  return name
    .split(' ')
    .map((n) => n[0])
    .join('')
    .slice(0, 2)
    .toUpperCase()
}
