import { z } from 'zod'
import { api, csrfHeaders, ApiError } from './http'

/** Requester portal API (docs/api/PHASE6_API.md). Purpose-built shapes: nothing here mirrors the agent ticket model. */

const capabilitiesSchema = z.object({ attachmentsEnabled: z.boolean() })
export const getPortalCapabilities = () => api('GET', '/api/portal/capabilities', { schema: capabilitiesSchema })

export const REQUEST_FILTERS = ['active', 'closed', 'all'] as const
export type RequestFilter = (typeof REQUEST_FILTERS)[number]

export const portalRequestItemSchema = z.object({
  reference: z.string(),
  subject: z.string(),
  status: z.string(),
  statusLabel: z.string(),
  department: z.string(),
  createdAt: z.string(),
  lastActivityAt: z.string(),
})
export type PortalRequestItem = z.infer<typeof portalRequestItemSchema>

const page = <T extends z.ZodType>(item: T) => z.object({ items: z.array(item), nextCursor: z.string().nullable() })

export const portalAttachmentSchema = z.object({ id: z.string(), fileName: z.string(), size: z.number(), downloadable: z.boolean() })
export const portalMessageSchema = z.object({
  id: z.string(),
  author: z.string(),
  fromYou: z.boolean(),
  body: z.string(),
  at: z.string(),
  attachments: z.array(portalAttachmentSchema),
})
export type PortalMessage = z.infer<typeof portalMessageSchema>

export const portalRequestSchema = z.object({
  reference: z.string(),
  subject: z.string(),
  status: z.string(),
  statusLabel: z.string(),
  department: z.string(),
  form: z.string().nullish(),
  createdAt: z.string(),
  lastActivityAt: z.string(),
  fields: z.array(z.object({ label: z.string(), value: z.string() })),
  messages: z.array(portalMessageSchema),
  /** Set when this request was merged into another request of the same requester. */
  mergedInto: z.string().nullish(),
  /** False when the request is shown to a requester who was kept in it by a merge: they can read it but replies stay with its own requester. */
  canReply: z.boolean().default(true),
})
export type PortalRequest = z.infer<typeof portalRequestSchema>

export const portalFormListItemSchema = z.object({ id: z.string(), name: z.string(), description: z.string().nullish(), department: z.string() })
export type PortalFormListItem = z.infer<typeof portalFormListItemSchema>

export const portalFieldSchema = z.object({
  key: z.string(),
  label: z.string(),
  helpText: z.string().nullish(),
  type: z.string(),
  required: z.boolean(),
  options: z.array(z.object({ value: z.string(), label: z.string() })),
})
export type PortalField = z.infer<typeof portalFieldSchema>

export const portalFormSchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string().nullish(),
  department: z.string(),
  fields: z.array(portalFieldSchema),
})
export type PortalForm = z.infer<typeof portalFormSchema>

export const portalHelpTopicSchema = z.object({ id: z.string(), name: z.string(), description: z.string().nullish(), department: z.string(), formId: z.string() })
export type PortalHelpTopic = z.infer<typeof portalHelpTopicSchema>

/** Topics are offered only while their request form is usable by the caller; the server decides, the form still validates every submission. */
export const listPortalHelpTopics = () => api('GET', '/api/portal/help-topics', { schema: z.array(portalHelpTopicSchema) })
export const getPortalHelpTopic = (id: string) => api('GET', `/api/portal/help-topics/${encodeURIComponent(id)}`, { schema: portalHelpTopicSchema })

export const listPortalForms = () => api('GET', '/api/portal/forms', { schema: z.array(portalFormListItemSchema) })
export const getPortalForm = (id: string) => api('GET', `/api/portal/forms/${encodeURIComponent(id)}`, { schema: portalFormSchema })

export const listPortalRequests = (filter: RequestFilter, cursor: string | null, limit = 20) => {
  const p = new URLSearchParams({ filter, limit: String(limit) })
  if (cursor) p.set('cursor', cursor)
  return api('GET', `/api/portal/requests?${p.toString()}`, { schema: page(portalRequestItemSchema) })
}

export const getPortalRequest = (reference: string) => api('GET', `/api/portal/requests/${encodeURIComponent(reference)}`, { schema: portalRequestSchema })

export type NewPortalRequest = { formId: string; subject: string; description: string; fields: Record<string, unknown>; submissionId: string }
export const createPortalRequest = (body: NewPortalRequest) =>
  api('POST', '/api/portal/requests', { body, schema: z.object({ reference: z.string(), status: z.string(), statusLabel: z.string() }) })

const replyResultSchema = z.object({ message: portalMessageSchema, status: z.string(), statusLabel: z.string(), attachmentsNotSaved: z.number().default(0) })

/** Reply with optional files. Files go as multipart through the same secure attachment pipeline as email attachments. */
export async function replyToRequest(reference: string, body: string, files: File[], clientMessageId?: string) {
  if (files.length === 0) return api('POST', `/api/portal/requests/${encodeURIComponent(reference)}/replies`, { body: { body, clientMessageId }, schema: replyResultSchema })
  const form = new FormData()
  form.set('body', body)
  if (clientMessageId) form.set('clientMessageId', clientMessageId)
  for (const f of files) form.append('files', f)
  const res = await fetch(`/api/portal/requests/${encodeURIComponent(reference)}/replies`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { Accept: 'application/json', ...(await csrfHeaders()) },
    body: form,
  })
  if (!res.ok) {
    let detail: { title?: string; detail?: string; errors?: Record<string, string[]>; code?: string } = {}
    try {
      detail = await res.json()
    } catch {
      // not JSON
    }
    throw new ApiError(res.status, detail.title ?? 'Request failed', detail.detail, detail.errors, detail.code)
  }
  return replyResultSchema.parse(await res.json())
}

export const portalAttachmentHref = (reference: string, id: string) => `/api/portal/requests/${encodeURIComponent(reference)}/attachments/${encodeURIComponent(id)}`

// ----- knowledge -----

export const articleCardSchema = z.object({ slug: z.string(), title: z.string(), summary: z.string(), department: z.string(), category: z.string().nullish() })
export type ArticleCard = z.infer<typeof articleCardSchema>

export const knowledgeCategorySchema = z.object({ id: z.string(), name: z.string(), description: z.string().nullish(), department: z.string(), articleCount: z.number() })
export type KnowledgeCategory = z.infer<typeof knowledgeCategorySchema>

export const portalArticleSchema = z.object({
  slug: z.string(),
  title: z.string(),
  summary: z.string(),
  body: z.string(),
  department: z.string(),
  category: z.string().nullish(),
  publishedAt: z.string(),
  updatedAt: z.string(),
  myFeedback: z.boolean().nullish(),
})
export type PortalArticle = z.infer<typeof portalArticleSchema>

export const listKnowledgeCategories = () => api('GET', '/api/portal/knowledge/categories', { schema: z.array(knowledgeCategorySchema) })

export const searchKnowledge = (opts: { q?: string; categoryId?: string; cursor?: string | null; limit?: number }) => {
  const p = new URLSearchParams({ limit: String(opts.limit ?? 10) })
  if (opts.q) p.set('q', opts.q)
  if (opts.categoryId) p.set('categoryId', opts.categoryId)
  if (opts.cursor) p.set('cursor', opts.cursor)
  return api('GET', `/api/portal/knowledge/articles?${p.toString()}`, { schema: page(articleCardSchema) })
}

export const getKnowledgeArticle = (slug: string) => api('GET', `/api/portal/knowledge/articles/${encodeURIComponent(slug)}`, { schema: portalArticleSchema })
export const sendArticleFeedback = (slug: string, helpful: boolean) =>
  api('PUT', `/api/portal/knowledge/articles/${encodeURIComponent(slug)}/feedback`, { body: { helpful }, schema: portalArticleSchema })
