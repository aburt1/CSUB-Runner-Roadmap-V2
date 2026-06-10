import { describe, it, expect } from 'vitest'
import { safeUrl } from './links'

describe('safeUrl', () => {
  it('allows http/https/mailto/tel', () => {
    expect(safeUrl('https://csub.edu/apply')).toBe('https://csub.edu/apply')
    expect(safeUrl('http://example.com')).toBe('http://example.com')
    expect(safeUrl('mailto:admissions@csub.edu')).toBe('mailto:admissions@csub.edu')
    expect(safeUrl('tel:+16616543036')).toBe('tel:+16616543036')
  })

  it('allows scheme-less relative links', () => {
    expect(safeUrl('/portal')).toBe('/portal')
    expect(safeUrl('#section')).toBe('#section')
  })

  it('neutralizes javascript:, data:, and other schemes', () => {
    expect(safeUrl('javascript:alert(1)')).toBe('#')
    expect(safeUrl('JaVaScRiPt:alert(1)')).toBe('#')
    expect(safeUrl(' javascript:alert(1)')).toBe('#')
    expect(safeUrl('data:text/html,<script>alert(1)</script>')).toBe('#')
    expect(safeUrl('vbscript:msgbox(1)')).toBe('#')
  })

  it('handles null/undefined/empty', () => {
    expect(safeUrl(null)).toBe('#')
    expect(safeUrl(undefined)).toBe('#')
    expect(safeUrl('')).toBe('#')
  })
})
