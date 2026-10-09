import { useRef } from 'react'
import { Navigate, useLocation, useParams } from 'react-router-dom'
import type { TicketListItem } from '../api/ticketSchemas'
import { QUEUES } from '../api/ticketSchemas'
import type { ComposerHandle } from '../components/Composer'
import QueuePane from '../components/QueuePane'
import TicketPane from '../components/TicketPane'
import type { Mode } from '../lib/filters'
import { useShortcuts } from '../lib/shortcuts'

/**
 * The agent workspace. A queue (or a search) is a full-width list; opening a ticket goes to its own page at `/tickets/:ticket`, which is a stable, shareable URL.
 * The old split URLs (`/queue/:queue/:ticket`, `/search/:ticket`) still work and forward to the ticket page, remembering which list they came from.
 */
export default function WorkspacePage({ mode }: { mode: Mode }) {
  const { queue: queueParam, ticket } = useParams()
  const location = useLocation()
  const composerRef = useRef<ComposerHandle | null>(null)

  useShortcuts({
    r: (e) => {
      if (!composerRef.current) return
      e.preventDefault()
      composerRef.current.focus('Public')
    },
    n: (e) => {
      if (!composerRef.current) return
      e.preventDefault()
      composerRef.current.focus('Internal')
    },
  })

  const queue = QUEUES.find((q) => q === queueParam)
  if (mode === 'queue' && !queue) return <Navigate to="/queue/my" replace />

  const base = mode === 'queue' ? `/queue/${queue}` : '/search'
  if (mode !== 'ticket' && ticket) {
    return <Navigate to={`/tickets/${encodeURIComponent(ticket)}`} replace state={{ backTo: `${base}${location.search}` }} />
  }

  if (mode === 'ticket') {
    return (
      <div className="workspace" data-layout="ticket">
        <section className="ticket-pane" aria-label="Ticket">
          <TicketPane key={ticket!.toLowerCase()} ticketRef={ticket!} composerRef={composerRef} />
        </section>
      </div>
    )
  }

  const hrefFor = (t: TicketListItem) => `/tickets/${encodeURIComponent(t.reference)}`
  return (
    <div className="workspace" data-layout="list">
      <QueuePane queue={mode === 'queue' ? queue! : 'all'} mode={mode} hrefFor={hrefFor} />
    </div>
  )
}
