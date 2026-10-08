import type { ReportParams } from '../api/reports'

export type ReportCategory = 'Ticketing' | 'SLA' | 'Productivity' | 'Communication'

export type ReportDef = { key: string; title: string; category: ReportCategory; description: string; icon: string; keywords: string }

/** The reports the server provides (docs/architecture/REPORTING_DEFINITIONS.md). Nothing is listed here that the API cannot answer. */
export const REPORTS: ReportDef[] = [
  { key: 'support-overview', title: 'Support overview', category: 'Ticketing', icon: 'chart', description: 'Received, resolved and unresolved tickets, the backlog trend, SLA compliance and response times in one view.', keywords: 'summary dashboard trends kpi' },
  { key: 'ticket-volume', title: 'Ticket volume trends', category: 'Ticketing', icon: 'chart', description: 'Tickets created and resolved over time, by day, week or month, with the breakdown by priority and status.', keywords: 'created resolved received priority status' },
  { key: 'lifecycle', title: 'Ticket lifecycle', category: 'Ticketing', icon: 'views', description: 'How tickets move: created, pending, resolved, closed and reopened, with every status transition counted.', keywords: 'reopen transitions status history' },
  { key: 'backlog', title: 'Backlog and ageing', category: 'Ticketing', icon: 'all', description: 'Unresolved tickets over time, how old they are, the longest outstanding and what is unassigned.', keywords: 'unresolved age oldest unassigned open pending' },
  { key: 'sla', title: 'SLA performance', category: 'SLA', icon: 'clock', description: 'First response and resolution compliance, breaches over time and the split by priority.', keywords: 'breach compliance target overdue' },
  { key: 'response-times', title: 'Response and resolution times', category: 'SLA', icon: 'clock', description: 'Average, median and 90th percentile first response and resolution times, with trends and priority breakdown.', keywords: 'first response median average speed' },
  { key: 'workload', title: 'Agent workload', category: 'Productivity', icon: 'person', description: 'Where open and pending work sits across the department, and how much is unassigned. A picture of load, not a league table.', keywords: 'assignee agents load distribution' },
  { key: 'email', title: 'Email delivery', category: 'Communication', icon: 'mail', description: 'Outgoing reply delivery: accepted, failed, uncertain, bounced and delayed, with the recorded reasons.', keywords: 'mail outbound failed bounced delayed' },
]

export const CATEGORIES: ReportCategory[] = ['Ticketing', 'SLA', 'Productivity', 'Communication']

export const reportByKey = (key: string) => REPORTS.find((r) => r.key === key)

// ---------- dates (Europe/London days, as the server buckets them) ----------

const londonDay = (d: Date) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/London', year: 'numeric', month: '2-digit', day: '2-digit' }).format(d)

export const today = () => londonDay(new Date())

const addDays = (iso: string, n: number) => {
  const [y, m, d] = iso.split('-').map(Number)
  const t = new Date(Date.UTC(y!, m! - 1, d! + n))
  return t.toISOString().slice(0, 10)
}

export const PRESETS = [
  { id: 'today', label: 'Today' },
  { id: '7', label: 'Last 7 days' },
  { id: '30', label: 'Last 30 days' },
  { id: 'month', label: 'This month' },
  { id: 'lastmonth', label: 'Last month' },
  { id: 'custom', label: 'Custom' },
] as const
export type PresetId = (typeof PRESETS)[number]['id']

export function presetRange(id: Exclude<PresetId, 'custom'>, now = today()): { from: string; to: string } {
  const [y, m] = now.split('-').map(Number)
  switch (id) {
    case 'today':
      return { from: now, to: now }
    case '7':
      return { from: addDays(now, -6), to: now }
    case '30':
      return { from: addDays(now, -29), to: now }
    case 'month':
      return { from: `${y}-${String(m).padStart(2, '0')}-01`, to: now }
    default: {
      const last = new Date(Date.UTC(y!, m! - 1, 0))
      const lm = String(last.getUTCMonth() + 1).padStart(2, '0')
      return { from: `${last.getUTCFullYear()}-${lm}-01`, to: `${last.getUTCFullYear()}-${lm}-${String(last.getUTCDate()).padStart(2, '0')}` }
    }
  }
}

export const daysBetween = (from: string, to: string) => Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000) + 1

/** The grouping the server would pick, so the picker can show it and avoid the daily-bucket limit. */
export function defaultGroup(from: string, to: string) {
  const days = daysBetween(from, to)
  return days <= 31 ? 'day' : days <= 120 ? 'week' : 'month'
}

export const DEFAULT_PARAMS = (): ReportParams => {
  const r = presetRange('30')
  return { ...r, group: defaultGroup(r.from, r.to), priority: [], status: [], assignee: '', unassigned: false }
}

// ---------- formatting ----------

export function formatValue(value: number | null | undefined, unit: string): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—'
  switch (unit) {
    case 'percent':
      return `${Number.isInteger(value) ? value : value.toFixed(1)}%`
    case 'minutes':
      if (value < 1) return '<1 min'
      if (value < 60) return `${Math.round(value)} min`
      if (value < 1440) return `${(value / 60).toFixed(1).replace(/\.0$/, '')} h`
      return `${(value / 1440).toFixed(1).replace(/\.0$/, '')} d`
    case 'hours':
      return `${value.toFixed(1).replace(/\.0$/, '')} h`
    default:
      return Number.isInteger(value) ? value.toLocaleString('en-GB') : value.toLocaleString('en-GB', { maximumFractionDigits: 1 })
  }
}

export function formatBucket(iso: string, group: string): string {
  const d = new Date(`${iso}T12:00:00Z`)
  if (group === 'month') return d.toLocaleDateString('en-GB', { month: 'short', year: '2-digit', timeZone: 'UTC' })
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })
}

export function formatCell(value: unknown, type: string): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'number') return formatValue(value, type === 'number' ? 'count' : type)
  if (type === 'date' && typeof value === 'string') return /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T12:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }) : new Date(value).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
  return String(value)
}

// ---------- CSV ----------

const csvCell = (v: unknown) => {
  const s = v === null || v === undefined ? '' : String(v)
  // Spreadsheet formula injection: a cell that starts like a formula is quoted as text.
  const safe = /^[=+\-@\t\r]/.test(s) && Number.isNaN(Number(s)) ? `'${s}` : s
  return /[",\n\r]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe
}

export const toCsv = (rows: unknown[][]) => rows.map((r) => r.map(csvCell).join(',')).join('\r\n')

export function downloadCsv(filename: string, rows: unknown[][]) {
  const blob = new Blob(['﻿' + toCsv(rows)], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}

// ---------- favourites, recents and saved settings (this browser only) ----------

const KEY = 'helpdesk.analytics.v1'
export type SavedSetting = { id: string; report: string; name: string; params: ReportParams }
type Store = { favourites: string[]; recent: string[]; saved: SavedSetting[] }

export function readStore(): Store {
  try {
    const raw = JSON.parse(window.localStorage.getItem(KEY) ?? '{}') as Partial<Store>
    return { favourites: raw.favourites ?? [], recent: raw.recent ?? [], saved: raw.saved ?? [] }
  } catch {
    return { favourites: [], recent: [], saved: [] }
  }
}

export function writeStore(update: (s: Store) => Store): Store {
  const next = update(readStore())
  try {
    window.localStorage.setItem(KEY, JSON.stringify(next))
  } catch {
    /* not remembered in a private window */
  }
  return next
}
