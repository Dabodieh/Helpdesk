import { z } from 'zod'
import { api } from './http'

// ----- shared -----

export const FIELD_TYPES = ['ShortText', 'LongText', 'SingleSelect', 'MultiSelect', 'Checkbox', 'Date', 'Number'] as const
export const FIELD_TYPE_LABEL: Record<string, string> = {
  ShortText: 'Short text',
  LongText: 'Long text',
  SingleSelect: 'Single choice',
  MultiSelect: 'Multiple choice',
  Checkbox: 'Checkbox (yes/no)',
  Date: 'Date',
  Number: 'Number',
}

const optionSchema = z.object({ value: z.string(), label: z.string(), isActive: z.boolean() })
const usageSchema = z.object({ id: z.string(), name: z.string() })

// ----- ticket types (administration) -----

export const typeFieldLinkSchema = z.object({
  fieldId: z.string(),
  key: z.string(),
  label: z.string(),
  type: z.string(),
  required: z.boolean(),
  requiredOverride: z.boolean().nullable(),
  order: z.number(),
  fieldIsActive: z.boolean(),
})
export type TypeFieldLink = z.infer<typeof typeFieldLinkSchema>

export const ticketTypeSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  description: z.string().nullish(),
  isActive: z.boolean(),
  isDefault: z.boolean(),
  order: z.number(),
  fields: z.array(typeFieldLinkSchema),
  version: z.string(),
})
export type TicketType = z.infer<typeof ticketTypeSchema>

/** `required` null/undefined = the field's own default. */
export type FieldLinkInput = { fieldId: string; required?: boolean | null }

const typesBase = (d: string) => `/api/departments/${d}/ticket-types`
export const listTicketTypes = (d: string) => api('GET', typesBase(d), { schema: z.array(ticketTypeSchema) })
export const createTicketType = (d: string, body: { name: string; description?: string; isActive?: boolean; isDefault?: boolean }) =>
  api('POST', typesBase(d), { body, schema: ticketTypeSchema })
export const updateTicketType = (d: string, id: string, body: { name?: string; description?: string; isActive?: boolean; isDefault?: boolean; version: string }) =>
  api('PATCH', `${typesBase(d)}/${id}`, { body, schema: ticketTypeSchema })
export const reorderTicketTypes = (d: string, ids: string[]) => api('PUT', `${typesBase(d)}/order`, { body: { ids }, schema: z.array(ticketTypeSchema) })
export const setTypeFields = (d: string, id: string, fields: FieldLinkInput[]) => api('PUT', `${typesBase(d)}/${id}/fields`, { body: { fields }, schema: ticketTypeSchema })

// ----- ticket fields (administration) -----

export const ticketFieldSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  key: z.string(),
  label: z.string(),
  helpText: z.string().nullish(),
  type: z.string(),
  required: z.boolean(),
  options: z.array(optionSchema),
  order: z.number(),
  isActive: z.boolean(),
  appliesToAllTypes: z.boolean(),
  requesterVisible: z.boolean(),
  requesterEditable: z.boolean(),
  agentVisible: z.boolean(),
  hasValues: z.boolean(),
  types: z.array(usageSchema),
  forms: z.array(usageSchema),
  version: z.string(),
})
export type TicketField = z.infer<typeof ticketFieldSchema>

export type TicketFieldInput = {
  key?: string
  label?: string
  helpText?: string
  type?: string
  required?: boolean
  options?: { value?: string; label: string; isActive?: boolean }[]
  isActive?: boolean
  appliesToAllTypes?: boolean
  requesterVisible?: boolean
  requesterEditable?: boolean
  agentVisible?: boolean
  version?: string
}

const fieldsBase = (d: string) => `/api/departments/${d}/ticket-fields`
export const listTicketFields = (d: string) => api('GET', fieldsBase(d), { schema: z.array(ticketFieldSchema) })
export const createTicketField = (d: string, body: TicketFieldInput) => api('POST', fieldsBase(d), { body, schema: ticketFieldSchema })
export const updateTicketField = (d: string, id: string, body: TicketFieldInput) => api('PATCH', `${fieldsBase(d)}/${id}`, { body, schema: ticketFieldSchema })
export const reorderTicketFields = (d: string, ids: string[]) => api('PUT', `${fieldsBase(d)}/order`, { body: { ids }, schema: z.array(ticketFieldSchema) })

// ----- request forms (administration) -----

export const formFieldLinkSchema = z.object({
  fieldId: z.string(),
  key: z.string(),
  label: z.string(),
  type: z.string(),
  required: z.boolean(),
  requiredOverride: z.boolean().nullable(),
  order: z.number(),
  fieldIsActive: z.boolean(),
  requesterVisible: z.boolean(),
})
export type FormFieldLink = z.infer<typeof formFieldLinkSchema>

export const requestFormSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  description: z.string().nullish(),
  isActive: z.boolean(),
  visibleToRequesters: z.boolean(),
  order: z.number(),
  defaultTeamId: z.string().nullish(),
  defaultPriority: z.string().nullish(),
  ticketTypeId: z.string().nullish(),
  fields: z.array(formFieldLinkSchema),
  version: z.string(),
  audience: z.enum(['AllEmployees', 'Selected']),
  audienceUsers: z.array(z.object({ userId: z.string(), displayName: z.string(), email: z.string().nullish() })),
})
export type RequestForm = z.infer<typeof requestFormSchema>

export type FormInput = {
  name?: string
  description?: string
  isActive?: boolean
  visibleToRequesters?: boolean
  defaultTeamId?: string | null
  clearDefaultTeam?: boolean
  defaultPriority?: string | null
  clearDefaultPriority?: boolean
  ticketTypeId?: string | null
  clearTicketType?: boolean
  version?: string
  /** Who may discover and submit the form. Department membership is separate and unaffected. */
  audience?: 'AllEmployees' | 'Selected'
  audienceUserIds?: string[]
}

const formsBase = (d: string) => `/api/departments/${d}/request-forms`
export const listForms = (d: string) => api('GET', formsBase(d), { schema: z.array(requestFormSchema) })
export const createForm = (d: string, body: FormInput) => api('POST', formsBase(d), { body, schema: requestFormSchema })
export const updateForm = (d: string, id: string, body: FormInput) => api('PATCH', `${formsBase(d)}/${id}`, { body, schema: requestFormSchema })
export const reorderForms = (d: string, ids: string[]) => api('PUT', `${formsBase(d)}/order`, { body: { ids }, schema: z.array(requestFormSchema) })
export const setFormFields = (d: string, id: string, fields: FieldLinkInput[]) => api('PUT', `${formsBase(d)}/${id}/fields`, { body: { fields }, schema: requestFormSchema })

// ----- ticket properties (agent workspace) -----

export const propertySchema = z.object({
  fieldId: z.string().nullable(),
  key: z.string(),
  label: z.string(),
  helpText: z.string().nullish(),
  type: z.string(),
  required: z.boolean(),
  options: z.array(optionSchema),
  value: z.unknown().nullish(),
  display: z.string().nullish(),
  applicable: z.boolean(),
  editable: z.boolean(),
  active: z.boolean(),
  missingRequired: z.boolean(),
})
export type TicketProperty = z.infer<typeof propertySchema>

export const ticketPropertiesSchema = z.object({
  type: z.object({ id: z.string(), displayName: z.string() }).nullable(),
  types: z.array(z.object({ id: z.string(), name: z.string(), isActive: z.boolean(), isDefault: z.boolean() })),
  fields: z.array(propertySchema),
  version: z.string(),
})
export type TicketProperties = z.infer<typeof ticketPropertiesSchema>

const tk = (ticket: string) => `/api/tickets/${encodeURIComponent(ticket)}/properties`
export const getTicketProperties = (ticket: string) => api('GET', tk(ticket), { schema: ticketPropertiesSchema })
export const updateTicketProperties = (ticket: string, body: { version: string; ticketTypeId?: string; clearTicketType?: boolean; fields?: Record<string, unknown> }) =>
  api('PATCH', tk(ticket), { body, schema: ticketPropertiesSchema })
