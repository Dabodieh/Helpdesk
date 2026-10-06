import { Link } from 'react-router-dom'
import type { PortalRequestItem } from '../../api/portal'
import { formatDateTime, timeAgo } from '../../lib/format'

const STATUS_MARK: Record<string, string> = { InProgress: '●', Waiting: '◐', Resolved: '✓', Closed: '■' }

/** Requester wording for the status, with a mark so it never relies on colour alone. */
export function StatusBadge({ status, label }: { status: string; label: string }) {
  return (
    <span className={`pstatus pstatus-${status.toLowerCase()}`}>
      <span aria-hidden="true">{STATUS_MARK[status] ?? '●'}</span> {label}
    </span>
  )
}

export function RequestCard({ request: r }: { request: PortalRequestItem }) {
  return (
    <Link to={`/portal/requests/${encodeURIComponent(r.reference)}`} className="request-card">
      <span className="request-top">
        <strong>{r.subject}</strong>
        <StatusBadge status={r.status} label={r.statusLabel} />
      </span>
      <span className="muted small">
        {r.reference} · {r.department} · updated <time dateTime={r.lastActivityAt} title={formatDateTime(r.lastActivityAt)}>{timeAgo(r.lastActivityAt)}</time>
      </span>
    </Link>
  )
}
