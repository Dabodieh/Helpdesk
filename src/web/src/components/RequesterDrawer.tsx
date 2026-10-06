import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { getRequester } from '../api/requesters'
import type { TicketListItem } from '../api/ticketSchemas'
import { timeAgo } from '../lib/format'
import { ErrorNotice, Loading } from './common'
import Dialog from './Dialog'
import { StatusPill } from './Pills'

/** One historical ticket: reference, subject, status, department and the last visible update. Opens in the normal workspace. */
export function HistoryRow({ ticket, onNavigate }: { ticket: TicketListItem; onNavigate?: () => void }) {
  return (
    <li className="history-row">
      <Link to={`/tickets/${encodeURIComponent(ticket.reference)}`} onClick={onNavigate}>
        <span className="ref">{ticket.reference}</span> <span className="history-subject">{ticket.subject}</span>
      </Link>
      <span className="history-meta">
        {ticket.mergedInto ? <span className="badge off">Merged into {ticket.mergedInto}</span> : <StatusPill status={ticket.status} />}
        <span className="muted small">
          {ticket.departmentKey} · {ticket.assignee?.displayName ?? 'Unassigned'} · {timeAgo(ticket.updatedAt)}
        </span>
      </span>
    </li>
  )
}

export function HistoryGroup({ title, tickets, onNavigate }: { title: string; tickets: TicketListItem[]; onNavigate?: () => void }) {
  if (tickets.length === 0) return null
  return (
    <section className="history-group" aria-label={title}>
      <h3>{title}</h3>
      <ul className="history-list">
        {tickets.map((t) => (
          <HistoryRow key={t.id} ticket={t} onNavigate={onNavigate} />
        ))}
      </ul>
    </section>
  )
}

/** A requester's name as a quiet button that opens the quick-context drawer. */
export function RequesterName({ requesterId, name, className }: { requesterId: string; name: string; className?: string }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button type="button" className={`requester-link ${className ?? ''}`} onClick={() => setOpen(true)} aria-haspopup="dialog" title="Show requester history">
        {name}
      </button>
      {open && <RequesterDrawer requesterId={requesterId} onClose={() => setOpen(false)} />}
    </>
  )
}

function RequesterDrawer({ requesterId, onClose }: { requesterId: string; onClose: () => void }) {
  const q = useQuery({ queryKey: ['requester', requesterId], queryFn: () => getRequester(requesterId) })
  const r = q.data
  return (
    <Dialog title={r?.displayName ?? 'Requester'} onClose={onClose} variant="drawer">
      {q.isPending && <Loading label="Loading requester" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {r && (
        <div className="requester-card">
          <p className="muted">{r.email}</p>
          <p className="requester-counts">
            <strong>{r.openCount}</strong> open · <strong>{r.totalCount}</strong> total
          </p>
          {r.totalCount <= 1 && <p className="muted">No other requests from this person.</p>}
          <HistoryGroup title="Open" tickets={r.open} onNavigate={onClose} />
          <HistoryGroup title="Previous" tickets={r.previous} onNavigate={onClose} />
          <p>
            <Link to={`/requesters/${encodeURIComponent(r.id)}`} onClick={onClose}>
              View full history
            </Link>
          </p>
        </div>
      )}
    </Dialog>
  )
}
