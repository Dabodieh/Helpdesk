import type { Me } from './schemas'

/** Administrative permissions that the platform-admin flag also satisfies (docs/architecture/PERMISSIONS.md). UI hint only. */
const ADMINISTRATIVE = new Set([
  'department.read',
  'department.manage',
  'department.members.manage',
  'department.mailboxes.manage',
  'department.audit.read',
  'department.canned.manage',
  'department.sla.manage',
  'department.automation.manage',
  'department.forms.manage',
  'department.knowledge.manage',
])

export function canPlatform(me: Me, code: string) {
  return me.platformPermissions.includes(code)
}

/** UX hint: does the caller appear to hold `code` in the department? The server decides. */
export function canInDepartment(me: Me, code: string, departmentId: string) {
  if (me.isPlatformAdmin && ADMINISTRATIVE.has(code)) return true
  return me.memberships.some((m) => m.departmentId === departmentId && m.permissions.includes(code))
}

/** Ticket (content) permissions: a role in the department grants them, and so does the platform-admin flag in every department (S-02). UX hint; the server decides. */
export function canTicket(me: Me, code: string, departmentId: string) {
  if (me.isPlatformAdmin) return true
  return me.memberships.some((m) => m.departmentId === departmentId && m.permissions.includes(code))
}

/**
 * Departments in which the caller can use `code`: the memberships whose role grants it, plus every department for a platform administrator (S-02). Global access
 * never creates a membership, assignment, follow or notification. UX hint; the server decides.
 */
export function departmentsWith(me: Me, code: string) {
  const own = me.memberships.filter((m) => m.permissions.includes(code)).map((m) => ({ departmentId: m.departmentId, departmentKey: m.departmentKey, departmentName: m.departmentName }))
  if (!me.isPlatformAdmin) return own
  const seen = new Set(own.map((d) => d.departmentId))
  return [...own, ...me.departments.filter((d) => !seen.has(d.departmentId)).map((d) => ({ departmentId: d.departmentId, departmentKey: d.departmentKey, departmentName: d.departmentName }))]
}

export const hasAnyPermission = (me: Me, code: string) => departmentsWith(me, code).length > 0

/** Does /api/me show anything the user can administer? Controls only whether the Administration entry is offered. */
export function canAdminister(me: Me) {
  return (
    me.platformPermissions.length > 0 ||
    me.memberships.some((m) =>
      ['department.manage', 'department.members.manage', 'department.mailboxes.manage'].some((c) => m.permissions.includes(c)),
    )
  )
}
