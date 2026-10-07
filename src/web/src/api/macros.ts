import { z } from 'zod'
import { api } from './http'
import { ticketDetailSchema } from './ticketSchemas'

export const MACRO_ACTION_TYPES = [
  'setStatus',
  'setPriority',
  'setTeam',
  'setAssignee',
  'clearAssignee',
  'assignToMe',
  'setType',
  'setField',
  'internalNote',
  'publicReply',
] as const
export type MacroActionType = (typeof MACRO_ACTION_TYPES)[number]

export const MACRO_ACTION_LABEL: Record<MacroActionType, string> = {
  setStatus: 'Status',
  setPriority: 'Priority',
  setTeam: 'Team',
  setAssignee: 'Assignee',
  clearAssignee: 'Clear assignee',
  assignToMe: 'Assign to me',
  setType: 'Ticket type',
  setField: 'Ticket field',
  internalNote: 'Internal note',
  publicReply: 'Public reply (draft)',
}

export const macroActionSchema = z.object({
  type: z.string().nullish(),
  value: z.string().nullish(),
  text: z.string().nullish(),
  key: z.string().nullish(),
  data: z.unknown().nullish(),
})
export type MacroAction = z.infer<typeof macroActionSchema>

export const macroSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  description: z.string().nullish(),
  isActive: z.boolean(),
  order: z.number(),
  actions: z.array(macroActionSchema),
  summary: z.array(z.string()),
  version: z.string(),
})
export type Macro = z.infer<typeof macroSchema>

export type MacroInput = { name?: string; description?: string; isActive?: boolean; actions?: MacroAction[]; version?: string }

const base = (d: string) => `/api/departments/${encodeURIComponent(d)}/macros`
export const listMacros = (d: string) => api('GET', base(d), { schema: z.array(macroSchema) })
export const createMacro = (d: string, body: MacroInput) => api('POST', base(d), { body, schema: macroSchema })
export const updateMacro = (d: string, id: string, body: MacroInput) => api('PATCH', `${base(d)}/${encodeURIComponent(id)}`, { body, schema: macroSchema })
export const reorderMacros = (d: string, ids: string[]) => api('PUT', `${base(d)}/order`, { body: { ids }, schema: z.array(macroSchema) })

export const macroPickSchema = z.object({ id: z.string(), name: z.string(), description: z.string().nullish(), summary: z.array(z.string()) })
export type MacroPick = z.infer<typeof macroPickSchema>

const tk = (ticket: string) => `/api/tickets/${encodeURIComponent(ticket)}`

/** Active macros of the ticket department that the signed-in user may run (the server filters). */
export const pickMacros = (ticket: string, q: string) => api('GET', `${tk(ticket)}/macros?q=${encodeURIComponent(q)}`, { schema: z.array(macroPickSchema) })

export const macroRunSchema = z.object({
  ticket: ticketDetailSchema,
  draft: z.object({ body: z.string(), cannedResponseId: z.string().nullish(), title: z.string().nullish() }).nullish(),
  noteAdded: z.boolean(),
})
export type MacroRun = z.infer<typeof macroRunSchema>

export const runMacro = (ticket: string, macroId: string, version: string) =>
  api('POST', `${tk(ticket)}/macros/${encodeURIComponent(macroId)}/run`, { body: { version }, schema: macroRunSchema })
