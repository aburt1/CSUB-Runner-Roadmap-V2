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
})
