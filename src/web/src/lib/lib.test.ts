import { expect, test } from 'vitest'
import type { Activity } from '../api/ticketSchemas'
import { actorName, describeActivity, isMessageEvent, type NameLookup } from './activity'
import { parseFilters } from './filters'
import { timeAgo } from './format'
import { shouldIgnoreShortcut } from './shortcuts'

const names: NameLookup = {
  users: new Map([['u2', 'Bea Agent']]),
  teams: new Map([['tm1', 'Tier 1']]),
}
const act = (over: Partial<Activity>): Activity => ({
  id: 'a',
  occurredAt: '2026-10-01T10:00:00Z',
  actor: { id: 'u1', displayName: 'Ann Agent' },
  action: 'ticket.created',
  previous: null,
  next: null,
  ...over,
})

test('activity sentences are human text built from action/previous/next', () => {
  expect(describeActivity(act({ action: 'ticket.status_changed', previous: { status: 'Open' }, next: { status: 'Resolved' } }), names)).toBe(
    'changed status from Open to Resolved',
  )
  expect(
    describeActivity(act({ action: 'ticket.status_changed', reason: 'requester_reply', previous: { status: 'Closed' }, next: { status: 'Open' } }), names),
  ).toBe('changed status from Closed to Open (the requester replied)')
  expect(describeActivity(act({ action: 'ticket.priority_changed', previous: { priority: 'Low' }, next: { priority: 'High' } }), names)).toBe(
    'changed priority from Low to High',
  )
  expect(describeActivity(act({ action: 'ticket.subject_changed', previous: { subject: 'Old' }, next: { subject: 'New' } }), names)).toBe(
    'changed the subject from "Old" to "New"',
  )
  expect(describeActivity(act({ action: 'ticket.team_changed', previous: { teamId: null }, next: { teamId: 'tm1' } }), names)).toBe(
    'changed the team from no team to Tier 1',
  )
  expect(describeActivity(act({ action: 'ticket.assignee_changed', previous: { assigneeUserId: null }, next: { assigneeUserId: 'u2' } }), names)).toBe(
    'assigned the ticket to Bea Agent',
  )
  expect(describeActivity(act({ action: 'ticket.assignee_changed', previous: { assigneeUserId: 'u2' }, next: { assigneeUserId: null } }), names)).toBe(
    'unassigned the ticket from Bea Agent',
  )
  expect(describeActivity(act({ action: 'ticket.created', next: { reference: 'IT-1', status: 'Open', priority: 'Normal' } }), names)).toBe(
    'created the ticket (Open, Normal)',
  )
})

test('activity never prints raw JSON or raw ids; unknown ids and actions degrade to neutral text', () => {
  const unknownUser = describeActivity(act({ action: 'ticket.assignee_changed', previous: null, next: { assigneeUserId: 'zzz-guid' } }), names)
  expect(unknownUser).toBe('assigned the ticket to another agent')
  expect(unknownUser).not.toContain('zzz')
  expect(describeActivity(act({ action: 'ticket.something_new', next: { a: 1 } }), names)).toBe('performed ticket something new')
  expect(actorName(act({ actor: null }))).toBe('System')
})

test('message events are identified so the timeline can omit them', () => {
  expect(isMessageEvent(act({ action: 'ticket.message_added' }))).toBe(true)
  expect(isMessageEvent(act({ action: 'ticket.status_changed' }))).toBe(false)
})

test('shortcut guard: typing targets and modifiers are ignored', () => {
  const ev = (target: HTMLElement | Document, init: KeyboardEventInit = {}) => {
    const e = new KeyboardEvent('keydown', { key: 'j', bubbles: true, ...init })
    Object.defineProperty(e, 'target', { value: target })
    return e
  }
  expect(shouldIgnoreShortcut(ev(document.createElement('input')))).toBe(true)
  expect(shouldIgnoreShortcut(ev(document.createElement('textarea')))).toBe(true)
  expect(shouldIgnoreShortcut(ev(document.createElement('select')))).toBe(true)
  const editable = document.createElement('div')
  Object.defineProperty(editable, 'isContentEditable', { value: true })
  expect(shouldIgnoreShortcut(ev(editable))).toBe(true)
  expect(shouldIgnoreShortcut(ev(document.createElement('div'), { ctrlKey: true }))).toBe(true)
  expect(shouldIgnoreShortcut(ev(document.createElement('div'), { metaKey: true }))).toBe(true)
  expect(shouldIgnoreShortcut(ev(document.createElement('div'), { altKey: true }))).toBe(true)
  expect(shouldIgnoreShortcut(ev(document.createElement('div')))).toBe(false)
  expect(shouldIgnoreShortcut(ev(document.body))).toBe(false)
})

test('filters: URL state maps to server query; defaults per mode', () => {
  const none = new URLSearchParams()
  expect(parseFilters('my', none, 'queue', 'u1').statuses).toEqual(['Open', 'Pending'])
  expect(parseFilters('all', none, 'search', 'u1').statuses).toEqual(['Open', 'Pending', 'Resolved', 'Closed'])
  const f = parseFilters('all', new URLSearchParams('status=Open,closed&priority=high&assignee=none&q=a&sort=priority_desc'), 'queue', 'u1')
  expect(f.statuses).toEqual(['Open', 'Closed'])
  expect(f.priorities).toEqual(['High'])
  expect(f.unassigned).toBe(true)
  expect(f.q).toBeUndefined() // shorter than the server minimum of 2
  expect(f.sort).toBe('priority_desc')
  expect(parseFilters('all', new URLSearchParams('assignee=me&sort=bogus'), 'queue', 'u1')).toMatchObject({ assigneeUserId: 'u1', sort: 'updated_desc' })
})

test('timeAgo is compact', () => {
  const now = Date.parse('2026-10-05T12:00:00Z')
  expect(timeAgo('2026-10-05T11:59:40Z', now)).toBe('now')
  expect(timeAgo('2026-10-05T11:55:00Z', now)).toBe('5m')
  expect(timeAgo('2026-10-05T09:00:00Z', now)).toBe('3h')
  expect(timeAgo('2026-10-03T12:00:00Z', now)).toBe('2d')
})
