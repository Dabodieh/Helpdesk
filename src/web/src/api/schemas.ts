import { z } from 'zod'

export const ROLES = ['DepartmentAdmin', 'TeamLead', 'Agent', 'Viewer'] as const
export type Role = (typeof ROLES)[number]

/** Roles as people read them (DOC-007); the code stays the value. */
export const ROLE_LABEL: Record<string, string> = { DepartmentAdmin: 'Department admin', TeamLead: 'Team lead', Agent: 'Agent', Viewer: 'Viewer' }

export const csrfSchema = z.object({ headerName: z.string(), token: z.string() })

export const meSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  email: z.string().nullable().optional(),
  isPlatformAdmin: z.boolean(),
  platformPermissions: z.array(z.string()),
  memberships: z.array(
    z.object({
      departmentId: z.string(),
      departmentKey: z.string(),
      departmentName: z.string(),
      role: z.string(),
      permissions: z.array(z.string()),
    }),
  ),
})
export type Me = z.infer<typeof meSchema>

export const departmentSchema = z.object({
  id: z.string(),
  key: z.string(),
  name: z.string(),
  description: z.string().nullable().optional(),
  isActive: z.boolean(),
  myRole: z.string().nullable().optional(),
  version: z.string(),
})
export type Department = z.infer<typeof departmentSchema>

export const userRefSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  email: z.string().nullable().optional(),
})
export type UserRef = z.infer<typeof userRefSchema>

export const memberBaseSchema = z.object({
  userId: z.string(),
  displayName: z.string(),
  email: z.string().nullable().optional(),
})
export type MemberBase = z.infer<typeof memberBaseSchema>

export const memberSchema = memberBaseSchema.extend({
  role: z.string(),
})
export type Member = z.infer<typeof memberSchema>

export const DEPARTMENT_KEY = /^[A-Z][A-Z0-9]{1,9}$/
