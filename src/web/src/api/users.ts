import { z } from 'zod'
import { api } from './http'

export const platformUserSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  email: z.string().nullable(),
  isPlatformAdmin: z.boolean(),
  isActive: z.boolean(),
})
export type PlatformUser = z.infer<typeof platformUserSchema>

const pageSchema = z.object({ items: z.array(platformUserSchema), nextCursor: z.string().nullable() })

/** Platform administrators only (the server decides). `search` filters by name or address. */
export const listPlatformUsers = (search: string) =>
  api('GET', `/api/platform/users?limit=50${search ? `&search=${encodeURIComponent(search)}` : ''}`, { schema: pageSchema })

/** Deactivation ends every session of the person and releases their assigned tickets; reactivation requires them to sign in again. */
export const setUserActive = (id: string, value: boolean) =>
  api('PUT', `/api/platform/users/${encodeURIComponent(id)}/active`, { body: { value }, schema: platformUserSchema })
