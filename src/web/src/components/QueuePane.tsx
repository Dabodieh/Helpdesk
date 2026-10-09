import { useInfiniteQuery } from '@tanstack/react-query'
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { listTickets } from '../api/tickets'
import type { QueueColumn } from '../api/queuePrefs'
import type { Queue, TicketListItem } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { formatDateTime, timeAgo } from '../lib/format'
import { DEFAULT_SORT, hasCustomFilters, parseFilters, QUEUE_TITLES, withParam, type Mode } from '../lib/filters'
import { useQueuePrefs, visibleColumns } from '../hooks/useQueuePrefs'
import { hasDraft } from '../lib/drafts'
import { usePref } from '../lib/prefs'
import { readScroll, saveQueueContext, saveScroll } from '../lib/queueContext'
import { isActivatable, useShortcuts } from '../lib/shortcuts'
import { livePolling } from '../queries'
import BulkBar from './BulkBar'
import { ErrorNotice } from './common'
import { Icon } from './icons'
import { SlaBadge } from './Sla'
import { PriorityMark, StatusPill } from './Pills'
import QueueFilters from './QueueFilters'
import QueuePrefsMenu from './QueuePrefsMenu'
import { RequesterName } from './RequesterDrawer'

type Props = {
  queue: Queue
  mode: Mode
  /** Reference or id of the open ticket (from the URL). */
  selected?: string
  /** Where a ticket opens (keeps the filter query string). */
  hrefFor: (t: TicketListItem) => string
}

/** Highlights the search text inside plain text. Built from React nodes (never HTML), so a snippet cannot inject markup. */
export function Highlight({ text, term }: { text: string; term?: string }) {
  if (!term) return <>{text}</>
  const lower = text.toLowerCase()
  const needle = term.toLowerCase()
  const parts: React.ReactNode[] = []
  let from = 0
  for (let at = lower.indexOf(needle); at >= 0 && parts.length < 20; at = lower.indexOf(needle, from)) {
    if (at > from) parts.push(text.slice(from, at))
    parts.push(<mark key={at}>{text.slice(at, at + needle.length)}</mark>)
    from = at + needle.length
  }
  parts.push(text.slice(from))
  return (
    <>
      {parts.map((p, i) => (
        <Fragment key={i}>{p}</Fragment>
      ))}
    </>
  )
}

const MATCH_LABEL: Record<string, string> = {
  reference: 'Reference',
  subject: 'Subject',
  requester: 'Requester',
  message: 'Message',
  note: 'Internal note',
}

export default function QueuePane({ queue, mode, selected, hrefFor }: Props) {
  const me = useMe()
  const navigate = useNavigate()
  const location = useLocation()
  const [params, setParams] = useSearchParams()
  const searchRef = useRef<HTMLInputElement>(null)
  const filters = useMemo(() => parseFilters(queue, params, mode, me.id), [queue, params, mode, me.id])
  const enabled = mode !== 'search' || !!filters.q
  const { prefs } = useQueuePrefs()
  const [prefsOpen, setPrefsOpen] = useState(false)
  const [density, setDensity] = usePref('helpdesk.density', ['comfortable', 'compact'] as const, 'comfortable')

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

  // Remember this list (its address with filters and sort, and the order of what is loaded) so a ticket page can go back to it and step to the previous/next ticket.
  const backTo = `${location.pathname}${location.search}`
  const scrollRef = useRef<HTMLDivElement>(null)
  const restored = useRef(false)
  useEffect(() => {
    if (items.length > 0) saveQueueContext({ backTo, refs: items.map((t) => t.reference) })
  }, [items, backTo])
  useEffect(() => {
    const el = scrollRef.current
    if (!el || restored.current || items.length === 0) return
    restored.current = true
    const top = readScroll(backTo)
    if (top > 0) el.scrollTop = top
  }, [items, backTo])

  const isSelected = useCallback(
    (t: TicketListItem) => !!selected && (t.id === selected || t.reference.toLowerCase() === selected.toLowerCase()),
    [selected],
  )

  // Bulk selection (checkboxes). Cleared when the filters change, and never keeps ids that left the list.
  const filterKey = JSON.stringify(filters)
  const [selection, setSelection] = useState<{ key: string; ids: Set<string> }>({ key: filterKey, ids: new Set() })
  const checked = selection.key === filterKey ? selection.ids : new Set<string>()
  const setChecked = (ids: Set<string>) => setSelection({ key: filterKey, ids })
  const checkedItems = items.filter((t) => checked.has(t.id))
  const toggleChecked = (id: string) => {
    const next = new Set(checked)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setChecked(next)
  }

  // Keyboard cursor, tracked by ticket id so it survives refreshes. Falls back to the open ticket.
  const [cursorId, setCursorId] = useState<string | null>(null)
  const cursor = items.find((t) => t.id === cursorId) ?? items.find((t) => isSelected(t)) ?? null
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
    x: (e) => {
      if (!cursor) return
      e.preventDefault()
      toggleChecked(cursor.id)
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
  const sort = filters.sort
  const setSort = (next: string) => setParams((p) => withParam(p, 'sort', next === DEFAULT_SORT ? null : next), { replace: true })
  const allChecked = items.length > 0 && items.every((t) => checked.has(t.id))
  const columns = visibleColumns(prefs)
  const sortHeader = (cls: string, label: string, on: boolean, dir: 'ascending' | 'descending' | 'none', arrow: string, toggle: () => void) => (
    <th scope="col" className={cls} aria-sort={dir}>
      <button type="button" className="th-sort" onClick={toggle}>
        {label} {on && <span aria-hidden="true">{arrow}</span>}
      </button>
    </th>
  )
  const headerCell = (c: QueueColumn) => {
    switch (c) {
      case 'requester':
        return <th scope="col" className="col-requester">Requester</th>
      case 'status':
        return <th scope="col" className="col-status">Status</th>
      case 'priority':
        return sortHeader('col-prio', 'Priority', sort === 'priority_desc', sort === 'priority_desc' ? 'descending' : 'none', '↓', () => setSort(sort === 'priority_desc' ? DEFAULT_SORT : 'priority_desc'))
      case 'sla':
        return sortHeader('col-sla', 'SLA', sort === 'sla_due_asc', sort === 'sla_due_asc' ? 'ascending' : 'none', '↑', () => setSort(sort === 'sla_due_asc' ? DEFAULT_SORT : 'sla_due_asc'))
      case 'assignee':
        return <th scope="col" className="col-assignee">Assignee</th>
      case 'updated':
        return sortHeader(
          'col-age',
          'Updated',
          sort === 'updated_desc' || sort === 'updated_asc',
          sort === 'updated_desc' ? 'descending' : sort === 'updated_asc' ? 'ascending' : 'none',
          sort === 'updated_desc' ? '↓' : '↑',
          () => setSort(sort === 'updated_desc' ? 'updated_asc' : 'updated_desc'),
        )
    }
  }
  const bodyCell = (c: QueueColumn, t: TicketListItem) => {
    switch (c) {
      case 'requester':
        return (
          <td className="col-requester">
            <RequesterName requesterId={t.requester.id} name={t.requester.displayName} />
          </td>
        )
      case 'status':
        return (
          <td className="col-status">
            {t.mergedInto ? <span className="badge off">Merged into {t.mergedInto}</span> : <StatusPill status={t.status} />}
            {!t.mergedInto && (t.status === 'Open' || t.status === 'Pending') && t.lastPublicBy === 'Requester' && (
              <span className="turn turn-us" title="The requester wrote last: waiting for us">
                Requester replied
              </span>
            )}
            {!t.mergedInto && (t.status === 'Open' || t.status === 'Pending') && t.lastPublicBy === 'Agent' && (
              <span className="turn turn-them" title="We replied last: waiting for the requester">
                We replied
              </span>
            )}
            <span className="sla-inline">
              <SlaBadge sla={t.sla} />
            </span>
          </td>
        )
      case 'priority':
        return (
          <td className="col-prio">
            <PriorityMark priority={t.priority} />
          </td>
        )
      case 'sla':
        return (
          <td className="col-sla">
            <SlaBadge sla={t.sla} />
          </td>
        )
      case 'assignee':
        return <td className="col-assignee">{t.assignee?.displayName ?? <span className="muted">Unassigned</span>}</td>
      case 'updated':
        return (
          <td className="col-age" title={formatDateTime(t.updatedAt)}>
            {timeAgo(t.updatedAt)}
          </td>
        )
    }
  }

  return (
    <section className="queue-pane" aria-label={title} data-density={density}>
      <div className="pane-head">
        <h1>{title}</h1>
        <span className="muted small count-note" role="status">
          {list.isSuccess ? `${items.length}${list.hasNextPage ? '+' : ''} ${items.length === 1 ? 'ticket' : 'tickets'}` : ''}
        </span>
        <button
          type="button"
          className="ghost icon-btn"
          aria-pressed={density === 'compact'}
          title={density === 'compact' ? 'Switch to comfortable rows' : 'Switch to compact rows'}
          onClick={() => setDensity(density === 'compact' ? 'comfortable' : 'compact')}
        >
          <Icon name="density" />
          <span className="sr-only">{density === 'compact' ? 'Comfortable rows' : 'Compact rows'}</span>
        </button>
        {mode === 'queue' && (
          <button type="button" className="ghost" title="Columns & default" aria-haspopup="dialog" onClick={() => setPrefsOpen(true)}>
            <Icon name="views" /><span className="sr-only">Columns &amp; default</span>
          </button>
        )}
      </div>
      {prefsOpen && <QueuePrefsMenu queue={queue} onClose={() => setPrefsOpen(false)} />}
      <QueueFilters mode={mode} searchRef={searchRef} filters={filters} />
      {checkedItems.length > 0 && <BulkBar tickets={checkedItems} onClear={() => setChecked(new Set())} />}
      <div className="queue-scroll" id="queue-list" tabIndex={-1} ref={scrollRef} onScroll={(e) => saveScroll(backTo, e.currentTarget.scrollTop)}>
        {!enabled && (
          <div className="empty">
            <Icon name="search" size={28} />
            <p>Search tickets.</p>
            <p className="muted small">Type at least 2 characters.</p>
          </div>
        )}
        {enabled && list.isPending && <QueueSkeleton />}
        {enabled && list.isError && (
          <div className="pad">
            <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
          </div>
        )}
        {enabled && list.isSuccess && items.length === 0 && (
          <div className="empty">
            <Icon name="inbox" size={28} />
            <p>{mode === 'search' ? `No tickets match “${filters.q}”.` : 'No tickets here.'}</p>
            {custom ? (
              <button type="button" onClick={() => setParams(new URLSearchParams(), { replace: true })}>
                Reset filters
              </button>
            ) : null}
          </div>
        )}
        {items.length > 0 && (
          <table className="qtable">
            <caption className="sr-only">{title}</caption>
            <thead>
              <tr>
                <th scope="col" className="col-check">
                  <input
                    type="checkbox"
                    aria-label="Select all tickets in this list"
                    checked={allChecked}
                    onChange={() => setChecked(allChecked ? new Set() : new Set(items.map((t) => t.id)))}
                  />
                </th>
                <th scope="col" className="col-ref">
                  Ref
                </th>
                <th scope="col" className="col-subject">
                  Subject
                </th>
                {columns.map((c) => (
                  <Fragment key={c}>{headerCell(c)}</Fragment>
                ))}
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
                    data-checked={checked.has(t.id) || undefined}
                    onClick={(e) => {
                      if ((e.target as HTMLElement).closest('a,input,button')) return
                      setCursorId(t.id)
                      void navigate(hrefFor(t))
                    }}
                  >
                    <td className="col-check">
                      <input
                        type="checkbox"
                        aria-label={`Select ${t.reference}`}
                        checked={checked.has(t.id)}
                        onChange={() => toggleChecked(t.id)}
                      />
                    </td>
                    <td className="col-ref ref">{t.reference}</td>
                    <td className="col-subject">
                      <Link to={hrefFor(t)} className="row-link" aria-current={open ? 'true' : undefined} onClick={() => setCursorId(t.id)}>
                        <Highlight text={t.subject} term={t.match?.field === 'subject' ? filters.q : undefined} />
                      </Link>
                      <span className="subline">
                        {t.requester.displayName} · {t.assignee?.displayName ?? 'Unassigned'}
                        {hasDraft(t.id) && <span className="draft-tag" title="You have an unsent reply or note on this ticket">Draft</span>}
                      </span>
                      {t.match && t.match.field !== 'subject' && (
                        <span className="match">
                          <span className="match-field">{MATCH_LABEL[t.match.field] ?? t.match.field}</span>
                          {t.match.snippet ? <Highlight text={t.match.snippet} term={filters.q} /> : null}
                        </span>
                      )}
                    </td>
                    {columns.map((c) => (
                      <Fragment key={c}>{bodyCell(c, t)}</Fragment>
                    ))}
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
