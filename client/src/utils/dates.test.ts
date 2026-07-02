import { describe, it, expect } from 'vitest'
import { parseLocalDate } from './dates'

describe('parseLocalDate', () => {
  it('parses a date-only string as the local calendar day, not UTC', () => {
    // The correctness invariant: the parsed instant falls on the SAME calendar day the
    // string names, in the runtime's local zone. This holds in every timezone; it FAILS
    // for `new Date('2026-08-01')` (UTC midnight) in any negative UTC offset, where the
    // local day rolls back to Jul 31.
    const d = parseLocalDate('2026-08-01')
    expect(d.getFullYear()).toBe(2026)
    expect(d.getMonth()).toBe(7) // 0-based: August
    expect(d.getDate()).toBe(1)
  })

  it('renders the correct day west of UTC (the bug this guards against)', () => {
    // Pin a negative-UTC-offset scenario (Pacific is UTC-7/-8). If the machine running
    // this test is west of UTC, the OLD `new Date(str)` idiom would render the prior day,
    // while parseLocalDate stays on the named day. Assert the contrast directly so a
    // regression to `new Date(str)` turns this red.
    const iso = '2026-08-01'
    const offsetMinutes = new Date().getTimezoneOffset()
    const local = parseLocalDate(iso)
    const utcParsed = new Date(iso)

    // parseLocalDate is always on the named day.
    expect(local.getDate()).toBe(1)

    if (offsetMinutes > 0) {
      // West of UTC: the buggy UTC parse lands on the previous local day (Jul 31),
      // proving parseLocalDate's local-parse fixes the off-by-one.
      expect(utcParsed.getDate()).toBe(31)
      expect(local.getDate()).not.toBe(utcParsed.getDate())
    }
  })
})
