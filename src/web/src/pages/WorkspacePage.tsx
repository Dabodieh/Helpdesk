import { useRef, type CSSProperties } from 'react'
import { Navigate, useLocation, useParams } from 'react-router-dom'
import type { TicketListItem } from '../api/ticketSchemas'
import { QUEUES, type Queue } from '../api/ticketSchemas'
import type { ComposerHandle } from '../components/Composer'
import QueuePane from '../components/QueuePane'
import { useShell } from '../components/ShellContext'
import TicketPane from '../components/TicketPane'
import type { Mode } from '../lib/filters'
import { SPLIT_DEFAULT, SPLIT_MIN, useSplit } from '../lib/split'
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
  const workspaceRef = useRef<HTMLDivElement>(null)
  const split = useSplit(workspaceRef)

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
    <div className="workspace" data-view={ticket ? 'ticket' : 'list'} ref={workspaceRef} style={{ '--queue-w': `${split.width}px` } as CSSProperties}>
      <QueuePane queue={effectiveQueue} mode={mode} selected={ticket} hrefFor={hrefFor} />
      <div
        className="split-handle"
        role="separator"
        aria-orientation="vertical"
        aria-label="Resize ticket list"
        aria-valuemin={SPLIT_MIN}
        aria-valuenow={split.width}
        aria-valuemax={1600}
        aria-valuetext={`${split.width} pixels (default ${SPLIT_DEFAULT})`}
        tabIndex={0}
        {...split.handlers}
      />
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
