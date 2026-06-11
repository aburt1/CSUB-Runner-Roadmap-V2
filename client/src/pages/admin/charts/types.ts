/**
 * Shared analytics chart data contracts.
 *
 * Each chart component gets its own data shape here so AnalyticsTab and the
 * charts themselves can share types without circular imports.  DrillDownPayload
 * was previously declared once per chart (8 copies) plus once more in
 * AnalyticsTab/StudentDrillDown — consolidated here.
 *
 * filterValue is string | number: every producer passes either a step id
 * (number) or a bucket/tag/date string.  The API receives all values as query-
 * string parameters (string) anyway, so this is safe to serialize at the call
 * site.
 */

export interface DrillDownPayload {
  filterType: string
  filterValue: string | number
}

// Step-completion endpoint: { steps: StepCompletionItem[], totalStudents: number }
export interface StepCompletionItem {
  id: number
  title: string
  completed_count: number
}

export interface StepCompletionData {
  steps: StepCompletionItem[]
  totalStudents: number
}

// Completion-trend endpoint: array of { date, completions }
export interface TrendPoint {
  date: string
  completions: number
}

// Bottleneck endpoint: { steps: BottleneckStep[], totalStudents: number }
export interface BottleneckStep {
  id: number
  title: string
  completion_pct: number
  completed_count: number
}

export interface BottleneckData {
  steps: BottleneckStep[]
  totalStudents: number
}

// Cohort-summary endpoint: array of { bucket, student_count }
export interface ProgressBucket {
  bucket: string
  student_count: number
}
