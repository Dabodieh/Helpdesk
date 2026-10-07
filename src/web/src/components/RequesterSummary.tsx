import { Link } from 'react-router-dom'
import type { TicketDetail } from '../api/ticketSchemas'

/** Who asked, how to reach them and how much history they have (counts come from the ticket and are limited to what the caller may read). */
export default function RequesterSummary({ ticket: t }: { ticket: TicketDetail }) {
  return (
    <section className="prop-section" aria-label="Requester">
      <h3>Requester</h3>
      <p className="requester-name">{t.requester.displayName}</p>
      <p className="muted small">{t.requester.email}</p>
      <p className="requester-counts small">
        <strong>{t.requester.openTicketCount}</strong> open · <strong>{t.requester.totalTicketCount}</strong> total ·{' '}
        <Link to={`/requesters/${encodeURIComponent(t.requester.id)}`}>View history</Link>
      </p>
      {t.participants.length > 0 && <p className="muted small">Cc: {t.participants.map((p) => p.displayName || p.email).join(', ')}</p>}
    </section>
  )
}
