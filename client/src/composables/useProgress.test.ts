import { describe, it, expect } from 'vitest'
import { stepApplies, deriveAllStepStatuses, type ProgressMapEntry } from './useProgress'
import type { Step } from '../types/api'

// Minimal Step factory — only the fields the tag/status logic reads matter; the
// rest get harmless defaults so the fixtures stay readable.
function makeStep(partial: Partial<Step>): Step {
  return {
    id: 1,
    title: '',
    description: '',
    icon: '',
    sort_order: 0,
    is_public: 1,
    is_optional: 0,
    deadline: null,
    deadline_date: null,
    links: null,
    guide_content: null,
    contact_info: null,
    required_tags: null,
    excluded_tags: null,
    required_tag_mode: null,
    link_url: null,
    link_label: null,
    category: null,
    api_check_type: null,
    ...partial,
  }
}

describe('stepApplies', () => {
  it('applies to everyone when there are no tag rules', () => {
    expect(stepApplies(makeStep({}), [])).toBe(true)
    expect(stepApplies(makeStep({}), ['transfer'])).toBe(true)
  })

  it('excludes a student who has an excluded tag', () => {
    const step = makeStep({ excluded_tags: ['international'] })
    expect(stepApplies(step, ['international'])).toBe(false)
    expect(stepApplies(step, ['transfer'])).toBe(true)
  })

  it('required_tag_mode "any" needs at least one match', () => {
    const step = makeStep({ required_tags: ['transfer', 'veteran'], required_tag_mode: 'any' })
    expect(stepApplies(step, ['veteran'])).toBe(true)
    expect(stepApplies(step, ['freshman'])).toBe(false)
  })

  it('required_tag_mode "all" needs every tag', () => {
    const step = makeStep({ required_tags: ['transfer', 'veteran'], required_tag_mode: 'all' })
    expect(stepApplies(step, ['transfer', 'veteran'])).toBe(true)
    expect(stepApplies(step, ['transfer'])).toBe(false)
  })

  it('defaults to "any" when mode is null', () => {
    const step = makeStep({ required_tags: ['transfer', 'veteran'], required_tag_mode: null })
    expect(stepApplies(step, ['veteran'])).toBe(true)
  })

  it('parses tags stored as a JSON string', () => {
    const step = makeStep({ required_tags: '["transfer"]' })
    expect(stepApplies(step, ['transfer'])).toBe(true)
    expect(stepApplies(step, ['freshman'])).toBe(false)
  })

  it('exclusion wins over an otherwise-matching required tag', () => {
    const step = makeStep({ required_tags: ['transfer'], excluded_tags: ['hold'] })
    expect(stepApplies(step, ['transfer', 'hold'])).toBe(false)
  })
})

describe('deriveAllStepStatuses', () => {
  it('marks the first incomplete required step in_progress and the rest not_started', () => {
    const steps = [makeStep({ id: 1 }), makeStep({ id: 2 }), makeStep({ id: 3 })]
    const result = deriveAllStepStatuses(steps, new Map())
    expect(result.map((s) => s.status)).toEqual(['in_progress', 'not_started', 'not_started'])
  })

  it('uses saved progress and advances the current pointer past completed steps', () => {
    const steps = [makeStep({ id: 1 }), makeStep({ id: 2 }), makeStep({ id: 3 })]
    const progress = new Map<number, ProgressMapEntry>([
      [1, { status: 'completed', completed_at: '2026-01-01' }],
    ])
    const result = deriveAllStepStatuses(steps, progress)
    expect(result.map((s) => s.status)).toEqual(['completed', 'in_progress', 'not_started'])
  })

  it('never auto-advances optional steps', () => {
    const steps = [makeStep({ id: 1, is_optional: 1 }), makeStep({ id: 2 })]
    const result = deriveAllStepStatuses(steps, new Map())
    expect(result.map((s) => s.status)).toEqual(['not_started', 'in_progress'])
  })
})
