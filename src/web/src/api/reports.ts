import { z } from 'zod'
import { api } from './http'

const kpiSchema = z.object({
  key: z.string(),
  label: z.string(),
  value: z.number().nullish(),
  unit: z.string(),
  previous: z.number().nullish(),
  drill: z.string().nullish(),
  note: z.string().nullish(),
  tone: z.string().default('neutral'),
})

const lineSchema = z.object({
  key: z.string(),
  label: z.string(),
  values: z.array(z.number().nullable()),
  drill: z.string().nullish(),
  tone: z.string().default('neutral'),
})

const seriesSchema = z.object({
  key: z.string(),
  title: z.string(),
  kind: z.string(),
  unit: z.string(),
  bucketType: z.string(),
  buckets: z.array(z.string()),
  lines: z.array(lineSchema),
  subtitle: z.string().nullish(),
})

const tableSchema = z.object({
  key: z.string(),
  title: z.string(),
  columns: z.array(z.object({ key: z.string(), label: z.string(), type: z.string() })),
  rows: z.array(z.array(z.unknown())),
  subtitle: z.string().nullish(),
})

export const reportSchema = z.object({
  report: z.string(),
  period: z.object({ from: z.string(), to: z.string(), group: z.string(), timeZone: z.string(), generatedAt: z.string() }),
  kpis: z.array(kpiSchema),
  series: z.array(seriesSchema),
  tables: z.array(tableSchema),
  notes: z.array(z.string()),
  unavailable: z.array(z.string()),
})
export type Report = z.infer<typeof reportSchema>
export type ReportKpi = z.infer<typeof kpiSchema>
export type ReportSeries = z.infer<typeof seriesSchema>
export type ReportLine = z.infer<typeof lineSchema>
export type ReportTable = z.infer<typeof tableSchema>

export const reportTicketsSchema = z.object({
  metric: z.string(),
  from: z.string(),
  to: z.string(),
  total: z.number(),
  truncated: z.boolean(),
  items: z.array(
    z.object({
      id: z.string(),
      reference: z.string(),
      subject: z.string(),
      status: z.string(),
      priority: z.string(),
      assignee: z.string().nullish(),
      createdAt: z.string(),
      resolvedAt: z.string().nullish(),
      ageDays: z.number(),
    }),
  ),
})
export type ReportTickets = z.infer<typeof reportTicketsSchema>

export type ReportParams = {
  from: string
  to: string
  group: string
  priority: string[]
  status: string[]
  assignee: string
  unassigned: boolean
}

export function reportQuery(p: ReportParams, extra: Record<string, string> = {}) {
  const q = new URLSearchParams({ from: p.from, to: p.to, group: p.group, ...extra })
  for (const v of p.priority) q.append('priority', v)
  for (const v of p.status) q.append('status', v)
  if (p.unassigned) q.set('unassigned', 'true')
  else if (p.assignee) q.set('assignee', p.assignee)
  return q.toString()
}

const base = (dept: string) => `/api/departments/${encodeURIComponent(dept)}/reports`

export const fetchReport = (dept: string, kind: string, p: ReportParams) => api('GET', `${base(dept)}/${encodeURIComponent(kind)}?${reportQuery(p)}`, { schema: reportSchema })

export const fetchReportTickets = (dept: string, metric: string, p: ReportParams, bucket?: string) =>
  api('GET', `${base(dept)}/tickets?${reportQuery(p, { metric, ...(bucket ? { bucket } : {}) })}`, { schema: reportTicketsSchema })
