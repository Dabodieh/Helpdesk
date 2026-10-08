import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import { listMembers } from '../api/organisation'
import { listMailboxes } from '../api/mail'
import { getAutoReply } from '../api/emailIdentity'
import { listBusinessHours, listSlaPolicies } from '../api/sla'
import { listRules } from '../api/automation'
import { listHelpTopics } from '../api/helpTopics'
import { listForms, listTicketTypes, listTicketFields } from '../api/ticketing'
import { listCategories } from '../api/knowledgeAdmin'
import { listMacros } from '../api/macros'
import { listCanned } from '../api/canned'
import { listIdentityCases, IDENTITY_PAGE } from '../api/requesterIdentity'
import { canInDepartment } from '../api/permissions'
import { useMe } from '../auth'
import type { Tone } from '../components/common'

export type ConfigurationState = { label: string; tone: Tone; warning?: string; error?: boolean; loading?: boolean; retry?: () => void }
export const countLabel = (n: number, noun: string, plural = `${noun}s`) => `${n} ${n === 1 ? noun : plural}`
const count = (n: number, noun: string, plural?: string): ConfigurationState => ({ label: countLabel(n, noun, plural), tone: n ? 'ok' : 'off' })
function state<T>(query: UseQueryResult<T>, render: (data: T) => ConfigurationState): ConfigurationState {
  if (query.isError) return { label: 'Status unavailable', tone: 'warn', error: true, retry: () => { void query.refetch() } }
  if (query.data === undefined) return { label: 'Loading status…', tone: 'off', loading: true }
  return render(query.data)
}

/** Only the selected Department is queried. Metadata privileges never enable ticket-content queries. */
export function useAdminConfiguration(id: string) {
  const me = useMe()
  const can = (p: string) => !!id && canInDepartment(me, p, id)
  const members = useQuery({ queryKey: ['members', id], queryFn: () => listMembers(id), enabled: can('department.read') })
  const identities = useQuery({ queryKey: ['requester-identities', id, 'summary'], queryFn: () => listIdentityCases(id), enabled: can('department.members.manage') })
  const mailboxes = useQuery({ queryKey: ['mailboxes', id], queryFn: () => listMailboxes(id), enabled: can('department.mailboxes.manage') })
  const ack = useQuery({ queryKey: ['auto-reply', id], queryFn: () => getAutoReply(id), enabled: can('department.mailboxes.manage') })
  const hours = useQuery({ queryKey: ['business-hours', id], queryFn: () => listBusinessHours(id), enabled: can('department.sla.manage') })
  const sla = useQuery({ queryKey: ['sla-policies', id], queryFn: () => listSlaPolicies(id), enabled: can('department.sla.manage') })
  const rules = useQuery({ queryKey: ['automations', id], queryFn: () => listRules(id), enabled: can('department.automation.manage') })
  const forms = useQuery({ queryKey: ['request-forms', id], queryFn: () => listForms(id), enabled: can('department.forms.manage') })
  const types = useQuery({ queryKey: ['ticket-types', id], queryFn: () => listTicketTypes(id), enabled: can('department.forms.manage') })
  const fields = useQuery({ queryKey: ['ticket-fields', id], queryFn: () => listTicketFields(id), enabled: can('department.forms.manage') })
  const knowledge = useQuery({ queryKey: ['knowledge-categories', id], queryFn: () => listCategories(id), enabled: can('department.knowledge.manage') })
  const macros = useQuery({ queryKey: ['macros', id], queryFn: () => listMacros(id), enabled: can('department.canned.manage') })
  const responses = useQuery({ queryKey: ['canned', 'manage', 'Department', id], queryFn: () => listCanned({ departmentId: id, includeInactive: true }), enabled: can('department.canned.manage') })
  const topics = useQuery({ queryKey: ['help-topics', id], queryFn: () => listHelpTopics(id), enabled: can('department.forms.manage') })
  const states: Record<string, ConfigurationState> = {
    members: state(members, data => ({ ...count(data.length, 'member'), ...(data.length ? {} : { tone: 'warn', warning: 'Add members so people can work in this department.' }) })),
    requesters: state(identities, data => ({ label: `${data.length}${data.length === IDENTITY_PAGE ? '+' : ''} unlinked requesters`, tone: data.some(r => r.needsAttention) ? 'warn' : 'off' })),
    mailboxes: state(mailboxes, data => {
      const active = data.filter(m => m.isActive)
      const failing = active.filter(m => m.health?.status === 'Failed' || m.health?.status === 'Attention')
      if (!active.length) return { label: 'No active mailbox', tone: 'warn', warning: 'Configure an active mailbox for email replies and new requests.' }
      if (!active.some(m => m.isDefault)) return { label: 'Default mailbox missing', tone: 'warn', warning: 'Choose a default mailbox before requesters submit new portal requests.' }
      if (failing.length) return { label: countLabel(failing.length, 'mailbox needs attention', 'mailboxes need attention'), tone: 'warn', warning: 'Review mailbox health and mail requiring attention.' }
      return { label: countLabel(active.length, 'active mailbox', 'active mailboxes'), tone: 'ok' }
    }),
    acknowledgements: state(ack, data => ({ label: data.enabled && (data.forEmail || data.forPortal) ? 'Enabled' : 'Off', tone: data.enabled && (data.forEmail || data.forPortal) ? 'ok' : 'off' })),
    hours: state(hours, data => count(data.length, 'calendar')),
    sla: state(sla, data => count(data.filter(p => p.isActive).length, 'active policy', 'active policies')),
    automations: state(rules, data => count(data.filter(r => r.isActive).length, 'active rule')),
    forms: state(forms, data => {
      const available = data.filter(f => f.isActive && f.visibleToRequesters && (f.audience === 'AllEmployees' || f.audienceUsers.length > 0)).length
      return { ...count(available, 'form available', 'forms available'), ...(data.some(f => f.isActive) && !available ? { tone: 'warn' as const, warning: 'Active forms have no requester audience. Review publication and audience settings.' } : {}) }
    }),
    topics: state(topics, data => count(data.filter(t => t.isActive && t.formAvailable).length, 'available topic')),
    types: state(types, data => count(data.filter(t => t.isActive).length, 'active type')),
    fields: state(fields, data => count(data.filter(f => f.isActive).length, 'active field')),
    knowledge: state(knowledge, data => count(data.filter(c => c.isActive).length, 'active category', 'active categories')),
    macros: state(macros, data => count(data.filter(m => m.isActive).length, 'active macro')),
    responses: state(responses, data => count(data.filter(c => c.scope === 'Department' && c.departmentId === id && c.isActive).length, 'shared response')),
  }
  return states
}
