import { z } from 'zod'
import { api, ApiError } from './http'
import {
  departmentSchema,
  memberSchema,
  meSchema,
  userRefSchema,
  type Me,
  type Role,
} from './schemas'

export async function fetchMe(): Promise<Me | null> {
  try {
    return await api('GET', '/api/me', { schema: meSchema })
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) return null
    throw e
  }
}

export const logout = () => api('POST', '/api/auth/logout')

const d = (id: string) => `/api/departments/${encodeURIComponent(id)}`

export const listDepartments = () => api('GET', '/api/departments', { schema: z.array(departmentSchema) })
export const getDepartment = (id: string) => api('GET', d(id), { schema: departmentSchema })
export const createDepartment = (body: { key: string; name: string; description?: string }) =>
  api('POST', '/api/departments', { body, schema: departmentSchema })
export const updateDepartment = (
  id: string,
  body: { version: string; name?: string; description?: string; isActive?: boolean },
) => api('PATCH', d(id), { body, schema: departmentSchema })

export const listMembers = (id: string) => api('GET', `${d(id)}/members`, { schema: z.array(memberSchema) })
export const setMemberRole = (id: string, userId: string, role: Role) =>
  api('PUT', `${d(id)}/members/${encodeURIComponent(userId)}`, { body: { role }, schema: memberSchema })
export const removeMember = (id: string, userId: string) =>
  api('DELETE', `${d(id)}/members/${encodeURIComponent(userId)}`)

export const lookupUsers = (q: string) =>
  api('GET', `/api/users/lookup?q=${encodeURIComponent(q)}`, { schema: z.array(userRefSchema) })
