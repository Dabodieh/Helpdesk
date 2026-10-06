import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { checkDue, createBusinessHours, listBusinessHours, updateBusinessHours, type BusinessHours, type CalendarException, type Interval } from '../api/sla'
import type { Department } from '../api/schemas'
import { formatDateTime } from '../lib/format'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const

type DayState = { open: boolean; intervals: Interval[] }

const zones = (): string[] => {
  try {
    return (Intl as unknown as { supportedValuesOf: (k: string) => string[] }).supportedValuesOf('timeZone')
  } catch {
    return ['UTC', 'Europe/London', 'Europe/Dublin', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Australia/Sydney']
  }
}

function emptyWeek(): Record<string, DayState> {
  return Object.fromEntries(DAYS.map((d) => [d, { open: ['Saturday', 'Sunday'].every((w) => w !== d), intervals: [{ start: '09:00', end: '17:00' }] }]))
}

function fromCalendar(c: BusinessHours): Record<string, DayState> {
  const week = Object.fromEntries(DAYS.map((d) => [d, { open: false, intervals: [{ start: '09:00', end: '17:00' }] }])) as Record<string, DayState>
  for (const w of c.weekly) week[w.day] = { open: true, intervals: w.intervals.length ? w.intervals : [{ start: '09:00', end: '17:00' }] }
  return week
}

function Editor({ department, existing, onClose }: { department: Department; existing?: BusinessHours; onClose: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(existing?.name ?? '')
  const [zone, setZone] = useState(existing?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone ?? 'UTC')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [week, setWeek] = useState<Record<string, DayState>>(existing ? fromCalendar(existing) : emptyWeek())
  const [exceptions, setExceptions] = useState<CalendarException[]>(existing?.exceptions ?? [])
  const zoneList = useMemo(() => zones(), [])

  const body = () => ({
    name: name.trim(),
    timeZone: zone.trim(),
    isActive: active,
    weekly: DAYS.filter((d) => week[d]!.open).map((d) => ({ day: d, intervals: week[d]!.intervals })),
    exceptions,
  })
  const save = useMutation({
    mutationFn: () => (existing ? updateBusinessHours(department.id, existing.id, { ...body(), version: existing.version }) : createBusinessHours(department.id, body())),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['business-hours', department.id] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  const setDay = (d: string, next: Partial<DayState>) => setWeek((w) => ({ ...w, [d]: { ...w[d]!, ...next } }))
  const setInterval_ = (d: string, i: number, patch: Partial<Interval>) =>
    setDay(d, { intervals: week[d]!.intervals.map((x, j) => (j === i ? { ...x, ...patch } : x)) })

  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim()) save.mutate()
  }

  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New business hours'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Business hours">
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Time zone" error={server.timeZone} hint="Daylight saving changes are handled automatically.">
          {(p) => (
            <>
              <input {...p} list="tz-list" value={zone} onChange={(e) => setZone(e.target.value)} />
              <datalist id="tz-list">
                {zoneList.map((z) => (
                  <option key={z} value={z} />
                ))}
              </datalist>
            </>
          )}
        </Field>
        <fieldset className="editor">
          <legend>Working hours</legend>
          {server.weekly && <span className="field-error">{server.weekly}</span>}
          {DAYS.map((d) => (
            <div key={d} className="editor-row day-row" role="group" aria-label={d}>
              <label className="check day-name">
                <input type="checkbox" checked={week[d]!.open} onChange={(e) => setDay(d, { open: e.target.checked })} /> {d}
              </label>
              {week[d]!.open ? (
                <div className="intervals">
                  {week[d]!.intervals.map((iv, i) => (
                    <span key={i} className="interval">
                      <input type="time" aria-label={`${d} start ${i + 1}`} value={iv.start} onChange={(e) => setInterval_(d, i, { start: e.target.value })} />
                      to
                      <input type="time" aria-label={`${d} end ${i + 1}`} value={iv.end === '24:00' ? '23:59' : iv.end} onChange={(e) => setInterval_(d, i, { end: e.target.value })} />
                      {week[d]!.intervals.length > 1 && (
                        <button type="button" className="ghost" aria-label={`Remove ${d} interval ${i + 1}`} onClick={() => setDay(d, { intervals: week[d]!.intervals.filter((_, j) => j !== i) })}>
                          Remove
                        </button>
                      )}
                    </span>
                  ))}
                  <button type="button" className="ghost" onClick={() => setDay(d, { intervals: [...week[d]!.intervals, { start: '13:00', end: '17:00' }] })}>
                    Add interval
                  </button>
                </div>
              ) : (
                <span className="muted">Closed</span>
              )}
              {server[`weekly[${DAYS.indexOf(d)}]`] && <span className="field-error">{server[`weekly[${DAYS.indexOf(d)}]`]}</span>}
            </div>
          ))}
        </fieldset>
        <fieldset className="editor">
          <legend>Exceptions (holidays and special hours)</legend>
          {exceptions.length === 0 && <p className="muted small">None. Add a date to close the department for a day or give it different hours.</p>}
          {exceptions.map((x, i) => (
            <div key={i} className="editor-row" role="group" aria-label={`Exception ${i + 1}`}>
              <input type="date" aria-label={`Exception ${i + 1} date`} value={x.date} onChange={(e) => setExceptions(exceptions.map((y, j) => (j === i ? { ...y, date: e.target.value } : y)))} />
              <input
                aria-label={`Exception ${i + 1} name`}
                placeholder="Name (optional)"
                value={x.name ?? ''}
                maxLength={100}
                onChange={(e) => setExceptions(exceptions.map((y, j) => (j === i ? { ...y, name: e.target.value } : y)))}
              />
              <label className="check">
                <input
                  type="checkbox"
                  checked={x.intervals.length === 0}
                  onChange={(e) => setExceptions(exceptions.map((y, j) => (j === i ? { ...y, intervals: e.target.checked ? [] : [{ start: '09:00', end: '13:00' }] } : y)))}
                />{' '}
                Closed all day
              </label>
              {x.intervals.map((iv, k) => (
                <span key={k} className="interval">
                  <input
                    type="time"
                    aria-label={`Exception ${i + 1} start`}
                    value={iv.start}
                    onChange={(e) => setExceptions(exceptions.map((y, j) => (j === i ? { ...y, intervals: y.intervals.map((z, m) => (m === k ? { ...z, start: e.target.value } : z)) } : y)))}
                  />
                  to
                  <input
                    type="time"
                    aria-label={`Exception ${i + 1} end`}
                    value={iv.end}
                    onChange={(e) => setExceptions(exceptions.map((y, j) => (j === i ? { ...y, intervals: y.intervals.map((z, m) => (m === k ? { ...z, end: e.target.value } : z)) } : y)))}
                  />
                </span>
              ))}
              <button type="button" className="ghost" onClick={() => setExceptions(exceptions.filter((_, j) => j !== i))} aria-label={`Remove exception ${i + 1}`}>
                Remove
              </button>
              {server[`exceptions[${i}]`] && <span className="field-error">{server[`exceptions[${i}]`]}</span>}
            </div>
          ))}
          <button type="button" onClick={() => setExceptions([...exceptions, { date: '', name: '', intervals: [] }])}>
            Add exception
          </button>
        </fieldset>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active (can be chosen by SLA policies)
        </label>
        <p className="muted small">Tickets that already have a service level keep the hours they were issued with; changes apply to tickets created afterwards.</p>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim()}>
            Save business hours
          </button>
        </div>
      </form>
    </Dialog>
  )
}

function DueCheck({ departmentId, calendar }: { departmentId: string; calendar: BusinessHours }) {
  const [start, setStart] = useState('')
  const [hours, setHours] = useState('4')
  const check = useMutation({
    mutationFn: () => checkDue(departmentId, calendar.id, { start: new Date(start).toISOString(), minutes: Math.round(Number(hours) * 60) }),
  })
  return (
    <form
      className="due-check"
      aria-label={`Check a deadline with ${calendar.name}`}
      onSubmit={(e) => {
        e.preventDefault()
        if (start && Number(hours) > 0) check.mutate()
      }}
    >
      <label>
        If a ticket arrives <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} />
      </label>
      <label>
        with a target of <input type="number" min="0.25" step="0.25" value={hours} onChange={(e) => setHours(e.target.value)} /> business hours
      </label>
      <button type="submit" disabled={!start || check.isPending}>
        Show deadline
      </button>
      {check.data && (
        <span role="status">
          Due {check.data.dueAt ? formatDateTime(check.data.dueAt) : 'never (no working time in this calendar)'}
        </span>
      )}
      <ErrorNotice error={check.error} />
    </form>
  )
}

function summary(c: BusinessHours) {
  const days = c.weekly.map((w) => `${w.day.slice(0, 3)} ${w.intervals.map((i) => `${i.start}–${i.end}`).join(', ')}`)
  return days.join(' · ')
}

/** Department business hours (permission department.sla.manage; the server enforces it). */
export default function BusinessHoursSection({ department }: { department: Department }) {
  const q = useQuery({ queryKey: ['business-hours', department.id], queryFn: () => listBusinessHours(department.id) })
  const [editing, setEditing] = useState<BusinessHours | 'new' | null>(null)
  const [checking, setChecking] = useState<string | null>(null)
  return (
    <section aria-labelledby="bh-h">
      <div className="section-head">
        <h2 id="bh-h">Business hours</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New business hours
        </button>
      </div>
      <p className="muted">When the team works. SLA policies count only working time; holidays and special days are exceptions.</p>
      {q.isPending && <Loading label="Loading business hours" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && q.data.length === 0 && <p className="empty">No business hours yet. Policies without a calendar run round the clock.</p>}
      {q.isSuccess && q.data.length > 0 && (
        <table className="grid">
          <caption className="sr-only">Business hours of {department.name}</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Time zone</th>
              <th scope="col">Weekly hours</th>
              <th scope="col">Exceptions</th>
              <th scope="col">Status</th>
              <th scope="col" />
            </tr>
          </thead>
          <tbody>
            {q.data.map((c) => (
              <tr key={c.id}>
                <td>
                  <strong>{c.name}</strong>
                </td>
                <td>{c.timeZone}</td>
                <td className="small">{summary(c)}</td>
                <td>{c.exceptions.length}</td>
                <td>
                  <ActiveBadge active={c.isActive} />
                </td>
                <td className="row-actions">
                  <button type="button" onClick={() => setEditing(c)}>
                    Edit
                  </button>
                  <button type="button" aria-expanded={checking === c.id} onClick={() => setChecking(checking === c.id ? null : c.id)}>
                    Check a deadline
                  </button>
                  {checking === c.id && <DueCheck departmentId={department.id} calendar={c} />}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && <Editor department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
    </section>
  )
}
