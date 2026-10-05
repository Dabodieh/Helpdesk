import { z } from 'zod'
import { api } from './http'

export const cannedSchema = z.object({
  id: z.string(),
  scope: z.enum(['Personal', 'Department']),
  departmentId: z.string().nullable(),
  departmentName: z.string().nullable(),
  title: z.string(),
  body: z.string(),
  isActive: z.boolean(),
  canManage: z.boolean(),
  updatedAt: z.string(),
  version: z.string(),
})
export type Canned = z.infer<typeof cannedSchema>

export const listCanned = (opts: { departmentId?: string; includeInactive?: boolean } = {}) => {
  const p = new URLSearchParams()
  if (opts.departmentId) p.set('departmentId', opts.departmentId)
  if (opts.includeInactive) p.set('includeInactive', 'true')
  const qs = p.toString()
  return api('GET', `/api/canned-responses${qs ? `?${qs}` : ''}`, { schema: z.array(cannedSchema) })
}

export const createCanned = (body: { scope: 'Personal' | 'Department'; departmentId?: string; title: string; body: string }) =>
  api('POST', '/api/canned-responses', { body, schema: cannedSchema })

export const updateCanned = (id: string, body: { version: string; title?: string; body?: string; isActive?: boolean }) =>
  api('PATCH', `/api/canned-responses/${encodeURIComponent(id)}`, { body, schema: cannedSchema })
