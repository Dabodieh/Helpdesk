import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent, type ReactNode, type RefObject } from 'react'
import { Link } from 'react-router-dom'
import { canTicket } from '../api/permissions'
import { getTicket, setAssignment, updateTicket } from '../api/tickets'
import { PRIORITIES, STATUSES, type TicketDetail } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useApplyTicket } from '../hooks/useApplyTicket'
import { useDirectory } from '../hooks/useDirectory'
import { formatDateTime } from '../lib/format'
import { livePolling } from '../queries'
import { ErrorNotice, Loading } from './common'
import type { ComposerHandle } from './Composer'
import Composer from './Composer'
import Conversation from './Conversation'
import History from './History'
import { Icon } from './icons'
import { PriorityMark, StatusPill } from './Pills'
import ProblemBanner from './ProblemBanner'

type Props = { ticketRef: string; backTo: string; composerRef: RefObject<ComposerHandle | null> }

export default function TicketPane({ ticketRef, backTo, composerRef }: Props) {
  const qc = useQueryClient()
  const ticket = useQuery({
    queryKey: ['ticket', ticketRef],
    queryFn: () => getTicket(ticketRef),
    ...livePolling,
  })
  const [tab, setTab] = useState<'conversation' | 'history'>('conversation')
  const [problem, setProblem] = useState<unknown>(null)

  function reload() {
    setProblem(null)
    void qc.invalidateQueries({ queryKey: ['ticket'] })
  }

  const back = (
    <Link to={backTo} className="back">
      <Icon name="back" /> Back to list
    </Link>
  )

  if (ticket.isPending)
    return (
      <div className="ticket-inner">
        {back}
        <Loading label="Loading ticket" />
      </div>
    )
  if (ticket.isError && !ticket.data)
    return (
      <div className="ticket-inner pad">
        {back}
        <ErrorNotice error={ticket.error} onRetry={() => void ticket.refetch()} />
      </div>
    )
  const t = ticket.data!

  return (
    <div className="ticket-inner">
      <TicketHeader ticket={t} routeRef={ticketRef} back={back} onProblem={setProblem} />
      <ProblemBanner error={problem} onReload={reload} onDismiss={() => setProblem(null)} />
      <div className="tabs" role="group" aria-label="Ticket sections">
        <button type="button" aria-pressed={tab === 'conversation'} onClick={() => setTab('conversation')}>
          Conversation
        </button>
        <button type="button" aria-pressed={tab === 'history'} onClick={() => setTab('history')}>
          History
        </button>
      </div>
      {tab === 'conversation' ? <Conversation ticketId={t.id} /> : <History ticket={t} />}
      <Composer key={t.id} ticket={t} routeRef={ticketRef} handleRef={composerRef} onReload={reload} />
    </div>
  )
}

function TicketHeader({
  ticket: t,
  routeRef,
  back,
  onProblem,
}: {
  ticket: TicketDetail
  routeRef: string
  back: ReactNode
  onProblem: (e: unknown) => void
}) {
  const me = useMe()
  const apply = useApplyTicket(routeRef)
  const dir = useDirectory(t.departmentId)
  const canUpdate = canTicket(me, 'tickets.update', t.departmentId)
  const canAssignAny = canTicket(me, 'tickets.assign.any', t.departmentId)
  const canAssignSelf = canTicket(me, 'tickets.assign.self', t.departmentId)

  const onSuccess = (res: TicketDetail) => {
    onProblem(null)
    apply(res)
  }
  const patch = useMutation({
    mutationFn: (b: { version?: string; subject?: string; status?: string; priority?: string }) =>
      updateTicket(t.id, { ...b, version: b.version ?? t.version }),
    onSuccess,
    onError: onProblem,
  })
  const assign = useMutation({
    mutationFn: (b: { teamId: string | null; assigneeUserId: string | null }) =>
      setAssignment(t.id, { ...b, version: t.version }),
    onSuccess,
    onError: onProblem,
  })
  const busy = patch.isPending || assign.isPending

  // Assignee choices: eligible department members. Without assign.any only "me" (and "unassigned" when it is me now).
  const eligible = dir.members.filter((m) => m.role !== 'Viewer')
  const assigneeOptions: { id: string; name: string }[] = canAssignAny
    ? eligible.map((m) => ({ id: m.userId, name: m.displayName }))
    : [{ id: me.id, name: `${me.displayName} (me)` }]
  if (t.assignee && !assigneeOptions.some((o) => o.id === t.assignee!.id))
    assigneeOptions.push({ id: t.assignee.id, name: t.assignee.displayName })
  const assigneeLocked = !(canAssignAny || (canAssignSelf && (!t.assignee || t.assignee.id === me.id)))
  const teamOptions = dir.teams.filter((x) => x.isActive || x.id === t.team?.id)

  return (
    <header className="t-head">
      <div className="t-title">
        {back}
        <span className="ref">{t.reference}</span>
        <span className="muted small">{t.departmentName}</span>
      </div>
      <Subject ticket={t} canEdit={canUpdate} busy={busy} save={(subject, version) => patch.mutateAsync({ subject, version })} onProblem={onProblem} />
      <div className="t-controls">
        {canUpdate ? (
          <>
            <Control label="Status" value={t.status} disabled={busy} onChange={(v) => patch.mutate({ status: v })}>
              {STATUSES.map((s) => (
                <option key={s}>{s}</option>
              ))}
            </Control>
            <Control label="Priority" value={t.priority} disabled={busy} onChange={(v) => patch.mutate({ priority: v })}>
              {PRIORITIES.map((p) => (
                <option key={p}>{p}</option>
              ))}
            </Control>
          </>
        ) : (
          <>
            <div className="ctl">
              <span className="ctl-label">Status</span>
              <StatusPill status={t.status} />
            </div>
            <div className="ctl">
              <span className="ctl-label">Priority</span>
              <PriorityMark priority={t.priority} />
            </div>
          </>
        )}
        {canAssignAny ? (
          <Control
            label="Team"
            value={t.team?.id ?? ''}
            disabled={busy}
            onChange={(v) => assign.mutate({ teamId: v || null, assigneeUserId: t.assignee?.id ?? null })}
          >
            <option value="">No team</option>
            {teamOptions.map((x) => (
              <option key={x.id} value={x.id}>
                {x.name}
              </option>
            ))}
            {t.team && !teamOptions.some((x) => x.id === t.team!.id) && <option value={t.team.id}>{t.team.name}</option>}
          </Control>
        ) : (
          <div className="ctl">
            <span className="ctl-label">Team</span>
            <span>{t.team?.name ?? 'No team'}</span>
          </div>
        )}
        {!assigneeLocked ? (
          <Control
            label="Assignee"
            value={t.assignee?.id ?? ''}
            disabled={busy}
            onChange={(v) => assign.mutate({ teamId: t.team?.id ?? null, assigneeUserId: v || null })}
          >
            {(canAssignAny || t.assignee?.id === me.id || !t.assignee) && <option value="">Unassigned</option>}
            {assigneeOptions.map((o) => (
              <option key={o.id} value={o.id}>
                {o.name}
              </option>
            ))}
          </Control>
        ) : (
          <div className="ctl">
            <span className="ctl-label">Assignee</span>
            <span>{t.assignee?.displayName ?? 'Unassigned'}</span>
          </div>
        )}
      </div>
      <dl className="t-meta">
        <div>
          <dt>Requester</dt>
          <dd>
            {t.requester.displayName} <span className="muted">&lt;{t.requester.email}&gt;</span>
            <span className="muted small">
              {' '}
              · {t.requester.openTicketCount} open / {t.requester.totalTicketCount} total
            </span>
          </dd>
        </div>
        {t.participants.length > 0 && (
          <div>
            <dt>Cc</dt>
            <dd>{t.participants.map((p) => p.displayName || p.email).join(', ')}</dd>
          </div>
        )}
        {t.mailbox && (
          <div>
            <dt>Mailbox</dt>
            <dd>{t.mailbox.address}</dd>
          </div>
        )}
        <div>
          <dt>Created</dt>
          <dd>{formatDateTime(t.createdAt)}</dd>
        </div>
        <div>
          <dt>Updated</dt>
          <dd>{formatDateTime(t.updatedAt)}</dd>
        </div>
        {t.resolvedAt && (
          <div>
            <dt>Resolved</dt>
            <dd>{formatDateTime(t.resolvedAt)}</dd>
          </div>
        )}
        {t.closedAt && (
          <div>
            <dt>Closed</dt>
            <dd>{formatDateTime(t.closedAt)}</dd>
          </div>
        )}
      </dl>
    </header>
  )
}

function Control({
  label,
  value,
  disabled,
  onChange,
  children,
}: {
  label: string
  value: string
  disabled?: boolean
  onChange: (v: string) => void
  children: ReactNode
}) {
  return (
    <label className="ctl">
      <span className="ctl-label">{label}</span>
      <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {children}
      </select>
    </label>
  )
}

/** Subject with an explicit Edit step. The edit sends the version it started from, so a concurrent change is a conflict, never an overwrite. */
function Subject({
  ticket: t,
  canEdit,
  busy,
  save,
  onProblem,
}: {
  ticket: TicketDetail
  canEdit: boolean
  busy: boolean
  save: (subject: string, version?: string) => Promise<unknown>
  onProblem: (e: unknown) => void
}) {
  const [editing, setEditing] = useState<{ text: string; version: string | null } | null>(null)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!editing) return
    const text = editing.text.trim()
    if (!text || text === t.subject) return setEditing(null)
    try {
      await save(text, editing.version ?? undefined)
      setEditing(null)
    } catch (err) {
      // The typed subject stays in the box. After a conflict the next save deliberately targets the latest version.
      setEditing({ ...editing, version: null })
      onProblem(err)
    }
  }

  if (!editing)
    return (
      <div className="t-subject">
        <h2>{t.subject}</h2>
        {canEdit && (
          <button type="button" className="ghost" onClick={() => setEditing({ text: t.subject, version: t.version })}>
            <Icon name="edit" /> Edit subject
          </button>
        )}
      </div>
    )
  return (
    <form className="t-subject" onSubmit={(e) => void submit(e)}>
      <input
        autoFocus
        aria-label="Subject"
        value={editing.text}
        maxLength={300}
        onChange={(e) => setEditing({ ...editing, text: e.target.value })}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.preventDefault()
            e.stopPropagation()
            setEditing(null)
          }
        }}
      />
      <button type="submit" className="primary" disabled={busy}>
        Save subject
      </button>
      <button type="button" onClick={() => setEditing(null)}>
        Cancel
      </button>
    </form>
  )
}
