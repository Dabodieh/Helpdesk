import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { createSlaPolicy, listBusinessHours, listSlaPolicies, reorderSlaPolicies, updateSlaPolicy, type Condition, type SlaPolicy } from '../api/sla'
import type { Department } from '../api/schemas'
import { formatMinutes } from '../lib/sla'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'
import { POLICY_FIELDS, summariseRule } from '../lib/rules'
import { ConditionsEditor } from './RuleEditors'

type Unit = 'minutes' | 'hours' | 'days'
const FACTOR: Record<Unit, number> = { minutes: 1, hours: 60, days: 1440 }

/** A target as number + unit; empty means "no target". */
function Duration({ label, minutes, onChange, error, hint }: { label: string; minutes: number | null; onChange: (m: number | null) => void; error?: string; hint?: string }) {
  const unit: Unit = minutes && minutes % 1440 === 0 ? 'days' : minutes && minutes % 60 === 0 ? 'hours' : 'minutes'
  const [u, setU] = useState<Unit>(unit)
  const shown = minutes === null ? '' : String(minutes / FACTOR[u])
  return (
    <Field label={label} error={error} hint={hint}>
      {(p) => (
        <span className="duration">
          <input
            {...p}
            type="number"
            min="1"
            step="1"
            value={shown}
            placeholder="none"
            onChange={(e) => onChange(e.target.value === '' ? null : Math.round(Number(e.target.value) * FACTOR[u]))}
          />
          <select aria-label={`${label} unit`} value={u} onChange={(e) => setU(e.target.value as Unit)}>
            <option value="minutes">minutes</option>
            <option value="hours">hours</option>
            <option value="days">days</option>
          </select>
        </span>
      )}
    </Field>
  )
}

function Editor({ department, existing, onClose }: { department: Department; existing?: SlaPolicy; onClose: () => void }) {
  const qc = useQueryClient()
  const calendars = useQuery({ queryKey: ['business-hours', department.id], queryFn: () => listBusinessHours(department.id) })
  const [name, setName] = useState(existing?.name ?? '')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [calendarId, setCalendarId] = useState(existing?.calendarId ?? '')
  const [first, setFirst] = useState<number | null>(existing ? existing.firstResponseMinutes : 60)
  const [resolution, setResolution] = useState<number | null>(existing ? existing.resolutionMinutes : 480)
  const [pause, setPause] = useState(existing?.pauseWhenPending ?? false)
  const [lead, setLead] = useState(existing?.approachingLeadMinutes ?? 60)
  const [conditions, setConditions] = useState<Condition[]>(existing?.conditions ?? [])

  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(),
        isActive: active,
        conditions,
        calendarId: calendarId || null,
        clearCalendar: !calendarId,
        firstResponseMinutes: first,
        resolutionMinutes: resolution,
        clearFirstResponse: first === null,
        clearResolution: resolution === null,
        pauseWhenPending: pause,
        approachingLeadMinutes: lead,
      }
      return existing ? updateSlaPolicy(department.id, existing.id, { ...body, version: existing.version }) : createSlaPolicy(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['sla-policies', department.id] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)

  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim() && (first !== null || resolution !== null)) save.mutate()
  }

  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New SLA policy'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="SLA policy">
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Business hours" error={server.calendarId} hint="Targets count only working time in this calendar. Choose 24 x 7 to count round the clock.">
          {(p) => (
            <select {...p} value={calendarId} onChange={(e) => setCalendarId(e.target.value)}>
              <option value="">24 x 7 (round the clock)</option>
              {(calendars.data ?? [])
                .filter((c) => c.isActive || c.id === existing?.calendarId)
                .map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name} ({c.timeZone})
                  </option>
                ))}
            </select>
          )}
        </Field>
        <Duration label="First response target" minutes={first} onChange={setFirst} error={server.firstResponseMinutes} hint="Met by the first public reply from an agent. Notes and automation never count." />
        <Duration label="Resolution target" minutes={resolution} onChange={setResolution} error={server.resolutionMinutes} hint="Met when the ticket is Resolved or Closed." />
        <label className="check">
          <input type="checkbox" checked={pause} onChange={(e) => setPause(e.target.checked)} /> Pause the resolution clock while the ticket is Pending
        </label>
        <Field label="Warn this long before a deadline (minutes)" error={server.approachingLeadMinutes}>
          {(p) => <input {...p} type="number" min="0" max="10080" value={lead} onChange={(e) => setLead(Number(e.target.value))} />}
        </Field>
        <ConditionsEditor departmentId={department.id} fields={POLICY_FIELDS} value={conditions} onChange={setConditions} errors={server} label="Applies to tickets where" />
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active
        </label>
        <p className="muted small">Policies are checked in order and the first active match is used. Editing a policy never changes tickets that already have a service level.</p>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim() || (first === null && resolution === null)}>
            Save policy
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Department SLA policies (permission department.sla.manage). */
export default function SlaPoliciesSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['sla-policies', department.id], queryFn: () => listSlaPolicies(department.id) })
  const [editing, setEditing] = useState<SlaPolicy | 'new' | null>(null)
  const reorder = useMutation({
    mutationFn: (ids: string[]) => reorderSlaPolicies(department.id, ids),
    onSuccess: (data) => qc.setQueryData(['sla-policies', department.id], data),
  })
  const move = (index: number, delta: -1 | 1) => {
    const ids = (q.data ?? []).map((p) => p.id)
    const j = index + delta
    if (j < 0 || j >= ids.length) return
    ;[ids[index], ids[j]] = [ids[j]!, ids[index]!]
    reorder.mutate(ids)
  }
  const nameOf = (_kind: string, value: string) => (value === 'none' ? 'none' : value)

  return (
    <section aria-labelledby="sla-h">
      <div className="section-head">
        <h2 id="sla-h">SLA policies</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New SLA policy
        </button>
      </div>
      <p className="muted">Response and resolution targets. The first active policy that matches a new ticket is used, in the order shown. Policies are never combined.</p>
      {q.isPending && <Loading label="Loading SLA policies" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && q.data.length === 0 && <p className="empty">No SLA policies yet. Tickets have no deadlines until you add one.</p>}
      {q.isSuccess && q.data.length > 0 && (
        <table className="grid">
          <caption className="sr-only">SLA policies of {department.name}, in matching order</caption>
          <thead>
            <tr>
              <th scope="col">Order</th>
              <th scope="col">Policy</th>
              <th scope="col">Applies to</th>
              <th scope="col">Targets</th>
              <th scope="col">Hours</th>
              <th scope="col">Status</th>
              <th scope="col" />
            </tr>
          </thead>
          <tbody>
            {q.data.map((p, i) => (
              <tr key={p.id}>
                <td className="row-actions">
                  <span className="muted">{i + 1}</span>
                  <button type="button" className="ghost" aria-label={`Move ${p.name} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)}>
                    ↑
                  </button>
                  <button type="button" className="ghost" aria-label={`Move ${p.name} down`} disabled={i === q.data.length - 1 || reorder.isPending} onClick={() => move(i, 1)}>
                    ↓
                  </button>
                </td>
                <td>
                  <strong>{p.name}</strong>
                </td>
                <td className="small">{summariseRule(p.conditions, [], nameOf).replace(' → ', '')}</td>
                <td className="small">
                  {p.firstResponseMinutes ? `First response ${formatMinutes(p.firstResponseMinutes)}` : ''}
                  {p.firstResponseMinutes && p.resolutionMinutes ? ' · ' : ''}
                  {p.resolutionMinutes ? `Resolution ${formatMinutes(p.resolutionMinutes)}` : ''}
                  {p.pauseWhenPending ? ' · pauses when Pending' : ''}
                </td>
                <td>{p.calendarName ?? '24 x 7'}</td>
                <td>
                  <ActiveBadge active={p.isActive} />
                </td>
                <td className="row-actions">
                  <button type="button" onClick={() => setEditing(p)}>
                    Edit<span className="sr-only"> {p.name}</span>
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ErrorNotice error={reorder.error} />
      {editing && <Editor department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
    </section>
  )
}
