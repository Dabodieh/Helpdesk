import { z } from 'zod'
import { api } from './http'

export const categoryAdminSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  name: z.string(),
  description: z.string().nullish(),
  order: z.number(),
  isActive: z.boolean(),
  articleCount: z.number(),
  version: z.string(),
})
export type CategoryAdmin = z.infer<typeof categoryAdminSchema>

export const articleListItemSchema = z.object({
  id: z.string(),
  slug: z.string(),
  title: z.string(),
  status: z.string(),
  visibility: z.string(),
  categoryId: z.string().nullish(),
  category: z.string().nullish(),
  hasUnpublishedChanges: z.boolean(),
  updatedAt: z.string(),
  publishedAt: z.string().nullish(),
})
export type ArticleListItem = z.infer<typeof articleListItemSchema>

const slotSchema = z.object({ title: z.string(), summary: z.string(), body: z.string() })
export const articleAdminSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  slug: z.string(),
  status: z.string(),
  visibility: z.string(),
  categoryId: z.string().nullish(),
  draft: slotSchema,
  draftUpdatedAt: z.string(),
  published: slotSchema.nullish(),
  publishedAt: z.string().nullish(),
  hasUnpublishedChanges: z.boolean(),
  helpful: z.number(),
  notHelpful: z.number(),
  version: z.string(),
})
export type ArticleAdmin = z.infer<typeof articleAdminSchema>

export type ArticleInput = { title?: string; summary?: string; body?: string; categoryId?: string | null; clearCategory?: boolean; visibility?: string; version?: string }

const base = (d: string) => `/api/departments/${d}/knowledge`
export const listCategories = (d: string) => api('GET', `${base(d)}/categories`, { schema: z.array(categoryAdminSchema) })
export const createCategory = (d: string, body: { name: string; description?: string }) => api('POST', `${base(d)}/categories`, { body, schema: categoryAdminSchema })
export const updateCategory = (d: string, id: string, body: { name?: string; description?: string; isActive?: boolean; version: string }) =>
  api('PATCH', `${base(d)}/categories/${id}`, { body, schema: categoryAdminSchema })
export const reorderCategories = (d: string, ids: string[]) => api('PUT', `${base(d)}/categories/order`, { body: { ids }, schema: z.array(categoryAdminSchema) })

export const listArticles = (d: string, opts: { status?: string; q?: string; page?: number }) => {
  const p = new URLSearchParams()
  if (opts.status) p.set('status', opts.status)
  if (opts.q) p.set('q', opts.q)
  if (opts.page) p.set('page', String(opts.page))
  return api('GET', `${base(d)}/articles?${p.toString()}`, { schema: z.array(articleListItemSchema) })
}
export const getArticle = (d: string, id: string) => api('GET', `${base(d)}/articles/${id}`, { schema: articleAdminSchema })
export const createArticle = (d: string, body: ArticleInput) => api('POST', `${base(d)}/articles`, { body, schema: articleAdminSchema })
export const saveDraft = (d: string, id: string, body: ArticleInput) => api('PATCH', `${base(d)}/articles/${id}`, { body, schema: articleAdminSchema })
export const publishArticle = (d: string, id: string, version: string) => api('POST', `${base(d)}/articles/${id}/publish`, { body: { version }, schema: articleAdminSchema })
export const archiveArticle = (d: string, id: string, version: string) => api('POST', `${base(d)}/articles/${id}/archive`, { body: { version }, schema: articleAdminSchema })
