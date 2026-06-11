import { describe, it, expect } from 'vitest'
import { parseMaybeJson } from './json'

describe('parseMaybeJson', () => {
  it('parses JSON strings', () => {
    expect(parseMaybeJson('["a","b"]', [])).toEqual(['a', 'b'])
    expect(parseMaybeJson('{"name":"x"}', null)).toEqual({ name: 'x' })
  })

  it('passes through already-parsed values', () => {
    expect(parseMaybeJson(['a'], [])).toEqual(['a'])
    expect(parseMaybeJson({ name: 'x' }, null)).toEqual({ name: 'x' })
  })

  it('returns the fallback for null/undefined/empty', () => {
    expect(parseMaybeJson(null, [])).toEqual([])
    expect(parseMaybeJson(undefined, null)).toBeNull()
    expect(parseMaybeJson('', [])).toEqual([])
  })

  it('returns the fallback for malformed JSON instead of throwing', () => {
    expect(parseMaybeJson('not json', [])).toEqual([])
    expect(parseMaybeJson('{broken', null)).toBeNull()
  })

  it('returns the fallback for JSON null — mirrors server Json.SafeParse', () => {
    // JSON.parse('null') === null; without the ?? fallback this would return null
    // typed as T (e.g. array), causing a mid-render crash on .length access.
    expect(parseMaybeJson('null', [])).toEqual([])
    expect(parseMaybeJson('null', {})).toEqual({})
  })
})
