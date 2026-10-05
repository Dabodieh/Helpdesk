import type { Activity } from '../api/ticketSchemas'

/** Names for ids that appear in activity values. Unknown ids degrade to a neutral phrase, never a raw id. */
export type NameLookup = { users: Map<string, string>; teams: Map<string, string> }

type Obj = Record<string, unknown> | null | undefined

/** First string value among `keys`; falls back to the only value of a single-entry object. */
function pick(o: Obj, ...keys: string[]): string | null {
  if (!o) return null
  for (const k of keys) {
    const v = o[k]
    if (typeof v === 'string') return v
  }
  const values = Object.values(o)
  return values.length === 1 && typeof values[0] === 'string' ? values[0] : null
}

const quote = (s: string | null) => (s ? `"${s}"` : 'nothing')

/** Message events are shown in the conversation, so the timeline omits them. */
export const isMessageEvent = (a: Activity) => a.action === 'ticket.message_added'

export function actorName(a: Activity) {
  return a.actor?.displayName ?? 'System'
}

/** A human sentence fragment that follows the actor name, e.g. "changed status from Open to Pending". */
export function describeActivity(a: Activity, names: NameLookup): string {
  const who = (id: string | null) => (id ? (names.users.get(id) ?? 'another agent') : 'nobody')
  const team = (id: string | null) => (id ? (names.teams.get(id) ?? 'another team') : 'no team')
  switch (a.action) {
    case 'ticket.created': {
      const status = pick(a.next, 'status')
      const priority = pick(a.next, 'priority')
      const detail = [status, priority].filter(Boolean).join(', ')
      const fromEmail = pick(a.next, 'source') === 'email'
      const base = fromEmail ? 'created the ticket from an email' : 'created the ticket'
      return detail ? `${base} (${detail})` : base
    }
    case 'ticket.subject_changed':
      return `changed the subject from ${quote(pick(a.previous, 'subject'))} to ${quote(pick(a.next, 'subject'))}`
    case 'ticket.status_changed': {
      const base = `changed status from ${pick(a.previous, 'status') ?? 'unknown'} to ${pick(a.next, 'status') ?? 'unknown'}`
      return a.reason === 'requester_reply' ? `${base} (the requester replied)` : base
    }
    case 'ticket.priority_changed':
      return `changed priority from ${pick(a.previous, 'priority') ?? 'unknown'} to ${pick(a.next, 'priority') ?? 'unknown'}`
    case 'ticket.team_changed':
      return `changed the team from ${team(pick(a.previous, 'teamId', 'team'))} to ${team(pick(a.next, 'teamId', 'team'))}`
    case 'ticket.assignee_changed': {
      const from = pick(a.previous, 'assigneeUserId', 'assignee')
      const to = pick(a.next, 'assigneeUserId', 'assignee')
      if (!to) return from ? `unassigned the ticket from ${who(from)}` : 'left the ticket unassigned'
      return from ? `reassigned the ticket from ${who(from)} to ${who(to)}` : `assigned the ticket to ${who(to)}`
    }
    case 'ticket.message_delivery_changed': {
      const state = pick(a.next, 'delivery')
      if (state === 'Sent') return 'sent a reply by email'
      if (state === 'Failed') return 'could not send a reply by email'
      if (state === 'Pending') return 'queued a reply to be sent by email'
      return 'changed the delivery of a reply'
    }
    default:
      return `performed ${a.action.replace(/[._]/g, ' ')}`
  }
}
