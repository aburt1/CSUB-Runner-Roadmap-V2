import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import StepDetailPanel from './StepDetailPanel.vue'
import type { StepWithStatus } from '../../types/api'

// StepDetailPanel renders admin-authored guide_content into the app's only v-html
// sink (sanitizedGuideContent = DOMPurify.sanitize(...)). This is the path the
// DOMPurify advisory touches, so a regression that bound raw guide_content would be
// an XSS hole. These tests assert the sink stays sanitized: dangerous markup is
// stripped, safe markup survives, plain text passes through, and null renders empty.

function makeStep(overrides: Partial<StepWithStatus> = {}): StepWithStatus {
  return {
    id: 1,
    title: 'Test step',
    description: 'desc',
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
    status: 'not_started',
    ...overrides,
  }
}

function mountPanel(step: StepWithStatus) {
  return mount(StepDetailPanel, {
    props: {
      step,
      stepNumber: 1,
      totalSteps: 1,
      hasPrev: false,
      hasNext: false,
    },
  })
}

describe('StepDetailPanel guide_content sanitization', () => {
  it('strips dangerous markup but keeps safe elements', () => {
    const wrapper = mountPanel(
      makeStep({
        guide_content: '<img src=x onerror=alert(1)><script>alert(2)</script><p>ok</p>',
      }),
    )
    const html = wrapper.html()
    // Safe content survives.
    expect(html).toContain('<p>ok</p>')
    // The XSS vectors are gone.
    expect(html).not.toContain('onerror')
    expect(html).not.toContain('<script')
  })

  it('passes plain text through (non-HTML branch)', () => {
    const wrapper = mountPanel(makeStep({ guide_content: 'just plain text' }))
    expect(wrapper.text()).toContain('just plain text')
  })

  it('renders no guide section when guide_content is null', () => {
    const wrapper = mountPanel(makeStep({ guide_content: null }))
    expect(wrapper.text()).not.toContain('How to Complete This Step')
  })
})
