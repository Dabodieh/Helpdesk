import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { getTicketSla, type SlaSummary, type TicketSla } from '../api/sla'
import { dueIn, formatMinutes, SLA_LABEL, SLA_MARK } from '../lib/sla'
import { formatDateTime } from '../lib/format'
import { livePolling } from '../queries'
import { ErrorNotice, Loading } from './common'
import Dialog from './Dialog'

/** Compact SLA state for a queue row: a mark, a word and the time left. Renders nothing when no SLA applies. */
export function SlaBadge({ sla }: { sla: SlaSummary | null | undefined }) {
  if (!sla || sla.state === 'None') return null
  const text = SLA_LABEL[sla.state] ?? sla.state
  const when = sla.state === 'Running' || sla.state === 'Approaching' || sla.state === 'Breached' ? dueIn(sla.dueAt) : ''
  return (
    <span className={`sla sla-${sla.state.toLowerCase()}`} title={sla.clock ? `${sla.clock === 'FirstResponse' ? 'First response' : 'Resolution'} deadline` : undefined}>
      <span aria-hidden="true">{SLA_MARK[sla.state]}</span> {text}
      {when && <span className="muted small"> · {when}</span>}
    </span>
  )
}

const CLOCK_NAME: Record<string, string> = { FirstResponse: 'First response', Resolution: 'Resolution' }

function ClockLine({ label, state, dueAt, metAt, target }: { label: string; state: string; dueAt?: string | null; metAt?: string | null; target?: number | null }) {
  if (state === 'None' && !target) return null
  return (
    <div className="sla-line">
      <span className="sla-label">{label}</span>
      <span className={`sla sla-${state.toLowerCase()}`}>
        <span aria-hidden="true">{SLA_MARK[state]}</span> {SLA_LABEL[state] ?? state}
      </span>
      <span className="muted small">
        {metAt ? `at ${formatDateTime(metAt)}` : dueAt ? `due ${formatDateTime(dueAt)} (${dueIn(dueAt)})` : state === 'Paused' ? 'clock stopped while Pending' : ''}
        {target ? ` · target ${formatMinutes(target)}` : ''}
      </span>
    </div>
  )
}

function EventText({ kind, clock, detail }: { kind: string; clock: string | null; detail: Record<string, unknown> }) {
  const who = clock ? CLOCK_NAME[clock] : 'SLA'
  switch (kind) {
    case 'Applied':
      return <>SLA applied: {String(detail.policy)} ({String(detail.calendar)})</>
    case 'FirstResponseMet':
      return <>{who} met{detail.late ? ' late' : ''}</>
    case 'ResolutionMet':
      return <>{who} met{detail.late ? ' late' : ''}</>
    case 'Paused':
      return <>{who} paused</>
    case 'Resumed':
      return <>{who} resumed</>
    case 'Reopened':
      return <>{who} restarted after reopening (cycle {String(detail.cycle)})</>
    case 'FirstResponseNotApplicable':
      return <>{who} not applicable: the ticket was resolved or closed without a reply</>
    case 'PolicyChanged':
      return <>SLA policy changed: {String(detail.from)} to {String(detail.to)}</>
    case 'Approaching':
      return <>{who} deadline approaching</>
    case 'Breached':
      return <>{who} deadline breached</>
    default:
      return <>{kind}</>
  }
}

function Details({ ticket, onClose }: { ticket: string; onClose: () => void }) {
  const q = useQuery({ queryKey: ['ticket', ticket, 'sla'], queryFn: () => getTicketSla(ticket), ...livePolling })
  return (
    <Dialog title="Service level" onClose={onClose}>
      {q.isPending && <Loading label="Loading service level" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && !q.data && <p className="empty small">No service level policy applies to this ticket.</p>}
      {q.data && <DetailsBody sla={q.data} />}
    </Dialog>
  )
}

function DetailsBody({ sla }: { sla: TicketSla }) {
  return (
    <>
      <p>
        <strong>{sla.policyName}</strong> <span className="muted small">· {sla.calendar} ({sla.timeZone}) · issued {formatDateTime(sla.appliedAt)}</span>
      </p>
      <p className="muted small">These deadlines were fixed when the policy was applied; later edits to the policy do not change them.</p>
      <ClockLine label="First response" state={sla.firstResponse.state} dueAt={sla.firstResponse.dueAt} metAt={sla.firstResponse.metAt} target={sla.firstResponse.targetMinutes} />
      <ClockLine label="Resolution" state={sla.resolution.state} dueAt={sla.resolution.dueAt} metAt={sla.resolution.metAt} target={sla.resolution.targetMinutes} />
      {sla.pauseWhenPending && <p className="muted small">The resolution clock pauses while the ticket is Pending.</p>}
      <h3>History</h3>
      <ol className="sla-events">
        {sla.events.map((e, i) => (
          <li key={i}>
            <time className="muted small" dateTime={e.at}>
              {formatDateTime(e.at)}
            </time>{' '}
            <EventText kind={e.kind} clock={e.clock} detail={e.detail} />
          </li>
        ))}
      </ol>
    </>
  )
}

/** SLA state in the ticket header. Silent when the ticket has no SLA; the dialog explains every deadline. */
export function SlaStrip({ ticketRef, sla }: { ticketRef: string; sla: SlaSummary | null | undefined }) {
  const [open, setOpen] = useState(false)
  if (!sla || (sla.firstResponse === 'None' && sla.resolution === 'None')) return null
  return (
    <div className="sla-strip" role="group" aria-label="Ticket service level">
      <span className="sla-label">Service level</span>
      {sla.firstResponse !== 'None' && (
        <span>
          First response <SlaBadge sla={{ ...sla, state: sla.firstResponse, clock: 'FirstResponse', dueAt: sla.clock === 'FirstResponse' ? sla.dueAt : null }} />
        </span>
      )}
      {sla.resolution !== 'None' && (
        <span>
          Resolution <SlaBadge sla={{ ...sla, state: sla.resolution, clock: 'Resolution', dueAt: sla.clock === 'Resolution' ? sla.dueAt : null }} />
        </span>
      )}
      <button type="button" className="ghost" onClick={() => setOpen(true)}>
        Details
      </button>
      {open && <Details ticket={ticketRef} onClose={() => setOpen(false)} />}
    </div>
  )
}

function clockText(clock: TicketSla['firstResponse'], createdAt: string): string {
  switch (clock.state) {
    case 'Met':
    case 'MetLate': {
      const minutes = clock.metAt ? Math.max(1, Math.round((Date.parse(clock.metAt) - Date.parse(createdAt)) / 60000)) : 0
      const took = clock.metAt ? ` in ${formatMinutes(minutes) || '1m'}` : ''
      return clock.state === 'Met' ? `Met${took}` : `Met late${took}`
    }
    case 'NotApplicable':
      return 'Not applicable: resolved without a reply'
    case 'Breached':
      return `BREACHED · ${dueIn(clock.dueAt).replace(' overdue', '') || 'now'} ago`
    case 'Paused':
      return 'Paused while Pending'
    case 'Running':
    case 'Approaching':
      return clock.dueAt ? `${dueIn(clock.dueAt).replace(/^in /, '')} remaining` : 'Running'
    default:
      return ''
  }
}

/** SLA at a glance for the properties panel: each clock with a mark, a word and the time left or overdue. Hidden when no policy applies. */
export function SlaPanel({ reference, createdAt, merged = false }: { reference: string; createdAt: string; merged?: boolean }) {
  const [open, setOpen] = useState(false)
  const q = useQuery({ queryKey: ['ticket', reference, 'sla'], queryFn: () => getTicketSla(reference), ...livePolling })
  if (q.isPending) return <div className="skeleton" aria-hidden="true" />
  if (q.isError || !q.data) return null
  const sla = q.data
  const states = [sla.firstResponse.state, sla.resolution.state]
  const worst = states.includes('Breached') ? 'breached' : states.includes('Approaching') ? 'approaching' : 'ok'
  const line = (label: string, clock: TicketSla['firstResponse']) => {
    // A merged ticket's clocks were stopped by the merge: say so instead of showing an empty line.
    const text = clockText(clock, createdAt) || SLA_LABEL[clock.state] || (merged && clock.targetMinutes ? 'Stopped (ticket merged)' : '')
    return !text ? null : (
      <div className="sla-line">
        <span className="sla-label">{label}</span>
        <span className={`sla sla-${clock.state.toLowerCase()}`}>
          <span aria-hidden="true">{SLA_MARK[clock.state]}</span> {text}
        </span>
      </div>
    )
  }
  return (
    <section className={`prop-section sla-panel sla-panel-${worst}`} aria-label="Service level">
      <h3>SLA</h3>
      {line('First response', sla.firstResponse)}
      {line('Resolution', sla.resolution)}
      <button type="button" className="ghost" onClick={() => setOpen(true)}>
        SLA details
      </button>
      {open && <Details ticket={reference} onClose={() => setOpen(false)} />}
    </section>
  )
}

