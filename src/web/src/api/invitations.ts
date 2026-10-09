import { z } from 'zod'
import { api } from './http'

export const invitationSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  email: z.string(),
  departmentId: z.string(),
  departmentName: z.string(),
  role: z.string(),
  status: z.enum(['Pending', 'Accepted', 'Revoked', 'Expired']),
  expiresAt: z.string(),
  createdAt: z.string(),
  emailState: z.enum(['NotConfigured', 'Accepted', 'Failed', 'Uncertain']),
  acceptedAt: z.string().nullable(),
})
export type Invitation = z.infer<typeof invitationSchema>

/** The link is shown once, when the invitation is created or re-issued: only its hash is stored. */
const issuedSchema = z.object({ invitation: invitationSchema, activationUrl: z.string() })
export type IssuedInvitation = z.infer<typeof issuedSchema>

export const listInvitations = () => api('GET', '/api/admin/invitations', { schema: z.array(invitationSchema) })

export const createInvitation = (body: { displayName: string; email: string; departmentId: string; role: string }) =>
  api('POST', '/api/admin/invitations', { body, schema: issuedSchema })

export const resendInvitation = (id: string) => api('POST', `/api/admin/invitations/${encodeURIComponent(id)}/resend`, { body: {}, schema: issuedSchema })

export const revokeInvitation = (id: string) => api('POST', `/api/admin/invitations/${encodeURIComponent(id)}/revoke`, { body: {} })

const previewSchema = z.object({ displayName: z.string(), departmentName: z.string(), role: z.string() })
const acceptedSchema = z.object({ departmentId: z.string(), departmentName: z.string(), role: z.string(), alreadyMember: z.boolean() })

export const previewInvitation = (token: string) => api('POST', '/api/invitations/preview', { body: { token }, schema: previewSchema })
export const acceptInvitation = (token: string) => api('POST', '/api/invitations/accept', { body: { token }, schema: acceptedSchema })
