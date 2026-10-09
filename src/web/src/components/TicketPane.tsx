import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState, type RefObject } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { canTicket } from '../api/permissions'
import { getTicket } from '../api/tickets'
import { useMe } from '../auth'
import { formatDateTime } from '../lib/format'
import { usePref } from '../lib/prefs'
import { neighbours, readQueueContext, saveQueueContext } from '../lib/queueContext'
import { useShortcuts } from '../lib/shortcuts'
import { useReportPresence, usePresence, type PresenceState } from '../lib/realtime'
import { livePolling } from '../queries'
import { ErrorNotice, Loading } from './common'
import type { ComposerHandle } from './Composer'
import Composer from './Composer'
import PresenceNotice from './PresenceNotice'
import Conversation from './Conversation'
import History from './History'
import { Icon } from './icons'
import ProblemBanner from './ProblemBanner'
import PropertiesPanel from './PropertiesPanel'
import TicketHeader from './TicketHeader'
import { TicketDialogs, type TicketDialogName } from './TicketLinks'

type Props = { ticketRef: string; composerRef: RefObject<ComposerHandle | null> }

/** Below this width of the ticket pane the properties panel is an overlay instead of a docked column, so the conversation keeps usable room. */
const DOCK_MIN_WIDTH = 690

/** True while the element is narrower than <paramref name="limit"/> px. Assumes wide when ResizeObserver is unavailable. */
function useNarrow(el: HTMLElement | null, limit: number) {
  const [narrow, setNarrow] = useState(false)
  useEffect(() => {
    if (!el || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setNarrow(entry.contentRect.width < limit))
    observer.observe(el)
    return () => observer.disconnect()
  }, [el, limit])
  return narrow
}

export default function TicketPane({ ticketRef, composerRef }: Props) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const location = useLocation()
  // Where the agent came from (their filters, sort and place in the list) and the tickets either side of this one in that list.
  const fromState = (location.state as { backTo?: string } | null)?.backTo
  const nav = useMemo(() => {
    if (fromState) saveQueueContext({ backTo: fromState, refs: readQueueContext()?.backTo === fromState ? readQueueContext()!.refs : [] })
    return neighbours(readQueueContext(), ticketRef)
  }, [fromState, ticketRef])
  const backTo = fromState && nav.index < 0 ? fromState : nav.backTo
  const goto = (ref: string | undefined) => {
    if (ref) void navigate(`/tickets/${encodeURIComponent(ref)}`)
  }
  useShortcuts({ ']': (e) => { e.preventDefault(); goto(nav.next) }, '[': (e) => { e.preventDefault(); goto(nav.previous) } })
  const me = useMe()
  const ticket = useQuery({
    queryKey: ['ticket', ticketRef],
    queryFn: () => getTicket(ticketRef),
    ...livePolling,
  })
  const [tab, setTab] = useState<'conversation' | 'history'>('conversation')
  const [problem, setProblem] = useState<unknown>(null)
  const [dialog, setDialog] = useState<TicketDialogName | null>(null)
  const [composing, setComposing] = useState<PresenceState | null>(null)
  const [root, setRoot] = useState<HTMLDivElement | null>(null)
  const narrow = useNarrow(root, DOCK_MIN_WIDTH)
  const [docked, setDocked] = usePref('helpdesk.properties', ['open', 'closed'] as const, typeof window !== 'undefined' && window.innerWidth < 1400 ? 'closed' : 'open')
  const [overlayOpen, setOverlayOpen] = useState(false)
  const panelOpen = narrow ? overlayOpen : docked === 'open'
  const togglePanel = () => (narrow ? setOverlayOpen((v) => !v) : setDocked(docked === 'open' ? 'closed' : 'open'))

  // Advisory presence: viewing, or writing a reply / internal note (a state only, never the text).
  useReportPresence(ticket.data?.id, ticket.data ? (composing ?? 'viewing') : null)
  const others = usePresence(ticket.data?.id)

  function reload() {
    setProblem(null)
    void qc.invalidateQueries({ queryKey: ['ticket'] })
  }

  const back = (
    <Link to={backTo} className="back">
      <Icon name="back" /> Back to queue
    </Link>
  )
  const stepper = (
    <span className="ticket-stepper" role="group" aria-label="Tickets in this queue">
      {nav.previous ? <Link to={`/tickets/${encodeURIComponent(nav.previous)}`} className="button" rel="prev" aria-label={`Previous ticket, ${nav.previous}`} title="Previous ticket ([)">←</Link> : <span className="button disabled" aria-hidden="true">←</span>}
      <span className="muted small" role="status">{nav.index >= 0 ? `${nav.index + 1} of ${nav.total}` : ''}</span>
      {nav.next ? <Link to={`/tickets/${encodeURIComponent(nav.next)}`} className="button" rel="next" aria-label={`Next ticket, ${nav.next}`} title="Next ticket (])">→</Link> : <span className="button disabled" aria-hidden="true">→</span>}
    </span>
  )

  if (ticket.isPending)
    return (
      <div className="ticket-inner">
        <div className="ticket-main">
          {back}
          <Loading label="Loading ticket" />
        </div>
      </div>
    )
  if (ticket.isError && !ticket.data)
    return (
      <div className="ticket-inner">
        <div className="ticket-main pad">
          {back}
          <ErrorNotice error={ticket.error} onRetry={() => void ticket.refetch()} />
        </div>
      </div>
    )
  const t = ticket.data!
  const merged = !!t.mergedInto
  // A merged ticket is read-only for every normal operation, whatever the layout offers.
  const canUpdate = canTicket(me, 'tickets.update', t.departmentId) && !merged

  return (
    <div className="ticket-inner" ref={setRoot} data-panel={panelOpen ? 'open' : 'closed'}>
      <div className="ticket-main">
        <TicketHeader
          ticket={t}
          routeRef={ticketRef}
          back={back}
          stepper={stepper}
          canUpdate={canUpdate}
          onProblem={setProblem}
          onOpen={setDialog}
          panelOpen={panelOpen}
          onTogglePanel={togglePanel}
        />
        <ProblemBanner error={problem} onReload={reload} onDismiss={() => setProblem(null)} />
        {t.mergedInto && (
          <section className="merged-banner" aria-label="Merged ticket">
            <strong className="badge off">MERGED</strong>
            <span>
              This ticket was merged into <span className="ref">{t.mergedInto.reference}</span>.
              <span className="muted small">
                {' '}
                {formatDateTime(t.mergedInto.mergedAt)}
                {t.mergedInto.mergedBy?.displayName ? ` by ${t.mergedInto.mergedBy.displayName}` : ''}. It is read-only.
              </span>
            </span>
            <Link className="button" to={`/tickets/${encodeURIComponent(t.mergedInto.reference)}`}>
              Open {t.mergedInto.reference}
            </Link>
          </section>
        )}
        {t.mergedTickets && t.mergedTickets.length > 0 && (
          <p className="merged-sources small muted">
            Merged tickets:{' '}
            {t.mergedTickets.map((m, i) => (
              <span key={m.id}>
                {i > 0 && ', '}
                <Link to={`/tickets/${encodeURIComponent(m.reference)}`} title={m.subject}>
                  {m.reference}
                </Link>
              </span>
            ))}
          </p>
        )}
        <div className="tabs" role="group" aria-label="Ticket sections">
          <button type="button" aria-pressed={tab === 'conversation'} onClick={() => setTab('conversation')}>
            Conversation
          </button>
          <button type="button" aria-pressed={tab === 'history'} onClick={() => setTab('history')}>
            History
          </button>
        </div>
        {tab === 'conversation' ? <Conversation ticketId={t.id} departmentId={t.departmentId} /> : <History ticket={t} />}
        {t.mergedInto ? (
          <p className="empty small" role="note">
            Replies and notes are disabled on a merged ticket. Continue on {t.mergedInto.reference}.
          </p>
        ) : (
          <>
            <PresenceNotice users={others} />
            <Composer key={t.id} ticket={t} routeRef={ticketRef} handleRef={composerRef} onReload={reload} onComposing={setComposing} />
          </>
        )}
      </div>
      {panelOpen && (
        <PropertiesPanel
          ticket={t}
          routeRef={ticketRef}
          canUpdate={canUpdate}
          onProblem={setProblem}
          onOpen={setDialog}
          onHide={togglePanel}
          overlay={narrow}
        />
      )}
      <TicketDialogs dialog={dialog} ticket={t} routeRef={ticketRef} composerRef={composerRef} onClose={() => setDialog(null)} />
    </div>
  )
}
