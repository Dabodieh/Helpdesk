import { z } from 'zod'
import { api } from './http'

export const entraStatusSchema = z.object({
  source: z.enum(['none', 'server', 'database']),
  configured: z.boolean(),
  tenantId: z.string().nullable(),
  clientId: z.string().nullable(),
  secretExpiresAt: z.string().nullable(),
  secretExpiresSoon: z.boolean(),
  secretUnreadable: z.boolean(),
  applicationObjectId: z.string().nullable(),
  redirectOrigins: z.array(z.string()),
  devSignInEnabled: z.boolean(),
})
export type EntraStatus = z.infer<typeof entraStatusSchema>

const startSchema = z.object({ sessionId: z.string(), userCode: z.string(), verificationUri: z.string(), expiresInSeconds: z.number(), intervalSeconds: z.number() })
export type EntraStart = z.infer<typeof startSchema>

const pollSchema = z.object({
  state: z.enum(['pending', 'signed_in', 'expired', 'declined', 'failed']),
  intervalSeconds: z.number(),
  adminName: z.string().nullable(),
  adminAccount: z.string().nullable(),
  tenantId: z.string().nullable(),
})
export type EntraPoll = z.infer<typeof pollSchema>

const createdSchema = z.object({ tenantId: z.string(), clientId: z.string(), secretExpiresAt: z.string(), redirectUris: z.array(z.string()), signedInAs: z.string(), previousApplicationObjectId: z.string().nullable() })
export type EntraCreated = z.infer<typeof createdSchema>

export const getEntraStatus = () => api('GET', '/api/admin/entra', { schema: entraStatusSchema })
export const startEntraSetup = () => api('POST', '/api/admin/entra/setup/start', { body: {}, schema: startSchema })
export const pollEntraSetup = (sessionId: string) => api('POST', '/api/admin/entra/setup/poll', { body: { sessionId }, schema: pollSchema })
export const createEntraApp = (sessionId: string, redirectOrigins: string[]) => api('POST', '/api/admin/entra/setup/create', { body: { sessionId, redirectOrigins }, schema: createdSchema })
export const disconnectEntra = () => api('POST', '/api/admin/entra/disconnect', { body: {} })

/** Whether staff can sign in with Microsoft yet. Unauthenticated; used by the sign-in page. */
export const getAuthProviders = () => api('GET', '/api/auth/providers', { schema: z.object({ entra: z.boolean() }) })
