/**
 * Shared chart theme constants for consistent styling across all analytics charts.
 *
 * Colors follow the CSUB brand palette:
 *   - csub-blue (#003594) for primary data
 *   - csub-blue-dark (#001A70) for secondary/axis text
 *   - csub-gold (#FFC72C) for highlights and completed states
 *
 * Semantic colors for status indicators:
 *   - Red (#DC2626) for danger/risk/zero progress
 *   - Amber (#F59E0B) for warnings/partial progress
 *   - Green (#10B981) for success/fast completion
 */

import type { CSSProperties } from 'vue'

// ── Brand colors ──────────────────────────────────────────
export const CSUB_BLUE: string = '#003594'
export const CSUB_BLUE_DARK: string = '#001A70'
export const CSUB_GOLD: string = '#FFC72C'
export const CSUB_GRAY: string = '#707372'

// ── Semantic colors ───────────────────────────────────────
export const COLOR_DANGER: string = '#DC2626'
export const COLOR_WARNING: string = '#F59E0B'
export const COLOR_SUCCESS: string = '#10B981'

// ── Axis & grid ───────────────────────────────────────────
export const AXIS_COLOR: string = '#6B7280'
export const AXIS_FONT_SIZE: number = 11
export const GRID_COLOR: string = '#E5E7EB'

// ── Bar styling ───────────────────────────────────────────
export const BAR_RADIUS: [number, number, number, number] = [4, 4, 0, 0]
export const BAR_RADIUS_HORIZONTAL: [number, number, number, number] = [0, 4, 4, 0]

// ── Tooltip styling ───────────────────────────────────────
export const TOOLTIP_STYLE: CSSProperties = {
  backgroundColor: '#fff',
  border: `1px solid ${GRID_COLOR}`,
  borderRadius: '8px',
  fontSize: '12px',
}

export const TOOLTIP_CURSOR: { fill: string } = { fill: 'rgba(0, 53, 148, 0.08)' }

// ── Progress bucket colors (0% → 100%) ───────────────────
// Tells a clear story: red (none) → amber (starting) → blue (progressing) → dark blue (strong) → gold (complete)
export const PROGRESS_COLORS: string[] = [
  COLOR_DANGER,
  COLOR_WARNING,
  CSUB_BLUE,
  CSUB_BLUE_DARK,
  CSUB_GOLD,
]

// ── Velocity bucket colors (fast → slow) ─────────────────
// Green (fast) → blue → amber → red (slow) → gray (very slow)
export const VELOCITY_COLORS: string[] = [
  COLOR_SUCCESS,
  CSUB_BLUE,
  COLOR_WARNING,
  COLOR_DANGER,
  CSUB_GRAY,
]

// ── Bottleneck / step completion color helper ─────────────
export function getCompletionColor(pct: number): string {
  if (pct <= 25) return COLOR_DANGER
  if (pct <= 50) return COLOR_WARNING
  return CSUB_BLUE
}
