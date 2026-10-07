import { z } from 'zod'
import { api } from './http'

export const helpTopicSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  description: z.string().nullish(),
  isActive: z.boolean(),
  order: z.number(),
  formId: z.string(),
  formName: z.string(),
  /** Is the form active and offered? Who may use it is the form's audience, decided per requester. */
  formAvailable: z.boolean(),
  version: z.string(),
})
export type HelpTopic = z.infer<typeof helpTopicSchema>

export type HelpTopicInput = { name?: string; description?: string; isActive?: boolean; formId?: string; version?: string }

const base = (d: string) => `/api/departments/${d}/help-topics`
export const listHelpTopics = (d: string) => api('GET', base(d), { schema: z.array(helpTopicSchema) })
export const createHelpTopic = (d: string, body: HelpTopicInput) => api('POST', base(d), { body, schema: helpTopicSchema })
export const updateHelpTopic = (d: string, id: string, body: HelpTopicInput) => api('PATCH', `${base(d)}/${id}`, { body, schema: helpTopicSchema })
export const reorderHelpTopics = (d: string, ids: string[]) => api('PUT', `${base(d)}/order`, { body: { ids }, schema: z.array(helpTopicSchema) })
