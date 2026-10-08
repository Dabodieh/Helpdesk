import { useQuery } from '@tanstack/react-query'
import { listMailboxes } from '../api/mail'
import type { RuleAction } from '../api/automation'
import type { Condition } from '../api/sla'
import { PRIORITIES, STATUSES } from '../api/ticketSchemas'
import { useDirectory } from '../hooks/useDirectory'
import { ACTION_LABEL, FIELD_LABEL, typeKey } from '../lib/rules'

/** Structured condition and action editors (no raw JSON, no expressions). The server re-validates every value against the department. */

const SLA_STATE_OPTIONS = ['Running', 'Approaching', 'Breached', 'Paused', 'Met', 'MetLate', 'NotApplicable', 'None']

const isSet = (field: string) => !['subjectContains', 'requesterEmail', 'requesterDomain'].includes(field)

function useChoices(departmentId: string) {
  const dir = useDirectory(departmentId)
  const mailboxes = useQuery({ queryKey: ['mailboxes', departmentId], queryFn: () => listMailboxes(departmentId), staleTime: 60_000 })
  return {
    status: STATUSES.map((s) => ({ value: s, label: s })),
    priority: PRIORITIES.map((p) => ({ value: p, label: p })),
    team: [{ value: 'none', label: 'No team' }, ...dir.teams.map((t) => ({ value: t.id, label: t.name }))],
    assignee: [{ value: 'none', label: 'Unassigned' }, ...dir.members.map((m) => ({ value: m.userId, label: m.displayName }))],
    mailbox: [{ value: 'none', label: 'No mailbox (created in the web)' }, ...(mailboxes.data ?? []).map((m) => ({ value: m.id, label: m.address }))],
    slaFirstResponse: SLA_STATE_OPTIONS.map((s) => ({ value: s, label: s })),
    slaResolution: SLA_STATE_OPTIONS.map((s) => ({ value: s, label: s })),
  } as Record<string, { value: string; label: string }[]>
}

function toggle(values: string[], value: string) {
  return values.includes(value) ? values.filter((v) => v !== value) : [...values, value]
}

export function ConditionsEditor({
  departmentId,
  fields,
  value,
  onChange,
  errors,
  label = 'Conditions',
}: {
  departmentId: string
  fields: readonly string[]
  value: Condition[]
  onChange: (next: Condition[]) => void
  errors?: Record<string, string>
  label?: string
}) {
  const choices = useChoices(departmentId)
  const update = (i: number, c: Condition) => onChange(value.map((x, j) => (j === i ? c : x)))
  return (
    <fieldset className="editor">
      <legend>{label}</legend>
      <p className="muted small">{value.length === 0 ? 'No conditions: this applies to every ticket.' : 'All conditions must be true.'}</p>
      {value.map((c, i) => (
        <div key={i} className="editor-row" role="group" aria-label={`Condition ${i + 1}`}>
          <select
            aria-label={`Condition ${i + 1} field`}
            value={c.field}
            onChange={(e) => {
              const field = e.target.value
              update(i, { field, operator: isSet(field) ? 'in' : field === 'subjectContains' ? 'contains' : 'equals', values: [] })
            }}
          >
            {fields.map((f) => (
              <option key={f} value={f}>
                {FIELD_LABEL[f]}
              </option>
            ))}
          </select>
          {isSet(c.field) ? (
            <>
              <select aria-label={`Condition ${i + 1} operator`} value={c.operator} onChange={(e) => update(i, { ...c, operator: e.target.value })}>
                <option value="in">is any of</option>
                <option value="notIn">is none of</option>
              </select>
              <div className="check-group" role="group" aria-label={`Condition ${i + 1} values`}>
                {(choices[c.field] ?? []).map((o) => (
                  <label key={o.value} className="check">
                    <input type="checkbox" checked={c.values.includes(o.value)} onChange={() => update(i, { ...c, values: toggle(c.values, o.value) })} /> {o.label}
                  </label>
                ))}
              </div>
            </>
          ) : (
            <input
              aria-label={`Condition ${i + 1} value`}
              value={c.values[0] ?? ''}
              maxLength={200}
              placeholder={c.field === 'requesterDomain' ? 'example.com' : c.field === 'requesterEmail' ? 'name@example.com' : 'text'}
              onChange={(e) => update(i, { ...c, values: [e.target.value] })}
            />
          )}
          <button type="button" className="ghost" onClick={() => onChange(value.filter((_, j) => j !== i))} aria-label={`Remove condition ${i + 1}`}>
            Remove
          </button>
          {errors?.[`conditions[${i}]`] && <span className="field-error">{errors[`conditions[${i}]`]}</span>}
        </div>
      ))}
      {errors?.conditions && <span className="field-error">{errors.conditions}</span>}
      <button type="button" onClick={() => onChange([...value, { field: fields[0]!, operator: isSet(fields[0]!) ? 'in' : 'equals', values: [] }])}>
        Add condition
      </button>
    </fieldset>
  )
}

export function ActionsEditor({
  departmentId,
  value,
  onChange,
  errors,
}: {
  departmentId: string
  value: RuleAction[]
  onChange: (next: RuleAction[]) => void
  errors?: Record<string, string>
}) {
  const choices = useChoices(departmentId)
  const update = (i: number, a: RuleAction) => onChange(value.map((x, j) => (j === i ? a : x)))
  const move = (i: number, delta: -1 | 1) => {
    const j = i + delta
    if (j < 0 || j >= value.length) return
    const next = [...value]
    ;[next[i], next[j]] = [next[j]!, next[i]!]
    onChange(next)
  }
  return (
    <fieldset className="editor">
      <legend>Actions</legend>
      <p className="muted small">Actions run in this order. Internal notes are for staff only and are never emailed to the requester.</p>
      {value.map((a, i) => (
        <div key={i} className="editor-row" role="group" aria-label={`Action ${i + 1}`}>
          <select
            aria-label={`Action ${i + 1} type`}
            value={a.type}
            onChange={(e) => {
              const type = e.target.value
              update(i, { type, value: type === 'addInternalNote' ? null : (choices[typeKey(type)]?.[0]?.value ?? ''), text: type === 'addInternalNote' ? '' : null })
            }}
          >
            {Object.entries(ACTION_LABEL).map(([k, v]) => (
              <option key={k} value={k}>
                {v}
              </option>
            ))}
          </select>
          {a.type === 'addInternalNote' ? (
            <textarea aria-label={`Action ${i + 1} note text`} rows={2} maxLength={1000} value={a.text ?? ''} onChange={(e) => update(i, { ...a, text: e.target.value })} />
          ) : (
            <select aria-label={`Action ${i + 1} value`} value={a.value ?? ''} onChange={(e) => update(i, { ...a, value: e.target.value })}>
              {(choices[typeKey(a.type)] ?? []).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          )}
          <span className="row-actions">
            <button type="button" className="ghost" onClick={() => move(i, -1)} disabled={i === 0} aria-label={`Move action ${i + 1} up`}>
              ↑
            </button>
            <button type="button" className="ghost" onClick={() => move(i, 1)} disabled={i === value.length - 1} aria-label={`Move action ${i + 1} down`}>
              ↓
            </button>
            <button type="button" className="ghost" onClick={() => onChange(value.filter((_, j) => j !== i))} aria-label={`Remove action ${i + 1}`}>
              Remove
            </button>
          </span>
          {errors?.[`actions[${i}]`] && <span className="field-error">{errors[`actions[${i}]`]}</span>}
        </div>
      ))}
      {errors?.actions && <span className="field-error">{errors.actions}</span>}
      <button type="button" onClick={() => onChange([...value, { type: 'setPriority', value: 'High', text: null }])}>
        Add action
      </button>
    </fieldset>
  )
}
