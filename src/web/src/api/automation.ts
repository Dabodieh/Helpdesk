import { z } from 'zod'
import { api } from './http'
import { conditionSchema } from './sla'

export const TRIGGERS = [
  'TicketCreated',
  'RequesterMessageAdded',
  'StatusChanged',
  'PriorityChanged',
  'AssigneeChanged',
  'SlaApproaching',
  'SlaBreached',
] as const
export type Trigger = (typeof TRIGGERS)[number]

export const TRIGGER_LABELS: Record<Trigger, string> = {
  TicketCreated: 'A ticket is created',
  RequesterMessageAdded: 'The requester sends a message',
  StatusChanged: 'The status changes',
  PriorityChanged: 'The priority changes',
  AssigneeChanged: 'The assignee changes',
  SlaApproaching: 'An SLA deadline is approaching',
  SlaBreached: 'An SLA deadline is breached',
}

export const ruleActionSchema = z.object({ type: z.string(), value: z.string().nullish(), text: z.string().nullish() })
export type RuleAction = z.infer<typeof ruleActionSchema>

export const ruleSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  isActive: z.boolean(),
  trigger: z.string(),
  order: z.number(),
  stopProcessing: z.boolean(),
  conditions: z.array(conditionSchema),
  actions: z.array(ruleActionSchema),
  updatedAt: z.string(),
  version: z.string(),
})
export type Rule = z.infer<typeof ruleSchema>

export type RuleInput = {
  name?: string
  trigger?: string
  isActive?: boolean
  stopProcessing?: boolean
  conditions?: { field: string; operator: string; values: string[] }[]
  actions?: RuleAction[]
  version?: string
}

const traceSchema = z.object({ field: z.string(), operator: z.string(), values: z.array(z.string()), actual: z.string().nullish(), result: z.boolean() })
const resultSchema = z.object({ index: z.number(), type: z.string(), value: z.string().nullish(), outcome: z.string(), detail: z.string().nullish() })

export const executionSchema = z.object({
  id: z.string(),
  ruleId: z.string(),
  ruleName: z.string(),
  ticketId: z.string(),
  ticketReference: z.string(),
  trigger: z.string(),
  chainId: z.string(),
  depth: z.number(),
  provenance: z.string(),
  state: z.string(),
  matched: z.boolean(),
  conditions: z.array(traceSchema),
  actions: z.array(resultSchema),
  error: z.string().nullish(),
  skipReason: z.string().nullish(),
  startedAt: z.string(),
  finishedAt: z.string().nullish(),
})
export type Execution = z.infer<typeof executionSchema>

export const testResultSchema = z.object({
  ticket: z.string(),
  trigger: z.string(),
  wouldChangeAnything: z.boolean(),
  notice: z.string(),
  rules: z.array(
    z.object({
      ruleId: z.string(),
      name: z.string(),
      trigger: z.string(),
      evaluated: z.boolean(),
      matched: z.boolean(),
      stopsProcessing: z.boolean(),
      conditions: z.array(traceSchema),
      actions: z.array(z.object({ index: z.number(), type: z.string(), value: z.string().nullish(), result: z.string(), detail: z.string().nullish() })),
      note: z.string().nullish(),
    }),
  ),
})
export type TestResult = z.infer<typeof testResultSchema>

const base = (d: string) => `/api/departments/${d}/automations`
export const listRules = (d: string) => api('GET', base(d), { schema: z.array(ruleSchema) })
export const createRule = (d: string, body: RuleInput) => api('POST', base(d), { body, schema: ruleSchema })
export const updateRule = (d: string, id: string, body: RuleInput) => api('PATCH', `${base(d)}/${id}`, { body, schema: ruleSchema })
export const reorderRules = (d: string, trigger: string, ids: string[]) => api('PUT', `${base(d)}/order/${trigger}`, { body: { ids }, schema: z.array(ruleSchema) })
export const testRule = (d: string, body: { ticket: string; trigger?: string; ruleId?: string }) => api('POST', `${base(d)}/test`, { body, schema: testResultSchema })
export const listHistory = (d: string, opts: { ruleId?: string; ticket?: string; state?: string; cursor?: string | null }) => {
  const p = new URLSearchParams({ limit: '50' })
  if (opts.ruleId) p.set('ruleId', opts.ruleId)
  if (opts.ticket) p.set('ticket', opts.ticket)
  if (opts.state) p.set('state', opts.state)
  if (opts.cursor) p.set('cursor', opts.cursor)
  return api('GET', `${base(d)}/history?${p.toString()}`, { schema: z.object({ items: z.array(executionSchema), nextCursor: z.string().nullable() }) })
}
