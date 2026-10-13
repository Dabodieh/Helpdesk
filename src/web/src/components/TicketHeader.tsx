import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent, type ReactNode } from 'react'
import { updateTicket } from '../api/tickets'
import type { TicketDetail } from '../api/ticketSchemas'
import { useApplyTicket } from '../hooks/useApplyTicket'
import { formatDateTime } from '../lib/format'
import { Icon } from './icons'
import { PriorityMark, StatusPill } from './Pills'
import { RequesterName } from './RequesterDrawer'
import { MoreMenu, type TicketDialogName } from './TicketLinks'

/**
 * Reference, subject, who asked, the two state marks and the primary actions. Everything else about the ticket lives in the properties panel.
 * The header never offers editing on a merged ticket.
 */
export default function TicketHeader({
  ticket: t,
  routeRef,
  back,
  stepper,
  canUpdate,
  onProblem,
  onOpen,
  panelOpen,
  onTogglePanel,
}: {
  ticket: TicketDetail
  routeRef: string
  back: ReactNode
  stepper?: ReactNode
  canUpdate: boolean
  onProblem: (e: unknown) => void
  onOpen: (dialog: TicketDialogName) => void
  panelOpen: boolean
  onTogglePanel: () => void
}) {
  const apply = useApplyTicket(routeRef)
  const patch = useMutation({
    mutationFn: (b: { subject: string; version: string }) => updateTicket(t.id, b),
    onSuccess: (res) => {
      onProblem(null)
      apply(res)
    },
    onError: onProblem,
  })
  return (
    <header className="t-head">
      <div className="t-title">
        {back}
        {stepper}
        <span className="ref">{t.reference}</span>
        <span className="dept-chip" title="Department">{t.departmentName}</span>
        <span className="grow" />
        {canUpdate && (
          <button type="button" onClick={() => onOpen('macro')}>
            Macro
          </button>
        )}
        {canUpdate && <MoreMenu onOpen={onOpen} />}
        <button type="button" aria-pressed={panelOpen} onClick={onTogglePanel}>
          Properties
        </button>
      </div>
      <Subject ticket={t} canEdit={canUpdate} busy={patch.isPending} save={(subject, version) => patch.mutateAsync({ subject, version: version ?? t.version })} onProblem={onProblem} />
      <div className="t-who">
        <RequesterName requesterId={t.requester.id} name={t.requester.displayName} />
        <span className="t-state">
          <StatusPill status={t.status} />
          <PriorityMark priority={t.priority} />
        </span>
        <span className="muted small t-times">
          Opened {formatDateTime(t.createdAt)} · Updated {formatDateTime(t.updatedAt)}
        </span>
      </div>
    </header>
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
