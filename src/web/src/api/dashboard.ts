import { z } from 'zod'
import { api } from './http'

const mailboxHealthSchema = z.object({
  mailboxId: z.string(),
  address: z.string(),
  displayName: z.string(),
  status: z.string(),
  reasons: z.array(z.string()),
  outboundFailed: z.number(),
  outboundUncertain: z.number(),
  lastSendAt: z.string().nullish(),
})

export const dashboardSchema = z.object({
  departmentId: z.string(),
  departmentName: z.string(),
  generatedAt: z.string(),
  backlog: z.object({ open: z.number(), pending: z.number(), unassigned: z.number(), awaitingFirstResponse: z.number() }),
  sla: z.object({ overdue: z.number(), dueSoon: z.number(), dueSoonWindowMinutes: z.number() }),
  ageing: z.array(z.object({ key: z.string(), label: z.string(), count: z.number() })),
  oldest: z.array(
    z.object({
      reference: z.string(),
      subject: z.string(),
      status: z.string(),
      priority: z.string(),
      createdAt: z.string(),
      assignee: z.string().nullish(),
      slaOverdue: z.boolean(),
    }),
  ),
  /** Current open backlog per assignee (userId null = unassigned). A workload snapshot, never a ranking. */
  agents: z.array(z.object({ userId: z.string().nullish(), name: z.string(), total: z.number(), overdue: z.number() })),
  sends: z.object({ failedTickets: z.number(), uncertainTickets: z.number() }),
  mailboxes: z.array(mailboxHealthSchema).nullish(),
})
export type Dashboard = z.infer<typeof dashboardSchema>

/** Current operational state of one department. Never an aggregate across departments. */
export const getDashboard = (departmentId: string) => api('GET', `/api/departments/${encodeURIComponent(departmentId)}/dashboard`, { schema: dashboardSchema })

/** Received and resolved per Europe/London day, with the same definitions as the department volume report (`daily` adds up to the totals). */
export const volumeSchema = z.object({
  departmentId: z.string(),
  from: z.string(),
  to: z.string(),
  received: z.number(),
  resolved: z.number(),
  daily: z.array(z.object({ date: z.string(), received: z.number(), resolved: z.number() })),
})
export type Volume = z.infer<typeof volumeSchema>

const londonDay = (d: Date) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/London' }).format(d)

/** The last `days` days including today. */
export const getVolume = (departmentId: string, days: number, now = new Date()) => {
  const from = londonDay(new Date(now.getTime() - (days - 1) * 86_400_000))
  const to = londonDay(now)
  return api('GET', `/api/departments/${encodeURIComponent(departmentId)}/reports/volume?from=${from}&to=${to}`, { schema: volumeSchema })
}
