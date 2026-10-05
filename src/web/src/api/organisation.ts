import { z } from 'zod'
import { api, ApiError } from './http'
import {
  departmentSchema,
  memberSchema,
  meSchema,
  teamMemberSchema,
  teamSchema,
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
const t = (id: string, teamId: string) => `${d(id)}/teams/${encodeURIComponent(teamId)}`

export const listDepartments = () => api('GET', '/api/departments', { schema: z.array(departmentSchema) })
export const getDepartment = (id: string) => api('GET', d(id), { schema: departmentSchema })
export const createDepartment = (body: { key: string; name: string; description?: string }) =>
  api('POST', '/api/departments', { body, schema: departmentSchema })
export const updateDepartment = (
  id: string,
  body: { version: string; name?: string; description?: string; isActive?: boolean },
) => api('PATCH', d(id), { body, schema: departmentSchema })

export const listTeams = (id: string) => api('GET', `${d(id)}/teams`, { schema: z.array(teamSchema) })
export const getTeam = (id: string, teamId: string) => api('GET', t(id, teamId), { schema: teamSchema })
export const createTeam = (id: string, body: { name: string; description?: string }) =>
  api('POST', `${d(id)}/teams`, { body, schema: teamSchema })
export const updateTeam = (
  id: string,
  teamId: string,
  body: { version: string; name?: string; description?: string; isActive?: boolean },
) => api('PATCH', t(id, teamId), { body, schema: teamSchema })

export const listTeamMembers = (id: string, teamId: string) =>
  api('GET', `${t(id, teamId)}/members`, { schema: z.array(teamMemberSchema) })
export const addTeamMember = (id: string, teamId: string, userId: string) =>
  api('PUT', `${t(id, teamId)}/members/${encodeURIComponent(userId)}`)
export const removeTeamMember = (id: string, teamId: string, userId: string) =>
  api('DELETE', `${t(id, teamId)}/members/${encodeURIComponent(userId)}`)

export const listMembers = (id: string) => api('GET', `${d(id)}/members`, { schema: z.array(memberSchema) })
export const setMemberRole = (id: string, userId: string, role: Role) =>
  api('PUT', `${d(id)}/members/${encodeURIComponent(userId)}`, { body: { role }, schema: memberSchema })
export const removeMember = (id: string, userId: string) =>
  api('DELETE', `${d(id)}/members/${encodeURIComponent(userId)}`)

export const lookupUsers = (q: string) =>
  api('GET', `/api/users/lookup?q=${encodeURIComponent(q)}`, { schema: z.array(userRefSchema) })
