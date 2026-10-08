import { useEffect, useRef, useState, type RefObject } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { listQueueTypes } from '../api/queuePrefs'
import type { TicketFilters } from '../api/tickets'
import { PRIORITIES, STATUSES } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useDirectory } from '../hooks/useDirectory'
import { ACTIVE_STATUSES, withParam, type Mode } from '../lib/filters'
import { useDebounced } from '../lib/useDebounced'
import { Icon } from './icons'
import ViewsMenu from './ViewsMenu'

/** Toolbar over the queue list. All state lives in the URL query string. */
export default function QueueFilters({
  mode,
  searchRef,
  filters,
}: {
  mode: Mode
  searchRef: RefObject<HTMLInputElement | null>
  filters: TicketFilters
}) {
  const me = useMe()
  const [params, setParams] = useSearchParams()
  const [viewsOpen, setViewsOpen] = useState(false)
  const departments = me.memberships.filter((m) => m.permissions.includes('tickets.read'))
  const deptParam = params.get('dept') ?? ''
  const scopeDept = deptParam || (departments.length === 1 ? departments[0]!.departmentId : undefined)
  const { members } = useDirectory(scopeDept)
  const types = useQuery({ queryKey: ['queue-types', scopeDept], queryFn: () => listQueueTypes(scopeDept!), enabled: !!scopeDept, staleTime: 60_000, retry: false })

  // The search box edits local text; the URL (and so the query) follows after a short pause.
  const urlQ = params.get('q') ?? ''
  const [text, setText] = useState(urlQ)
  const debounced = useDebounced(text, 300)
  const lastUrlQ = useRef(urlQ)
  useEffect(() => {
    if (urlQ !== lastUrlQ.current) {
      lastUrlQ.current = urlQ
      setText(urlQ)
    }
  }, [urlQ])
  useEffect(() => {
    if (debounced === lastUrlQ.current) return
    lastUrlQ.current = debounced
    setParams((p) => withParam(p, 'q', debounced || null), { replace: true })
  }, [debounced, setParams])

  // A Type that no longer exists (for example in an old saved view) is dropped from the filter instead of silently showing nothing.
  const typeParam = params.get('type')
  useEffect(() => {
    if (typeParam && types.isSuccess && !types.data.some((t) => t.id === typeParam)) setParams((p) => withParam(p, 'type', null), { replace: true })
  }, [typeParam, types.isSuccess, types.data, setParams])

  const defaultStatuses = mode === 'search' ? STATUSES : ACTIVE_STATUSES
  const rawStatus = params.get('status')
  const selected = new Set<string>(rawStatus === null ? defaultStatuses : rawStatus === '' ? [] : rawStatus.split(',').map((s) => s.trim()))
  function toggleStatus(s: string) {
    const next = new Set(selected)
    if (next.has(s)) next.delete(s)
    else next.add(s)
    const ordered = STATUSES.filter((x) => next.has(x))
    setParams((p) => withParam(p, 'status', ordered.join(',')), { replace: true })
  }
  const set = (key: string, value: string) => setParams((p) => withParam(p, key, value || null), { replace: true })

  return (
    <div className="filters" role="search" aria-label="Filter tickets">
      <div className="filters-row">
        <div className="search-box">
          <Icon name="search" />
          <input
            ref={searchRef}
            type="search"
            aria-label="Search tickets"
            placeholder={mode === 'search' ? 'Reference, subject, requester, message text…' : 'Search reference or subject  ( / )'}
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
        </div>
        <button type="button" className="views-btn" onClick={() => setViewsOpen(true)} aria-haspopup="dialog">
          <Icon name="views" /> Views
        </button>
      </div>
      <div className="filters-row">
        <div className="chips" role="group" aria-label="Status">
          {STATUSES.map((s) => (
            <button key={s} type="button" className="chip" aria-pressed={selected.has(s)} onClick={() => toggleStatus(s)}>
              {s}
            </button>
          ))}
        </div>
      </div>
      <div className="queue-sort">        <label className="fsel">
          <span>Sort</span>
          <select value={params.get('sort') ?? 'updated_desc'} onChange={(e) => set('sort', e.target.value === 'updated_desc' ? '' : e.target.value)}>
            <option value="updated_desc">Newest update</option>
            <option value="updated_asc">Oldest update</option>
            <option value="priority_desc">Priority</option>
            <option value="sla_due_asc">SLA due soonest</option>
          </select>
        </label></div>
      <details className="queue-filter-details">
        <summary>Filters{['priority', 'sla', 'dept', 'type', 'send', 'assignee'].filter(k => params.get(k)).length > 0 ? ` · ${['priority', 'sla', 'dept', 'type', 'send', 'assignee'].filter(k => params.get(k)).length} active` : ''}</summary>
      <div className="filters-row selects">
        <label className="fsel">
          <span>Priority</span>
          <select value={params.get('priority') ?? ''} onChange={(e) => set('priority', e.target.value)}>
            <option value="">Any</option>
            {PRIORITIES.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </select>
        </label>

        <label className="fsel">
          <span>Service level</span>
          <select value={params.get('sla') ?? ''} onChange={(e) => set('sla', e.target.value)}>
            <option value="">Any</option>
            <option value="breached">Breached</option>
            <option value="approaching">Near deadline (policy warning)</option>
            <option value="duesoon">Due within 4 hours (fixed window)</option>
            <option value="paused">Paused</option>
            <option value="awaitingresponse">Awaiting first response (no human reply yet)</option>
            <option value="slafirstresponse">SLA first response pending</option>
          </select>
        </label>
        {departments.length > 1 && (
          <label className="fsel">
            <span>Department</span>
            <select
              value={deptParam}
              onChange={(e) =>
                setParams((p) => withParam(withParam(withParam(p, 'dept', e.target.value || null), 'assignee', null), 'type', null), { replace: true })
              }
            >
              <option value="">All</option>
              {departments.map((d) => (
                <option key={d.departmentId} value={d.departmentId}>
                  {d.departmentName}
                </option>
              ))}
            </select>
          </label>
        )}
        {scopeDept && (types.data?.length ?? 0) > 0 && (
          <label className="fsel">
            <span>Type</span>
            <select value={params.get('type') ?? ''} onChange={(e) => set('type', e.target.value)}>
              <option value="">Any</option>
              {types.data!.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="fsel">
          <span>Email sending</span>
          <select value={params.get('send') ?? ''} onChange={(e) => set('send', e.target.value)}>
            <option value="">Any</option>
            <option value="failed">Reply not sent</option>
            <option value="uncertain">Send outcome uncertain</option>
          </select>
        </label>
        <label className="fsel">
          <span>Assignee</span>
          <select value={params.get('assignee') ?? ''} onChange={(e) => set('assignee', e.target.value)}>
            <option value="">Anyone</option>
            <option value="none">Unassigned</option>
            <option value="me">Me</option>
            {members
              .filter((m) => m.userId !== me.id)
              .map((m) => (
                <option key={m.userId} value={m.userId}>
                  {m.displayName}
                </option>
              ))}
          </select>
        </label>
      </div>
      </details>
      {viewsOpen && <ViewsMenu filters={filters} onClose={() => setViewsOpen(false)} />}
    </div>
  )
}
