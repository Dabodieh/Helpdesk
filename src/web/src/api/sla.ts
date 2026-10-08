import { z } from 'zod'
import { api } from './http'

export const SLA_STATES = ['Running', 'Approaching', 'Breached', 'Paused', 'Met', 'MetLate', 'NotApplicable', 'None'] as const

export const slaSummarySchema = z.object({
  state: z.string(),
  dueAt: z.string().nullish(),
  clock: z.string().nullish(),
  firstResponse: z.string(),
  resolution: z.string(),
})
export type SlaSummary = z.infer<typeof slaSummarySchema>

const slaClockSchema = z.object({
  targetMinutes: z.number().nullable(),
  dueAt: z.string().nullable(),
  metAt: z.string().nullable(),
  pausedAt: z.string().nullable(),
  state: z.string(),
  cycle: z.number().nullable(),
})

export const ticketSlaSchema = z.object({
  policyName: z.string(),
  calendar: z.string(),
  timeZone: z.string(),
  pauseWhenPending: z.boolean(),
  appliedAt: z.string(),
  firstResponse: slaClockSchema,
  resolution: slaClockSchema,
  events: z.array(z.object({ at: z.string(), kind: z.string(), clock: z.string().nullable(), detail: z.record(z.string(), z.unknown()) })),
})
export type TicketSla = z.infer<typeof ticketSlaSchema>

/** Null when no policy matched the ticket (the server answers 204). */
export const getTicketSla = async (ticket: string): Promise<TicketSla | null> =>
  (await api('GET', `/api/tickets/${encodeURIComponent(ticket)}/sla`, { schema: ticketSlaSchema.nullable() })) ?? null

// ----- configuration (department.sla.manage) -----

export const intervalSchema = z.object({ start: z.string(), end: z.string() })
export type Interval = z.infer<typeof intervalSchema>
export const weeklyDaySchema = z.object({ day: z.string(), intervals: z.array(intervalSchema) })
export const exceptionSchema = z.object({ date: z.string(), name: z.string().nullish(), intervals: z.array(intervalSchema) })
export type CalendarException = z.infer<typeof exceptionSchema>

export const businessHoursSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  timeZone: z.string(),
  isActive: z.boolean(),
  weekly: z.array(weeklyDaySchema),
  exceptions: z.array(exceptionSchema),
  updatedAt: z.string(),
  version: z.string(),
})
export type BusinessHours = z.infer<typeof businessHoursSchema>

export type BusinessHoursInput = {
  name?: string
  timeZone?: string
  isActive?: boolean
  weekly?: { day: string; intervals: Interval[] }[]
  exceptions?: CalendarException[]
  version?: string
}

const bh = (d: string) => `/api/departments/${d}/business-hours`
export const listBusinessHours = (d: string) => api('GET', bh(d), { schema: z.array(businessHoursSchema) })
export const createBusinessHours = (d: string, body: BusinessHoursInput) => api('POST', bh(d), { body, schema: businessHoursSchema })
export const updateBusinessHours = (d: string, id: string, body: BusinessHoursInput) => api('PATCH', `${bh(d)}/${id}`, { body, schema: businessHoursSchema })
export const checkDue = (d: string, id: string, body: { start: string; minutes: number }) =>
  api('POST', `${bh(d)}/${id}/due`, { body, schema: z.object({ start: z.string(), minutes: z.number(), dueAt: z.string().nullable(), timeZone: z.string() }) })

export const conditionSchema = z.object({ field: z.string(), operator: z.string(), values: z.array(z.string()) })
export type Condition = z.infer<typeof conditionSchema>

export const slaPolicySchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  isActive: z.boolean(),
  order: z.number(),
  conditions: z.array(conditionSchema),
  calendarId: z.string().nullable(),
  calendarName: z.string().nullable(),
  firstResponseMinutes: z.number().nullable(),
  resolutionMinutes: z.number().nullable(),
  pauseWhenPending: z.boolean(),
  approachingLeadMinutes: z.number(),
  updatedAt: z.string(),
  version: z.string(),
})
export type SlaPolicy = z.infer<typeof slaPolicySchema>

export type SlaPolicyInput = {
  name?: string
  isActive?: boolean
  conditions?: Condition[]
  calendarId?: string | null
  clearCalendar?: boolean
  firstResponseMinutes?: number | null
  resolutionMinutes?: number | null
  clearFirstResponse?: boolean
  clearResolution?: boolean
  pauseWhenPending?: boolean
  approachingLeadMinutes?: number
  version?: string
}

const sp = (d: string) => `/api/departments/${d}/sla-policies`
export const listSlaPolicies = (d: string) => api('GET', sp(d), { schema: z.array(slaPolicySchema) })
export const createSlaPolicy = (d: string, body: SlaPolicyInput) => api('POST', sp(d), { body, schema: slaPolicySchema })
export const updateSlaPolicy = (d: string, id: string, body: SlaPolicyInput) => api('PATCH', `${sp(d)}/${id}`, { body, schema: slaPolicySchema })
export const reorderSlaPolicies = (d: string, ids: string[]) => api('PUT', `${sp(d)}/order`, { body: { ids }, schema: z.array(slaPolicySchema) })
