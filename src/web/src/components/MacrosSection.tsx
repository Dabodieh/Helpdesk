import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { listCanned } from '../api/canned'
import { fieldErrors } from '../api/errors'
import { createMacro, listMacros, MACRO_ACTION_LABEL, MACRO_ACTION_TYPES, reorderMacros, updateMacro, type Macro, type MacroAction, type MacroActionType } from '../api/macros'
import type { Department } from '../api/schemas'
import { PRIORITIES, STATUSES } from '../api/ticketSchemas'
import { listTicketFields, listTicketTypes, type TicketField } from '../api/ticketing'
import { useDirectory } from '../hooks/useDirectory'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

const key = (d: string) => ['macros', d]

/** Value of a `setField` action as the field type needs it (a choice, yes/no, number, list of choices or text). */
function FieldValue({ field, value, onChange }: { field: TicketField; value: unknown; onChange: (v: unknown) => void }) {
  const options = field.options.filter((o) => o.isActive)
  switch (field.type) {
    case 'SingleSelect':
      return (
        <select aria-label="Value" value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value)}>
          <option value="">Choose…</option>
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      )
    case 'MultiSelect':
      return (
        <select aria-label="Value" multiple value={Array.isArray(value) ? (value as string[]) : []} onChange={(e) => onChange([...e.target.selectedOptions].map((o) => o.value))}>
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      )
    case 'Checkbox':
      return (
        <label className="check">
          <input type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} /> Yes
        </label>
      )
    case 'Number':
      return <input aria-label="Value" type="number" value={typeof value === 'number' ? value : ''} onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))} />
    case 'Date':
      return <input aria-label="Value" type="date" value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value)} />
    default:
      return <input aria-label="Value" value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value)} />
  }
}

function ActionRow({
  department,
  action,
  onChange,
  onRemove,
  onMove,
}: {
  department: Department
  action: MacroAction
  onChange: (a: MacroAction) => void
  onRemove: () => void
  onMove: (d: -1 | 1) => void
}) {
  const dir = useDirectory(department.id)
  const types = useQuery({ queryKey: ['ticket-types', department.id], queryFn: () => listTicketTypes(department.id) })
  const fields = useQuery({ queryKey: ['ticket-fields', department.id], queryFn: () => listTicketFields(department.id) })
  const canned = useQuery({ queryKey: ['canned', department.id], queryFn: () => listCanned({ departmentId: department.id }) })
  const type = action.type as MacroActionType
  const set = (patch: Partial<MacroAction>) => onChange({ ...action, ...patch })
  const field = (fields.data ?? []).find((f) => f.key === action.key)
  const sharedReplies = (canned.data ?? []).filter((c) => c.scope === 'Department')
  const replyFromCanned = action.value != null

  let control: React.ReactNode = null
  switch (type) {
    case 'setStatus':
      control = (
        <select aria-label="Status" value={action.value ?? ''} onChange={(e) => set({ value: e.target.value })}>
          <option value="">Choose…</option>
          {STATUSES.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
      )
      break
    case 'setPriority':
      control = (
        <select aria-label="Priority" value={action.value ?? ''} onChange={(e) => set({ value: e.target.value })}>
          <option value="">Choose…</option>
          {PRIORITIES.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
      )
      break
    case 'setAssignee':
      control = (
        <select aria-label="Assignee" value={action.value ?? ''} onChange={(e) => set({ value: e.target.value })}>
          <option value="">Choose…</option>
          {dir.members.filter((m) => m.role !== 'Viewer').map((m) => (
            <option key={m.userId} value={m.userId}>
              {m.displayName}
            </option>
          ))}
        </select>
      )
      break
    case 'setType':
      control = (
        <select aria-label="Ticket type" value={action.value ?? ''} onChange={(e) => set({ value: e.target.value })}>
          <option value="">Choose…</option>
          {(types.data ?? []).filter((t) => t.isActive).map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </select>
      )
      break
    case 'setField':
      control = (
        <span>
          <select aria-label="Field" value={action.key ?? ''} onChange={(e) => set({ key: e.target.value, data: undefined })}>
            <option value="">Choose…</option>
            {(fields.data ?? []).filter((f) => f.isActive && f.agentVisible).map((f) => (
              <option key={f.id} value={f.key}>
                {f.label}
              </option>
            ))}
          </select>{' '}
          {field && <FieldValue field={field} value={action.data} onChange={(v) => set({ data: v })} />}
        </span>
      )
      break
    case 'internalNote':
      control = <textarea aria-label="Note" rows={2} maxLength={2000} value={action.text ?? ''} onChange={(e) => set({ text: e.target.value })} />
      break
    case 'publicReply':
      control = (
        <span>
          <select
            aria-label="Reply source"
            value={replyFromCanned ? 'canned' : 'text'}
            onChange={(e) => (e.target.value === 'canned' ? set({ value: '', text: undefined }) : set({ value: undefined, text: '' }))}
          >
            <option value="canned">Canned response</option>
            <option value="text">Text</option>
          </select>{' '}
          {replyFromCanned ? (
            <select aria-label="Canned response" value={action.value ?? ''} onChange={(e) => set({ value: e.target.value })}>
              <option value="">Choose…</option>
              {sharedReplies.filter((c) => c.isActive).map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
          ) : (
            <textarea aria-label="Reply text" rows={3} maxLength={5000} value={action.text ?? ''} onChange={(e) => set({ text: e.target.value })} />
          )}
          <span className="muted small"> Fills the reply box as a draft. Never sent automatically.</span>
        </span>
      )
      break
    default:
      control = <span className="muted small">No options.</span>
  }

  return (
    <div className="macro-action-row">
      <strong>{MACRO_ACTION_LABEL[type] ?? action.type}</strong>
      <div>{control}</div>
      <span>
        <button type="button" className="ghost" aria-label="Move action up" onClick={() => onMove(-1)}>
          ↑
        </button>
        <button type="button" className="ghost" aria-label="Move action down" onClick={() => onMove(1)}>
          ↓
        </button>
        <button type="button" className="ghost" aria-label={`Remove ${MACRO_ACTION_LABEL[type] ?? 'action'}`} onClick={onRemove}>
          ×
        </button>
      </span>
    </div>
  )
}

function MacroDialog({ department, existing, onClose }: { department: Department; existing?: Macro; onClose: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(existing?.name ?? '')
  const [description, setDescription] = useState(existing?.description ?? '')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [actions, setActions] = useState<MacroAction[]>(existing?.actions ?? [])
  const [adding, setAdding] = useState('')
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim(), isActive: active, actions }
      return existing ? updateMacro(department.id, existing.id, { ...body, version: existing.version }) : createMacro(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: key(department.id) })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  const used = new Set(actions.map((a) => (a.type === 'clearAssignee' || a.type === 'assignToMe' || a.type === 'setAssignee' ? 'assignee' : a.type)))
  const offered = MACRO_ACTION_TYPES.filter((t) => t === 'setField' || !used.has(t === 'clearAssignee' || t === 'assignToMe' || t === 'setAssignee' ? 'assignee' : t))
  const move = (i: number, d: -1 | 1) =>
    setActions((cur) => {
      const j = i + d
      if (j < 0 || j >= cur.length) return cur
      const next = [...cur]
      ;[next[i], next[j]] = [next[j], next[i]]
      return next
    })
  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim() && actions.length > 0) save.mutate()
  }
  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New macro'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Macro">
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Description (optional)" error={server.description}>
          {(p) => <input {...p} value={description} maxLength={300} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <fieldset>
          <legend>Actions (run in this order; the result is validated as a whole)</legend>
          {actions.map((a, i) => (
            <div key={i}>
              <ActionRow
                department={department}
                action={a}
                onChange={(next) => setActions((cur) => cur.map((x, j) => (j === i ? next : x)))}
                onRemove={() => setActions((cur) => cur.filter((_, j) => j !== i))}
                onMove={(d) => move(i, d)}
              />
              {server[`actions[${i}]`] && <span className="field-error">{server[`actions[${i}]`]}</span>}
            </div>
          ))}
          {server.actions && <span className="field-error">{server.actions}</span>}
          <label>
            Add action{' '}
            <select
              value={adding}
              onChange={(e) => {
                const type = e.target.value
                setAdding('')
                if (type) setActions((cur) => [...cur, { type, value: type === 'publicReply' ? '' : undefined, text: undefined }])
              }}
            >
              <option value="">+ Add action</option>
              {offered.map((t) => (
                <option key={t} value={t}>
                  {MACRO_ACTION_LABEL[t]}
                </option>
              ))}
            </select>
          </label>
        </fieldset>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active (inactive macros cannot be run)
        </label>
        <ErrorNotice error={save.error && Object.keys(server).length === 0 ? save.error : null} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim() || actions.length === 0}>
            Save macro
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Agent macros of one department: manually run bundles of ticket actions. Not automation rules. */
export default function MacrosSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: key(department.id), queryFn: () => listMacros(department.id) })
  const [dialog, setDialog] = useState<{ existing?: Macro } | null>(null)
  const refresh = () => qc.invalidateQueries({ queryKey: key(department.id) })
  const toggle = useMutation({ mutationFn: (m: Macro) => updateMacro(department.id, m.id, { version: m.version, isActive: !m.isActive }), onSettled: refresh })
  const reorder = useMutation({ mutationFn: (ids: string[]) => reorderMacros(department.id, ids), onSettled: refresh })
  const rows = q.data ?? []
  const move = (i: number, d: -1 | 1) => {
    const ids = rows.map((m) => m.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j], ids[i]]
    reorder.mutate(ids)
  }
  return (
    <section aria-labelledby="macros-h">
      <h2 id="macros-h">Macros</h2>
      <p className="muted">
        A macro is a bundle of ticket actions that an agent runs by hand on a ticket. It is not an automation rule. A public reply action only fills the reply box with a draft for the
        agent to edit; nothing is ever sent automatically.
      </p>
      <p>
        <button type="button" className="primary" onClick={() => setDialog({})}>
          New macro
        </button>
      </p>
      {q.isPending && <Loading label="Loading macros" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && rows.length === 0 && <p className="empty">No macros yet.</p>}
      {rows.length > 0 && (
        <ol className="macro-admin-list">
          {rows.map((m, i) => (
            <li key={m.id}>
              <strong>{m.name}</strong> <ActiveBadge active={m.isActive} />
              {m.description ? <span className="muted"> · {m.description}</span> : null}
              <div className="muted small">{m.summary.join(' · ')}</div>
              <span>
                <button type="button" onClick={() => setDialog({ existing: m })}>
                  Edit<span className="sr-only"> {m.name}</span>
                </button>{' '}
                <button type="button" disabled={toggle.isPending} onClick={() => toggle.mutate(m)}>
                  {m.isActive ? 'Deactivate' : 'Activate'}
                </button>{' '}
                <button type="button" className="ghost" aria-label={`Move ${m.name} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)}>
                  ↑
                </button>
                <button type="button" className="ghost" aria-label={`Move ${m.name} down`} disabled={i === rows.length - 1 || reorder.isPending} onClick={() => move(i, 1)}>
                  ↓
                </button>
              </span>
            </li>
          ))}
        </ol>
      )}
      {dialog && <MacroDialog department={department} existing={dialog.existing} onClose={() => setDialog(null)} />}
    </section>
  )
}
