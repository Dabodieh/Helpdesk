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
