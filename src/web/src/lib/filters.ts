import type { TicketFilters } from '../api/tickets'
import { PRIORITIES, QUEUES, SORTS, STATUSES, type Priority, type Queue, type Sort, type Status } from '../api/ticketSchemas'
import type { ViewFilter } from '../api/views'

export type Mode = 'queue' | 'search' | 'ticket'

/** Agent work in progress. The queue default (the server default would also include Resolved). */
export const ACTIVE_STATUSES: Status[] = ['Open', 'Pending']
export const DEFAULT_SORT: Sort = 'updated_desc'

const list = <T extends string>(raw: string | null, allowed: readonly T[]): T[] =>
  (raw ?? '')
    .split(',')
    .map((s) => allowed.find((a) => a.toLowerCase() === s.trim().toLowerCase()))
    .filter((v): v is T => v !== undefined)

/**
 * Filters live in the URL so a reload or a shared link restores the view.
 * `status` absent = default (Open+Pending; every status in search). `status=` (empty) = every status.
 * `assignee` is a user id, `me` or `none` (unassigned).
 */
export function parseFilters(queue: Queue, params: URLSearchParams, mode: Mode, meId: string): TicketFilters {
  const statusParam = params.get('status')
  const statuses =
    statusParam === null
      ? mode === 'search'
        ? [...STATUSES]
        : ACTIVE_STATUSES
      : statusParam === ''
        ? [...STATUSES]
        : list<Status>(statusParam, STATUSES)
  const assignee = params.get('assignee')
  const q = (params.get('q') ?? '').trim()
  const sort = SORTS.find((s) => s === params.get('sort')) ?? DEFAULT_SORT
  return {
    queue,
    statuses: statuses.length ? statuses : ACTIVE_STATUSES,
    priorities: list<Priority>(params.get('priority'), PRIORITIES),
    departmentId: params.get('dept') || undefined,
    teamId: params.get('team') || undefined,
    assigneeUserId: assignee && assignee !== 'none' ? (assignee === 'me' ? meId : assignee) : undefined,
    unassigned: assignee === 'none' ? true : undefined,
    q: q.length >= 2 ? q : undefined,
    sort,
  }
}

/** Is any filter different from the defaults for this mode? (drives "Reset filters") */
export function hasCustomFilters(params: URLSearchParams) {
  return ['status', 'priority', 'dept', 'team', 'assignee', 'q', 'sort', 'view'].some((k) => params.has(k))
}

/** Set or clear one URL parameter. */
export function withParam(params: URLSearchParams, key: string, value: string | null) {
  const next = new URLSearchParams(params)
  if (value === null) next.delete(key)
  else next.set(key, value)
  return next
}

export const QUEUE_TITLES: Record<Queue, string> = {
  my: 'My Queue',
  team: 'Team Queue',
  unassigned: 'Unassigned',
  all: 'All Tickets',
}

/** The structured filter a saved view stores, from the filters the queue is showing. */
export function toViewFilter(f: TicketFilters): ViewFilter {
  return {
    queue: f.queue,
    departmentId: f.departmentId ?? null,
    teamId: f.teamId ?? null,
    assigneeUserId: f.assigneeUserId ?? null,
    unassigned: f.unassigned ?? null,
    status: f.statuses,
    priority: f.priorities.length > 0 ? f.priorities : null,
    q: f.q ?? null,
    sort: f.sort,
  }
}

/** Where a saved view opens: the normal queue URL carrying its filters (the server re-checks scope on every list call). */
export function viewLocation(v: { id: string; filter: ViewFilter }): string {
  const f = v.filter
  const queue = (QUEUES as readonly string[]).includes(f.queue ?? '') ? f.queue! : 'all'
  const p = new URLSearchParams()
  p.set('status', (f.status ?? []).join(','))
  if (f.priority?.length) p.set('priority', f.priority.join(','))
  if (f.departmentId) p.set('dept', f.departmentId)
  if (f.teamId) p.set('team', f.teamId)
  if (f.unassigned) p.set('assignee', 'none')
  else if (f.assigneeUserId) p.set('assignee', f.assigneeUserId)
  if (f.q) p.set('q', f.q)
  if (f.sort && f.sort !== DEFAULT_SORT) p.set('sort', f.sort)
  p.set('view', v.id)
  return `/queue/${queue}?${p.toString()}`
}
