import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { canTicket } from '../api/permissions'
import { bulkUpdate, type BulkChange } from '../api/tickets'
import { PRIORITIES, STATUSES, type BulkResult, type TicketListItem } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useDirectory } from '../hooks/useDirectory'
import { summarise } from '../lib/bulk'
import { ErrorNotice } from './common'
import SilentResolveDialog from './SilentResolveDialog'

/** Bulk change bar. Shows only what the selection allows (UX only: the server authorises every ticket individually). */
export default function BulkBar({ tickets, onClear }: { tickets: TicketListItem[]; onClear: () => void }) {
  const me = useMe()
  const qc = useQueryClient()
  const departmentIds = [...new Set(tickets.map((t) => t.departmentId))]
  const single = departmentIds.length === 1 ? departmentIds[0] : undefined
  const dir = useDirectory(single)
  const needDept = !single
  const canUpdate = departmentIds.every((d) => canTicket(me, 'tickets.update', d))
  const canAssignAny = departmentIds.every((d) => canTicket(me, 'tickets.assign.any', d))
  const canAssignSelf = departmentIds.every((d) => canTicket(me, 'tickets.assign.self', d))
  const canRoute = canAssignAny || departmentIds.every((d) => canTicket(me, 'tickets.assign.team', d))

  const [status, setStatus] = useState('')
  const [priority, setPriority] = useState('')
  const [team, setTeam] = useState('')
  const [assignee, setAssignee] = useState('')
  const [summary, setSummary] = useState<BulkResult | null>(null)
  const [confirmSilent, setConfirmSilent] = useState(false) // D24: resolving or closing sends the requesters nothing

  const change: BulkChange = {}
  if (status) change.status = status
  if (priority) change.priority = priority
  // Team and assignee choices only apply within ONE department: once the selection spans several, a stale choice is not sent (UX-009).
  if (team && !needDept) change.team = { id: team === 'none' ? null : team }
  if (assignee && (!needDept || assignee === 'me')) change.assignee = { id: assignee === 'none' ? null : assignee === 'me' ? me.id : assignee }
  const anything = Object.keys(change).length > 0

  const apply = useMutation({
    mutationFn: () =>
      bulkUpdate(
        tickets.map((t) => t.id),
        change,
        Object.fromEntries(tickets.flatMap((t) => (t.version ? [[t.id, t.version] as const] : []))),
      ),
    onSuccess: async (result) => {
      setSummary(result)
      setStatus('')
      setPriority('')
      setTeam('')
      setAssignee('')
      await Promise.all([qc.invalidateQueries({ queryKey: ['tickets'] }), qc.invalidateQueries({ queryKey: ['ticket'] })])
    },
  })

  const eligible = dir.members.filter((m) => m.role !== 'Viewer')
  const assignOptions = canAssignAny ? eligible.map((m) => ({ id: m.userId, name: m.displayName })) : []

  return (
    <div className="bulk-bar" role="region" aria-label="Bulk actions">
      <strong className="bulk-count">{tickets.length} selected</strong>
      <label className="fsel">
        <span>Status</span>
        <select value={status} disabled={!canUpdate} onChange={(e) => setStatus(e.target.value)} title={canUpdate ? undefined : 'You cannot change status on all selected tickets'}>
          <option value="">No change</option>
          {STATUSES.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
      </label>
      <label className="fsel">
        <span>Priority</span>
        <select value={priority} disabled={!canUpdate} onChange={(e) => setPriority(e.target.value)}>
          <option value="">No change</option>
          {PRIORITIES.map((p) => (
            <option key={p}>{p}</option>
          ))}
        </select>
      </label>
      <label className="fsel">
        <span>Team</span>
        <select
          value={team}
          disabled={!canRoute || needDept}
          title={needDept ? 'Select tickets from one department to assign a team' : !canRoute ? 'You cannot assign teams here' : undefined}
          onChange={(e) => setTeam(e.target.value)}
        >
          <option value="">No change</option>
          <option value="none">No team</option>
          {dir.teams
            .filter((t) => t.isActive)
            .map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
        </select>
      </label>
      <label className="fsel">
        <span>Assignee</span>
        <select
          value={assignee}
          disabled={(!canAssignAny && !canAssignSelf) || (canAssignAny && needDept)}
          title={needDept && canAssignAny ? 'Select tickets from one department to assign an agent' : undefined}
          onChange={(e) => setAssignee(e.target.value)}
        >
          <option value="">No change</option>
          <option value="me">Me</option>
          {canAssignAny && <option value="none">Unassigned</option>}
          {assignOptions
            .filter((o) => o.id !== me.id)
            .map((o) => (
              <option key={o.id} value={o.id}>
                {o.name}
              </option>
            ))}
        </select>
      </label>
      <button type="button" className="primary" disabled={!anything || apply.isPending} onClick={() => (status === 'Resolved' || status === 'Closed' ? setConfirmSilent(true) : apply.mutate())}>
        {apply.isPending ? 'Applying…' : 'Apply'}
      </button>
      <button type="button" onClick={onClear}>
        Clear selection
      </button>
      {summary && (
        <p className={`bulk-summary${summary.failed > 0 ? ' warn' : ''}`} role="status">
          {summarise(summary)}.{' '}
          <button type="button" className="linklike" onClick={() => setSummary(null)}>
            Dismiss
          </button>
        </p>
      )}
      <ErrorNotice error={apply.error} />
      {confirmSilent && (
        <SilentResolveDialog
          status={status}
          count={tickets.length}
          onClose={() => setConfirmSilent(false)}
          onConfirm={() => {
            setConfirmSilent(false)
            apply.mutate()
          }}
        />
      )}
    </div>
  )
}
