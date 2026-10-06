import { z } from 'zod'
import { api } from './http'

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
export const formFieldSchema = z.object({
  id: z.string(),
  key: z.string(),
  label: z.string(),
  helpText: z.string().nullish(),
  type: z.string(),
  required: z.boolean(),
  options: z.array(optionSchema),
  order: z.number(),
  isActive: z.boolean(),
  requesterVisible: z.boolean(),
  agentVisible: z.boolean(),
  hasValues: z.boolean(),
  version: z.string(),
})
export type FormField = z.infer<typeof formFieldSchema>

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
  fields: z.array(formFieldSchema),
  version: z.string(),
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
  version?: string
}

export type FieldInput = {
  key?: string
  label?: string
  helpText?: string
  type?: string
  required?: boolean
  options?: { value?: string; label: string; isActive?: boolean }[]
  isActive?: boolean
  requesterVisible?: boolean
  agentVisible?: boolean
  version?: string
}

const base = (d: string) => `/api/departments/${d}/request-forms`
export const listForms = (d: string) => api('GET', base(d), { schema: z.array(requestFormSchema) })
export const createForm = (d: string, body: FormInput) => api('POST', base(d), { body, schema: requestFormSchema })
export const updateForm = (d: string, id: string, body: FormInput) => api('PATCH', `${base(d)}/${id}`, { body, schema: requestFormSchema })
export const reorderForms = (d: string, ids: string[]) => api('PUT', `${base(d)}/order`, { body: { ids }, schema: z.array(requestFormSchema) })
export const createField = (d: string, formId: string, body: FieldInput) => api('POST', `${base(d)}/${formId}/fields`, { body, schema: requestFormSchema })
export const updateField = (d: string, formId: string, fieldId: string, body: FieldInput) =>
  api('PATCH', `${base(d)}/${formId}/fields/${fieldId}`, { body, schema: requestFormSchema })
export const reorderFields = (d: string, formId: string, ids: string[]) => api('PUT', `${base(d)}/${formId}/fields/order`, { body: { ids }, schema: requestFormSchema })
