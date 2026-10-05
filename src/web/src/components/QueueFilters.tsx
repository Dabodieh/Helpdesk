import { useEffect, useRef, useState, type RefObject } from 'react'
import { useSearchParams } from 'react-router-dom'
import { PRIORITIES, SORTS, STATUSES, type Sort } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useDirectory } from '../hooks/useDirectory'
import { ACTIVE_STATUSES, withParam, type Mode } from '../lib/filters'
import { useDebounced } from '../lib/useDebounced'
import { Icon } from './icons'

const SORT_LABELS: Record<Sort, string> = {
  updated_desc: 'Recently updated',
  updated_asc: 'Least recently updated',
  created_desc: 'Newest',
  created_asc: 'Oldest',
  priority_desc: 'Priority',
}

/** Toolbar over the queue list. All state lives in the URL query string. */
export default function QueueFilters({ mode, searchRef }: { mode: Mode; searchRef: RefObject<HTMLInputElement | null> }) {
  const me = useMe()
  const [params, setParams] = useSearchParams()
  const departments = me.memberships.filter((m) => m.permissions.includes('tickets.read'))
  const deptParam = params.get('dept') ?? ''
  const scopeDept = deptParam || (departments.length === 1 ? departments[0]!.departmentId : undefined)
  const { teams, members } = useDirectory(scopeDept)

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

  const defaultStatuses = mode === 'search' ? STATUSES : ACTIVE_STATUSES
  const rawStatus = params.get('status')
  const selected = new Set<string>(
    rawStatus === null ? defaultStatuses : rawStatus === '' ? [] : rawStatus.split(',').map((s) => s.trim()),
  )
  function toggleStatus(s: string) {
    const next = new Set(selected)
    if (next.has(s)) next.delete(s)
    else next.add(s)
    const ordered = STATUSES.filter((x) => next.has(x))
    setParams((p) => withParam(p, 'status', ordered.join(',')), { replace: true })
  }
  const set = (key: string, value: string) => setParams((p) => withParam(p, key, value || null), { replace: true })
  const needsDept = departments.length > 1 && !scopeDept

  return (
    <div className="filters" role="search" aria-label="Filter tickets">
      <div className="search-box">
        <Icon name="search" />
        <input
          ref={searchRef}
          type="search"
          aria-label="Search tickets"
          placeholder={mode === 'search' ? 'Reference or subject (2+ characters)' : 'Search reference or subject  ( / )'}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
      </div>
      <div className="chips" role="group" aria-label="Status">
        {STATUSES.map((s) => (
          <button key={s} type="button" className="chip" aria-pressed={selected.has(s)} onClick={() => toggleStatus(s)}>
            {s}
          </button>
        ))}
      </div>
      <label className="fsel">
        <span>Priority</span>
        <select value={params.get('priority') ?? ''} onChange={(e) => set('priority', e.target.value)}>
          <option value="">Any</option>
          {PRIORITIES.map((p) => (
            <option key={p}>{p}</option>
          ))}
        </select>
      </label>
      {departments.length > 1 && (
        <label className="fsel">
          <span>Department</span>
          <select
            value={deptParam}
            onChange={(e) =>
              setParams((p) => withParam(withParam(withParam(p, 'dept', e.target.value || null), 'team', null), 'assignee', null), {
                replace: true,
              })
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
      <label className="fsel">
        <span>Team</span>
        <select
          value={params.get('team') ?? ''}
          disabled={needsDept}
          title={needsDept ? 'Choose a department first' : undefined}
          onChange={(e) => set('team', e.target.value)}
        >
          <option value="">Any</option>
          {teams.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
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
      <label className="fsel">
        <span>Sort</span>
        <select value={params.get('sort') ?? ''} onChange={(e) => set('sort', e.target.value)}>
          <option value="">{SORT_LABELS.updated_desc}</option>
          {SORTS.filter((s) => s !== 'updated_desc').map((s) => (
            <option key={s} value={s}>
              {SORT_LABELS[s]}
            </option>
          ))}
        </select>
      </label>
    </div>
  )
}
