// Parses a date-only string ("YYYY-MM-DD") as LOCAL midnight. `new Date('2026-08-01')`
// is parsed as UTC midnight, which renders as the previous calendar day in any negative
// UTC offset (e.g. Pacific), so date-only values shown to the user land a day early.
// Appending 'T00:00:00' forces local-time parsing — the same idiom DeadlineCountdown uses.
export function parseLocalDate(date: string): Date {
  return new Date(date + 'T00:00:00')
}
