import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { listMailboxes } from '../api/mail'
import { canInDepartment } from '../api/permissions'
import { assignTicketMailbox } from '../api/tickets'
import type { TicketDetail } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useApplyTicket } from '../hooks/useApplyTicket'
import Dialog from './Dialog'
import { ErrorNotice } from './common'

/**
 * The ticket's sender identity (D22). Shows it, and for a Department Admin offers the explicit action to give a mailbox-less ticket one, or to correct that assignment
 * before anything has been sent. The server decides everything (membership, eligibility, whether it is already fixed); this only offers the action.
 */
export default function TicketMailbox({ ticket: t, routeRef }: { ticket: TicketDetail; routeRef: string }) {
  const me = useMe()
  const [open, setOpen] = useState(false)
  const mayManageMailboxes = canInDepartment(me, 'department.mailboxes.manage', t.departmentId)
  // Sender identity is an administrator concern: agents without the permission never see the row.
  if (!mayManageMailboxes) return null
  const canManage = !t.mergedInto
  const offer = canManage && (!t.mailbox || t.mailboxAssigned)
  return (
    <div className="ctl">
      <span className="ctl-label">Mailbox administration</span>
      <span>
        {t.mailbox ? (
          <>
            {t.mailbox.name}

          </>
        ) : (
          <span className="muted">No mailbox: replies cannot be emailed</span>
        )}
        {offer && (
          <>
            <br />
            <button type="button" className="linklike" onClick={() => setOpen(true)}>
              {t.mailbox ? 'Correct mailbox' : 'Assign a mailbox'}
              <span className="sr-only"> for {t.reference}</span>
            </button>
          </>
        )}
      </span>
      {open && <AssignDialog ticket={t} routeRef={routeRef} onClose={() => setOpen(false)} />}
    </div>
  )
}

function AssignDialog({ ticket: t, routeRef, onClose }: { ticket: TicketDetail; routeRef: string; onClose: () => void }) {
  const apply = useApplyTicket(routeRef)
  const mailboxes = useQuery({ queryKey: ['mailboxes', t.departmentId], queryFn: () => listMailboxes(t.departmentId) })
  const [chosen, setChosen] = useState('')
  const eligible = (mailboxes.data ?? []).filter((m) => m.isActive)
  const picked = eligible.find((m) => m.id === chosen)
  const save = useMutation({
    mutationFn: () => assignTicketMailbox(t.id, t.version, chosen),
    onSuccess: (res) => {
      apply(res)
      onClose()
    },
  })
  return (
    <Dialog title={t.mailbox ? `Correct the mailbox of ${t.reference}` : `Assign a mailbox to ${t.reference}`} onClose={onClose}>
      <form
        className="dialog-form"
        onSubmit={(e) => {
          e.preventDefault()
          if (chosen) save.mutate()
        }}
        aria-label="Assign mailbox"
      >
        {mailboxes.isPending && <p className="muted">Loading mailboxes…</p>}
        {mailboxes.isError && <ErrorNotice error={mailboxes.error} onRetry={() => void mailboxes.refetch()} />}
        <label className="ctl">
          <span className="ctl-label">Mailbox</span>
          <select value={chosen} data-autofocus onChange={(e) => setChosen(e.target.value)} required>
            <option value="">Choose a mailbox</option>
            {eligible.map((m) => (
              <option key={m.id} value={m.id}>
                {m.displayName} ({m.address})
              </option>
            ))}
          </select>
        </label>
        <p className="notice info">
          {picked ? `${picked.displayName} (${picked.address})` : 'The mailbox you choose'} becomes the sender identity of {t.reference}: every reply to the requester leaves from it. It can only be
          corrected until a public reply has been submitted, or may have been; once sending has begun the mailbox is permanently fixed.
        </p>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={!chosen || save.isPending}>
            {t.mailbox ? 'Correct mailbox' : 'Assign mailbox'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}
