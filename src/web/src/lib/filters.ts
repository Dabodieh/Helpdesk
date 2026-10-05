import type { TicketFilters } from '../api/tickets'
import { PRIORITIES, SORTS, STATUSES, type Priority, type Queue, type Sort, type Status } from '../api/ticketSchemas'

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
  return ['status', 'priority', 'dept', 'team', 'assignee', 'q', 'sort'].some((k) => params.has(k))
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
