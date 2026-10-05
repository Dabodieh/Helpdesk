import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { listCanned, type Canned } from '../api/canned'
import { ErrorNotice, Loading } from './common'
import Dialog from './Dialog'
import { Icon } from './icons'

/**
 * Insert a canned response into the composer. Picking one only inserts its text: the agent still reviews and submits it, nothing is
 * ever posted or sent automatically.
 */
export default function CannedPicker({ departmentId, onPick }: { departmentId: string; onPick: (text: string) => void }) {
  const [open, setOpen] = useState(false)
  const [filter, setFilter] = useState('')
  const q = useQuery({ queryKey: ['canned', departmentId], queryFn: () => listCanned({ departmentId }), staleTime: 60_000 })
  const matches = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    const all = q.data ?? []
    return needle ? all.filter((c) => c.title.toLowerCase().includes(needle) || c.body.toLowerCase().includes(needle)) : all
  }, [q.data, filter])

  function choose(c: Canned) {
    onPick(c.body)
    setOpen(false)
    setFilter('')
  }

  return (
    <>
      <button type="button" className="ghost insert-btn" onClick={() => setOpen(true)} aria-haspopup="dialog">
        <Icon name="snippet" /> Insert response
      </button>
      {open && (
        <Dialog title="Insert a canned response" onClose={() => setOpen(false)}>
          <input
            type="search"
            data-autofocus
            aria-label="Filter responses"
            placeholder="Filter by title or text"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && matches[0]) {
                e.preventDefault()
                choose(matches[0])
              }
            }}
          />
          {q.isPending && <Loading label="Loading responses" />}
          {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
          {q.isSuccess && (q.data ?? []).length === 0 && (
            <p className="empty small">
              You have no canned responses yet. <Link to="/responses">Create your own</Link> or ask a department administrator to add shared ones.
            </p>
          )}
          {q.isSuccess && (q.data ?? []).length > 0 && matches.length === 0 && <p className="empty small">Nothing matches “{filter}”.</p>}
          <ul className="canned-list">
            {matches.map((c) => (
              <li key={c.id}>
                <button type="button" onClick={() => choose(c)}>
                  <span className="canned-title">
                    {c.title} <span className="badge">{c.scope === 'Personal' ? 'Personal' : (c.departmentName ?? 'Department')}</span>
                  </span>
                  <span className="canned-preview muted small">{c.body}</span>
                </button>
              </li>
            ))}
          </ul>
          <p className="muted small">Enter inserts the first match. The text is added to your draft; nothing is sent.</p>
        </Dialog>
      )}
    </>
  )
}
