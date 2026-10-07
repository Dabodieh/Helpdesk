import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { canTicket } from '../api/permissions'
import { setAssignment, updateTicket } from '../api/tickets'
import { updateTicketProperties } from '../api/ticketing'
import { PRIORITIES, STATUSES, type TicketDetail } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useApplyTicket } from '../hooks/useApplyTicket'
import { useDirectory } from '../hooks/useDirectory'
import { PriorityMark, StatusPill } from './Pills'
import { propertiesKey, useTicketProperties } from '../hooks/useTicketProperties'

function Control({ label, value, disabled, onChange, children }: { label: string; value: string; disabled?: boolean; onChange: (v: string) => void; children: ReactNode }) {
  return (
    <label className="ctl">
      <span className="ctl-label">{label}</span>
      <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {children}
      </select>
    </label>
  )
}

function ReadOnly({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="ctl">
      <span className="ctl-label">{label}</span>
      <span>{children}</span>
    </div>
  )
}

/**
 * Status, priority, type, team and assignee as in-place controls. They use the same mutations as before (same validation, same permissions); a merged ticket
 * and callers without the permission only see the values. Failures go to the ticket banner with the server's explanation.
 */
export default function CoreProperties({ ticket: t, routeRef, onProblem }: { ticket: TicketDetail; routeRef: string; onProblem: (e: unknown) => void }) {
  const me = useMe()
  const qc = useQueryClient()
  const apply = useApplyTicket(routeRef)
  const dir = useDirectory(t.departmentId)
  const props = useTicketProperties(t.reference)
  const merged = !!t.mergedInto
  const canUpdate = canTicket(me, 'tickets.update', t.departmentId) && !merged
  const canAssignAny = canTicket(me, 'tickets.assign.any', t.departmentId) && !merged
  const canAssignSelf = canTicket(me, 'tickets.assign.self', t.departmentId) && !merged

  const onSuccess = (res: TicketDetail) => {
    onProblem(null)
    apply(res)
  }
  const patch = useMutation({
    mutationFn: (b: { status?: string; priority?: string }) => updateTicket(t.id, { ...b, version: t.version }),
    onSuccess,
    onError: onProblem,
  })
  const assign = useMutation({
    mutationFn: (b: { teamId: string | null; assigneeUserId: string | null }) => setAssignment(t.id, { ...b, version: t.version }),
    onSuccess,
    onError: onProblem,
  })
  const retype = useMutation({
    mutationFn: (typeId: string) => updateTicketProperties(t.reference, { version: props.data?.version ?? t.version, ...(typeId ? { ticketTypeId: typeId } : { clearTicketType: true }) }),
    onSuccess: async () => {
      onProblem(null)
      await qc.invalidateQueries({ queryKey: propertiesKey(t.reference) })
      await qc.invalidateQueries({ queryKey: ['ticket'] })
      await qc.invalidateQueries({ queryKey: ['tickets'] })
    },
    onError: onProblem,
  })
  const busy = patch.isPending || assign.isPending || retype.isPending

  // Assignee choices: eligible department members. Without assign.any only "me" (and "unassigned" when it is me now).
  const eligible = dir.members.filter((m) => m.role !== 'Viewer')
  const assigneeOptions: { id: string; name: string }[] = canAssignAny ? eligible.map((m) => ({ id: m.userId, name: m.displayName })) : [{ id: me.id, name: `${me.displayName} (me)` }]
  if (t.assignee && !assigneeOptions.some((o) => o.id === t.assignee!.id)) assigneeOptions.push({ id: t.assignee.id, name: t.assignee.displayName })
  const assigneeLocked = !(canAssignAny || (canAssignSelf && (!t.assignee || t.assignee.id === me.id)))
  const teamOptions = dir.teams.filter((x) => x.isActive || x.id === t.team?.id)
  const types = (props.data?.types ?? []).filter((x) => x.isActive || x.id === props.data?.type?.id)

  return (
    <section className="prop-section prop-core" aria-label="Ticket properties">
      <h3>Ticket</h3>
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
          {props.data ? (
            <Control label="Type" value={props.data.type?.id ?? ''} disabled={busy} onChange={(v) => retype.mutate(v)}>
              <option value="">No type</option>
              {types.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                  {x.isActive ? '' : ' (retired)'}
                </option>
              ))}
            </Control>
          ) : (
            <ReadOnly label="Type">{props.isPending ? <span className="skeleton inline" aria-hidden="true" /> : <span className="muted">Unavailable</span>}</ReadOnly>
          )}
        </>
      ) : (
        <>
          <ReadOnly label="Status">
            <StatusPill status={t.status} />
          </ReadOnly>
          <ReadOnly label="Priority">
            <PriorityMark priority={t.priority} />
          </ReadOnly>
          <ReadOnly label="Type">{props.data ? (props.data.type?.displayName ?? <span className="muted">No type</span>) : <span className="skeleton inline" aria-hidden="true" />}</ReadOnly>
        </>
      )}
      {canAssignAny ? (
        <Control label="Team" value={t.team?.id ?? ''} disabled={busy} onChange={(v) => assign.mutate({ teamId: v || null, assigneeUserId: t.assignee?.id ?? null })}>
          <option value="">No team</option>
          {teamOptions.map((x) => (
            <option key={x.id} value={x.id}>
              {x.name}
            </option>
          ))}
          {t.team && !teamOptions.some((x) => x.id === t.team!.id) && <option value={t.team.id}>{t.team.name}</option>}
        </Control>
      ) : (
        <ReadOnly label="Team">{t.team?.name ?? <span className="muted">No team</span>}</ReadOnly>
      )}
      {!assigneeLocked ? (
        <Control label="Assignee" value={t.assignee?.id ?? ''} disabled={busy} onChange={(v) => assign.mutate({ teamId: t.team?.id ?? null, assigneeUserId: v || null })}>
          {(canAssignAny || t.assignee?.id === me.id || !t.assignee) && <option value="">Unassigned</option>}
          {assigneeOptions.map((o) => (
            <option key={o.id} value={o.id}>
              {o.name}
            </option>
          ))}
        </Control>
      ) : (
        <ReadOnly label="Assignee">{t.assignee?.displayName ?? <span className="muted">Unassigned</span>}</ReadOnly>
      )}
    </section>
  )
}
