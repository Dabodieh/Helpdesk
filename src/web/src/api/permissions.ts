import type { Me } from './schemas'

/** Administrative permissions that the platform-admin flag also satisfies (docs/architecture/PERMISSIONS.md). UI hint only. */
const ADMINISTRATIVE = new Set([
  'department.read',
  'department.manage',
  'department.members.manage',
  'teams.manage',
  'department.audit.read',
])

export function canPlatform(me: Me, code: string) {
  return me.platformPermissions.includes(code)
}

/** UX hint: does the caller appear to hold `code` in the department? The server decides. */
export function canInDepartment(me: Me, code: string, departmentId: string) {
  if (me.isPlatformAdmin && ADMINISTRATIVE.has(code)) return true
  return me.memberships.some((m) => m.departmentId === departmentId && m.permissions.includes(code))
}
