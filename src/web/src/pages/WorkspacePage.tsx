import { useRef } from 'react'
import { Navigate, useLocation, useParams } from 'react-router-dom'
import type { TicketListItem } from '../api/ticketSchemas'
import { QUEUES, type Queue } from '../api/ticketSchemas'
import type { ComposerHandle } from '../components/Composer'
import QueuePane from '../components/QueuePane'
import { useShell } from '../components/ShellContext'
import TicketPane from '../components/TicketPane'
import type { Mode } from '../lib/filters'
import { useShortcuts } from '../lib/shortcuts'

/**
 * The agent workspace: queue list on the left, ticket on the right. The URL is the state
 * (`/queue/:queue/:ticket`, `/tickets/:ticket`, `/search/:ticket`), so deep links and reloads work.
 * Below 900px the two panes are separate views (CSS) and the ticket view has a Back control.
 */
export default function WorkspacePage({ mode }: { mode: Mode }) {
  const { queue: queueParam, ticket } = useParams()
  const location = useLocation()
  const composerRef = useRef<ComposerHandle | null>(null)
  const { openHelp } = useShell()

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

  const base = mode === 'queue' ? `/queue/${queue}` : mode === 'search' ? '/search' : '/tickets'
  const hrefFor = (t: TicketListItem) => `${base}/${encodeURIComponent(t.reference)}${location.search}`
  const listPath = mode === 'ticket' ? `/queue/all${location.search}` : `${base}${location.search}`
  const effectiveQueue: Queue = mode === 'queue' ? queue! : 'all'

  return (
    <div className="workspace" data-view={ticket ? 'ticket' : 'list'}>
      <QueuePane queue={effectiveQueue} mode={mode} selected={ticket} hrefFor={hrefFor} />
      <section className="ticket-pane" aria-label="Ticket">
        {ticket ? (
          <TicketPane key={ticket.toLowerCase()} ticketRef={ticket} backTo={listPath} composerRef={composerRef} />
        ) : (
          <div className="empty-workspace">
            <p>Select a ticket to work on it.</p>
            <p className="muted small">
              <kbd>j</kbd> / <kbd>k</kbd> move, <kbd>Enter</kbd> opens, <kbd>?</kbd> lists all shortcuts.{' '}
              <button type="button" className="ghost" onClick={openHelp}>
                Show shortcuts
              </button>
            </p>
          </div>
        )}
      </section>
    </div>
  )
}
