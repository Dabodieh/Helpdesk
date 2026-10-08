import type { RuleAction } from '../api/automation'
import type { Condition } from '../api/sla'

export const POLICY_FIELDS = ['priority', 'team', 'mailbox', 'subjectContains', 'requesterEmail', 'requesterDomain'] as const
export const RULE_FIELDS = [
  'status',
  'priority',
  'team',
  'assignee',
  'mailbox',
  'subjectContains',
  'requesterEmail',
  'requesterDomain',
  'slaFirstResponse',
  'slaResolution',
] as const

export const FIELD_LABEL: Record<string, string> = {
  status: 'Status',
  priority: 'Priority',
  team: 'Team',
  assignee: 'Assignee',
  mailbox: 'Mailbox',
  subjectContains: 'Subject contains',
  requesterEmail: 'Requester email is',
  requesterDomain: 'Requester domain is',
  slaFirstResponse: 'First response SLA',
  slaResolution: 'Resolution SLA',
}

export const ACTION_LABEL: Record<string, string> = {
  setStatus: 'Set status',
  setPriority: 'Set priority',
  assignTeam: 'Assign team',
  assignAgent: 'Assign agent',
  addInternalNote: 'Add internal note',
}

/** Why an automation run was skipped, in words (DOC-006). */
export const SKIP_LABEL: Record<string, string> = {
  loop_detected: 'loop protection: this rule already ran for the ticket in this chain',
  max_depth: 'loop protection: the chain of rules was too deep',
  max_executions: 'loop protection: too many rules ran in one chain',
}

/** What an action did in a recorded run, in words. */
export const OUTCOME_LABEL: Record<string, string> = { Done: 'Done', NoChange: 'No change needed', Failed: 'Failed', Pending: 'Not run' }

export function typeKey(type: string) {
  return type === 'setStatus' ? 'status' : type === 'setPriority' ? 'priority' : type === 'assignTeam' ? 'team' : 'assignee'
}

/** Plain-language one-line summary of conditions and actions for list rows. */
export function summariseRule(conditions: Condition[], actions: RuleAction[], name: (kind: string, value: string) => string = (_k, v) => v) {
  const c = conditions.length === 0 ? 'Always' : conditions.map((x) => `${FIELD_LABEL[x.field] ?? x.field} ${x.operator === 'notIn' ? 'not ' : ''}${x.values.map((v) => name(x.field, v)).join(', ')}`).join(' and ')
  const a = actions.map((x) => (x.type === 'addInternalNote' ? 'Add internal note' : `${ACTION_LABEL[x.type] ?? x.type}: ${name(typeKey(x.type), x.value ?? '')}`)).join(', ')
  return `${c} → ${a}`
}
