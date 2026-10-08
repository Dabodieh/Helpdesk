import { canInDepartment, canTicket } from '../api/permissions'
import type { Me } from '../api/schemas'

export const ADMIN_CATEGORIES = [
  { id: 'people', title: 'Organisation & access', description: 'The people behind your service desk.', icon: 'team' },
  { id: 'tickets', title: 'Ticket management', description: 'Structure requests and route work to the right people.', icon: 'ticket' },
  { id: 'communication', title: 'Channels & communication', description: 'Connect your inboxes and keep requesters informed.', icon: 'mail' },
  { id: 'service', title: 'Service management', description: 'Set service targets and automate routine work.', icon: 'clock' },
  { id: 'selfservice', title: 'Self-service', description: 'Help people find answers and request support.', icon: 'book' },
  { id: 'productivity', title: 'Agent productivity', description: 'Give agents consistent, reusable ways to respond.', icon: 'bolt' },
  { id: 'reporting', title: 'Reporting & analytics', description: 'Understand the current workload and service risks.', icon: 'chart' },
] as const
export type AdminCategory = (typeof ADMIN_CATEGORIES)[number]['id']
export type AdminSetting = { id: string; title: string; description: string; icon: string; category: AdminCategory; permission: string; section: string; tab?: string; keywords?: string; content?: boolean }
export const ADMIN_SETTINGS: AdminSetting[] = [
  { id: 'members', title: 'Members & roles', description: 'Manage department membership and access.', icon: 'person', category: 'people', permission: 'department.read', section: 'members', keywords: 'users agents permissions invitations' },
  { id: 'requesters', title: 'Requester identities', description: 'Review unlinked requesters and account matches.', icon: 'identity', category: 'people', permission: 'department.members.manage', section: 'requesters', keywords: 'users identity access' },
  { id: 'types', title: 'Ticket types', description: 'Define the kinds of work your department handles.', icon: 'ticket', category: 'tickets', permission: 'department.forms.manage', section: 'ticketing', tab: 'types' },
  { id: 'fields', title: 'Ticket fields', description: 'Capture the information agents need to resolve work.', icon: 'fields', category: 'tickets', permission: 'department.forms.manage', section: 'ticketing', tab: 'fields', keywords: 'custom properties' },
  { id: 'mailboxes', title: 'Mailboxes', description: 'Manage shared inboxes, sending and mail health.', icon: 'mail', category: 'communication', permission: 'department.mailboxes.manage', section: 'mailboxes', keywords: 'email exchange graph sender channels' },
  { id: 'acknowledgements', title: 'Acknowledgements', description: 'Configure automatic confirmations for new requests.', icon: 'reply', category: 'communication', permission: 'department.mailboxes.manage', section: 'acknowledgements', keywords: 'email auto reply confirmation' },
  { id: 'hours', title: 'Business hours', description: 'Set working calendars, time zones and holidays.', icon: 'calendar', category: 'service', permission: 'department.sla.manage', section: 'hours' },
  { id: 'sla', title: 'SLA policies', description: 'Set first-response and resolution targets.', icon: 'clock', category: 'service', permission: 'department.sla.manage', section: 'sla', keywords: 'service level due overdue response' },
  { id: 'automations', title: 'Automations & routing', description: 'Apply rules for assignment and ticket updates.', icon: 'route', category: 'service', permission: 'department.automation.manage', section: 'automations', keywords: 'workflow escalation routing assignment' },
  { id: 'forms', title: 'Request forms', description: 'Build forms and choose who can submit them.', icon: 'file', category: 'selfservice', permission: 'department.forms.manage', section: 'ticketing', tab: 'forms', keywords: 'portal audience catalogue' },
  { id: 'topics', title: 'Help topics', description: 'Guide requesters to the right request form.', icon: 'help', category: 'selfservice', permission: 'department.forms.manage', section: 'ticketing', tab: 'topics', keywords: 'portal catalogue' },
  { id: 'knowledge', title: 'Knowledge', description: 'Manage articles, categories and publishing.', icon: 'book', category: 'selfservice', permission: 'department.knowledge.manage', section: 'knowledge', keywords: 'self service answers help' },
  { id: 'macros', title: 'Macros', description: 'Bundle repeatable ticket actions for agents.', icon: 'bolt', category: 'productivity', permission: 'department.canned.manage', section: 'macros' },
  { id: 'responses', title: 'Canned responses', description: 'Maintain reusable replies shared by the department.', icon: 'chat', category: 'productivity', permission: 'department.canned.manage', section: 'responses', keywords: 'templates communication email reply shared' },
  { id: 'dashboard', title: 'Operational dashboard', description: 'View backlog, SLA risks and workload by assignee.', icon: 'chart', category: 'reporting', permission: 'tickets.read', section: 'dashboard', content: true, keywords: 'report analytics performance' },
]
export const allowedSettings = (me: Me, departmentId: string) => ADMIN_SETTINGS.filter(s => s.content ? canTicket(me, s.permission, departmentId) : canInDepartment(me, s.permission, departmentId))
export function settingHref(setting: AdminSetting, departmentId: string) {
  if (setting.id === 'dashboard') return `/dashboard/${encodeURIComponent(departmentId)}`
  const params = new URLSearchParams({ section: setting.section })
  if (setting.tab) params.set('tab', setting.tab)
  return `/admin/departments/${encodeURIComponent(departmentId)}?${params}`
}
export function matchesSetting(setting: AdminSetting, search: string) {
  const category = ADMIN_CATEGORIES.find(c => c.id === setting.category)!
  const text = `${setting.title} ${setting.description} ${setting.keywords ?? ''} ${category.title}`.toLowerCase()
  return search.trim().toLowerCase().split(/\s+/).every(word => text.includes(word))
}
