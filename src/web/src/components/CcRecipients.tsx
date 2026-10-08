import { useMutation, useQueryClient } from '@tanstack/react-query'
import { removeParticipant } from '../api/tickets'
import type { TicketDetail } from '../api/ticketSchemas'
import ProblemBanner from './ProblemBanner'

/**
 * The people a public reply is copied to, by name and address (never just a count). An ordinary participant can be taken off future replies by anyone who can reply;
 * that is final for this ticket and never rewrites sent mail. The requester of a merged ticket is always copied and cannot be removed.
 */
export default function CcRecipients({ ticket, canRemove }: { ticket: TicketDetail; canRemove: boolean }) {
  const qc = useQueryClient()
  const remove = useMutation({
    mutationFn: (email: string) => removeParticipant(ticket.id, email),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['ticket'] }),
  })

  return (
    <div className="composer-cc small" aria-label="Copied on this reply">
      <span className="muted">Also sent to:</span>
      <ul className="composer-cc-list">
        {ticket.participants.map((p) => (
          <li key={p.email}>
            <span>
              {p.displayName && p.displayName !== p.email ? `${p.displayName} <${p.email}>` : p.email}
              {p.isProtected && <span className="muted"> (requester of a merged ticket, always copied)</span>}
            </span>
            {canRemove && !p.isProtected && (
              <button type="button" className="link" disabled={remove.isPending} aria-label={`Stop copying ${p.email} on replies`} onClick={() => remove.mutate(p.email)}>
                Remove
              </button>
            )}
          </li>
        ))}
      </ul>
      {remove.isError && <ProblemBanner error={remove.error} />}
    </div>
  )
}
