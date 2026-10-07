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
      team: z.string().nullish(),
      assignee: z.string().nullish(),
      slaOverdue: z.boolean(),
    }),
  ),
  teams: z.array(z.object({ teamId: z.string().nullish(), name: z.string(), total: z.number(), unassigned: z.number(), overdue: z.number() })),
  sends: z.object({ failedTickets: z.number(), uncertainTickets: z.number() }),
  mailboxes: z.array(mailboxHealthSchema).nullish(),
})
export type Dashboard = z.infer<typeof dashboardSchema>

/** Current operational state of one department. Never an aggregate across departments. */
export const getDashboard = (departmentId: string) => api('GET', `/api/departments/${encodeURIComponent(departmentId)}/dashboard`, { schema: dashboardSchema })
