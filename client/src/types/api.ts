/** Shared client-side types matching the API response shapes. */

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
