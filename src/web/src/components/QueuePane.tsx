import { useInfiniteQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { listTickets } from '../api/tickets'
import type { Queue, TicketListItem } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { formatDateTime, timeAgo } from '../lib/format'
import { hasCustomFilters, parseFilters, QUEUE_TITLES, type Mode } from '../lib/filters'
import { isActivatable, useShortcuts } from '../lib/shortcuts'
import { livePolling } from '../queries'
import { ErrorNotice } from './common'
import { PriorityMark, StatusPill } from './Pills'
import QueueFilters from './QueueFilters'

type Props = {
  queue: Queue
  mode: Mode
  /** Reference or id of the open ticket (from the URL). */
  selected?: string
  /** Where a ticket opens (keeps the filter query string). */
  hrefFor: (t: TicketListItem) => string
}

export default function QueuePane({ queue, mode, selected, hrefFor }: Props) {
  const me = useMe()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const searchRef = useRef<HTMLInputElement>(null)
  const filters = useMemo(() => parseFilters(queue, params, mode, me.id), [queue, params, mode, me.id])
  const enabled = mode !== 'search' || !!filters.q

  const list = useInfiniteQuery({
    queryKey: ['tickets', 'list', filters],
    queryFn: ({ pageParam }) => listTickets(filters, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    enabled,
    ...livePolling,
  })

  const items = useMemo(() => {
    const seen = new Map<string, TicketListItem>()
    for (const page of list.data?.pages ?? []) for (const t of page.items) if (!seen.has(t.id)) seen.set(t.id, t)
    return [...seen.values()]
  }, [list.data])

  const isSelected = useCallback(
    (t: TicketListItem) =>
      !!selected && (t.id === selected || t.reference.toLowerCase() === selected.toLowerCase()),
    [selected],
  )

  // Keyboard cursor, tracked by ticket id so it survives refreshes. Falls back to the open ticket.
  const [cursorId, setCursorId] = useState<string | null>(null)
  const cursor =
    items.find((t) => t.id === cursorId) ?? items.find((t) => isSelected(t)) ?? null
  const cursorIndex = cursor ? items.indexOf(cursor) : -1
  const rowRefs = useRef(new Map<string, HTMLTableRowElement>())

  function moveCursor(delta: 1 | -1) {
    if (items.length === 0) return
    const next = Math.min(items.length - 1, Math.max(0, cursorIndex < 0 ? 0 : cursorIndex + delta))
    const t = items[next]!
    setCursorId(t.id)
    rowRefs.current.get(t.id)?.scrollIntoView?.({ block: 'nearest' })
    if (next >= items.length - 3 && list.hasNextPage && !list.isFetchingNextPage) void list.fetchNextPage()
  }

  useShortcuts({
    j: (e) => {
      e.preventDefault()
      moveCursor(1)
    },
    ArrowDown: (e) => {
      e.preventDefault()
      moveCursor(1)
    },
    k: (e) => {
      e.preventDefault()
      moveCursor(-1)
    },
    ArrowUp: (e) => {
      e.preventDefault()
      moveCursor(-1)
    },
    Enter: (e) => {
      if (isActivatable(e) || !cursor) return
      e.preventDefault()
      void navigate(hrefFor(cursor))
    },
    '/': (e) => {
      e.preventDefault()
      searchRef.current?.focus()
    },
  })

  // Load the next page automatically when the end of the list scrolls into view.
  const sentinel = useRef<HTMLDivElement>(null)
  const { hasNextPage, isFetchingNextPage, fetchNextPage } = list
  useEffect(() => {
    const el = sentinel.current
    if (!el || typeof IntersectionObserver === 'undefined') return
    const io = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting) && hasNextPage && !isFetchingNextPage) void fetchNextPage()
    })
    io.observe(el)
    return () => io.disconnect()
  }, [hasNextPage, isFetchingNextPage, fetchNextPage])

  const title = mode === 'search' ? 'Search' : mode === 'ticket' ? 'Tickets' : QUEUE_TITLES[queue]
  const custom = hasCustomFilters(params)

  return (
    <section className="queue-pane" aria-label={title}>
      <div className="pane-head">
        <h1>{title}</h1>
        <span className="muted small" role="status">
          {list.isSuccess ? `${items.length}${list.hasNextPage ? '+' : ''} shown` : ''}
        </span>
      </div>
      <QueueFilters mode={mode} searchRef={searchRef} />
      <div className="queue-scroll" id="queue-list" tabIndex={-1}>
        {!enabled && <p className="empty">Type at least 2 characters of a reference or subject to search all statuses.</p>}
        {enabled && list.isPending && <QueueSkeleton />}
        {enabled && list.isError && (
          <div className="pad">
            <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
          </div>
        )}
        {enabled && list.isSuccess && items.length === 0 && (
          <div className="empty">
            <p>No tickets here.</p>
            {custom ? (
              <button type="button" onClick={() => setParams(new URLSearchParams(), { replace: true })}>
                Reset filters
              </button>
            ) : (
              <p className="muted small">New tickets appear automatically.</p>
            )}
          </div>
        )}
        {items.length > 0 && (
          <table className="qtable">
            <caption className="sr-only">{title}</caption>
            <thead>
              <tr>
                <th scope="col" className="col-ref">Ref</th>
                <th scope="col" className="col-subject">Subject</th>
                <th scope="col" className="col-requester">Requester</th>
                <th scope="col" className="col-status">Status</th>
                <th scope="col" className="col-prio">Priority</th>
                <th scope="col" className="col-team">Team</th>
                <th scope="col" className="col-assignee">Assignee</th>
                <th scope="col" className="col-age">Updated</th>
              </tr>
            </thead>
            <tbody>
              {items.map((t) => {
                const open = isSelected(t)
                return (
                  <tr
                    key={t.id}
                    ref={(el) => {
                      if (el) rowRefs.current.set(t.id, el)
                      else rowRefs.current.delete(t.id)
                    }}
                    data-selected={open || undefined}
                    data-cursor={cursor?.id === t.id || undefined}
                    onClick={(e) => {
                      if ((e.target as HTMLElement).closest('a')) return
                      setCursorId(t.id)
                      void navigate(hrefFor(t))
                    }}
                  >
                    <td className="col-ref ref">{t.reference}</td>
                    <td className="col-subject">
                      <Link
                        to={hrefFor(t)}
                        className="row-link"
                        aria-current={open ? 'true' : undefined}
                        onClick={() => setCursorId(t.id)}
                      >
                        {t.subject}
                      </Link>
                      <span className="subline">
                        {t.requester.displayName} · {t.team?.name ?? 'No team'} · {t.assignee?.displayName ?? 'Unassigned'}
                      </span>
                    </td>
                    <td className="col-requester">{t.requester.displayName}</td>
                    <td className="col-status">
                      <StatusPill status={t.status} />
                    </td>
                    <td className="col-prio">
                      <PriorityMark priority={t.priority} />
                    </td>
                    <td className="col-team">{t.team?.name ?? <span className="muted">None</span>}</td>
                    <td className="col-assignee">{t.assignee?.displayName ?? <span className="muted">Unassigned</span>}</td>
                    <td className="col-age" title={formatDateTime(t.updatedAt)}>
                      {timeAgo(t.updatedAt)}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
        {items.length > 0 && (
          <div ref={sentinel} className="more">
            {list.hasNextPage ? (
              <button type="button" disabled={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>
                {list.isFetchingNextPage ? 'Loading…' : 'Load more'}
              </button>
            ) : (
              <span className="muted small">End of list</span>
            )}
          </div>
        )}
      </div>
    </section>
  )
}

function QueueSkeleton() {
  return (
    <div role="status" aria-label="Loading tickets" className="skeleton">
      {Array.from({ length: 10 }, (_, i) => (
        <div key={i} className="sk-row" />
      ))}
    </div>
  )
}
