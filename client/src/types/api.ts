/** Shared client-side types matching the API response shapes. */

// The exact payload StepForm builds and sends to PUT /steps/:id or POST /steps.
// Naming the shape here lets vue-tsc verify the parent handler matches the child emit.
export interface StepSavePayload {
  title: string
  icon: string | null
  description: string | null
  deadline: string | null
  deadline_date: string | null
  guide_content: string | null
  links: null
  required_tags: string[] | null
  required_tag_mode: 'any' | 'all'
  excluded_tags: string[] | null
  sort_order?: number
  contact_info: { name: string | null; email: string | null; phone: string | null } | null
  term_id: number | null
  is_public: 0 | 1
  is_optional: 0 | 1
}

export interface LinkItem {
  url: string
  label: string
}

export interface ContactInfo {
  name: string
  email?: string
  phone?: string
}

export interface Step {
  id: number
  title: string
  description: string
  icon: string
  sort_order: number
  is_public: number
  is_optional: number
  deadline: string | null
  deadline_date: string | null
  links: string | LinkItem[] | null
  guide_content: string | null
  contact_info: string | ContactInfo | null
  actionLabel?: string
  required_tags: string | string[] | null
  excluded_tags: string | string[] | null
  required_tag_mode: 'any' | 'all' | null
  link_url: string | null
  link_label: string | null
  category: string | null
  api_check_type: string | null
}

export type StepStatus =
  | 'completed'
  | 'waived'
  | 'in_progress'
  | 'not_started'
  | 'locked'
  | 'preview'

export interface StepWithStatus extends Step {
  status: StepStatus
  lockedReason?: string
}

export interface ProgressEntry {
  step_id: number
  completed_at: string | null
  status: string | null
}

export interface Term {
  id: number
  name: string
  start_date: string
  end_date: string
}

export interface ProgressResponse {
  progress: ProgressEntry[]
  tags: string[]
  term: Term | null
}

// A step whose status flipped during an API-check run. Mirrors the API's
// ApiCheckRunner.CheckedStep — each entry is an object, not a bare step id.
export interface CheckedStep {
  stepId: number
  newStatus: string
}

// GET /api/roadmap/check-status — poll response while background checks run.
export interface CheckStatusResponse {
  status: string // no_run | running | complete
  checkedSteps?: CheckedStep[]
}

// Audit log entry as returned by GET /audit
export interface AuditLog {
  id: number
  entity_type: string
  action: string
  changed_by: string
  created_at: string
  details: string | Record<string, unknown>
}
