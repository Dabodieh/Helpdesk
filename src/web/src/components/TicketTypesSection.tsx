import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import type { Department } from '../api/schemas'
import { createTicketType, listTicketFields, listTicketTypes, reorderTicketTypes, setTypeFields, updateTicketType, type FieldLinkInput, type TicketType } from '../api/ticketing'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

const typesKey = (d: string) => ['ticket-types', d]

function TypeDialog({ department, existing, onClose }: { department: Department; existing?: TicketType; onClose: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(existing?.name ?? '')
  const [description, setDescription] = useState(existing?.description ?? '')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [isDefault, setIsDefault] = useState(existing?.isDefault ?? false)
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim(), isActive: active, isDefault: active && isDefault }
      return existing ? updateTicketType(department.id, existing.id, { ...body, version: existing.version }) : createTicketType(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: typesKey(department.id) })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim()) save.mutate()
  }
  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New ticket type'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Ticket type">
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Description (optional)" error={server.description}>
          {(p) => <textarea {...p} rows={2} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <label className="check">
          <input type="checkbox" checked={isDefault && active} disabled={!active} onChange={(e) => setIsDefault(e.target.checked)} /> Default type (used when email or a form does not choose one)
        </label>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active (retired types stay on the tickets that already have them)
        </label>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim()}>
            Save type
          </button>
        </div>
      </form>
    </Dialog>
  )
}

type Row = { fieldId: string; required: boolean | null }

function FieldsDialog({ department, type, onClose }: { department: Department; type: TicketType; onClose: () => void }) {
  const qc = useQueryClient()
  const fields = useQuery({ queryKey: ['ticket-fields', department.id], queryFn: () => listTicketFields(department.id) })
  const [rows, setRows] = useState<Row[]>(() => type.fields.map((f) => ({ fieldId: f.fieldId, required: f.requiredOverride })))
  const save = useMutation({
    mutationFn: () => setTypeFields(department.id, type.id, rows.map<FieldLinkInput>((r) => ({ fieldId: r.fieldId, required: r.required }))),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: typesKey(department.id) })
      await qc.invalidateQueries({ queryKey: ['ticket-fields', department.id] })
      onClose()
    },
  })
  const all = fields.data ?? []
  const byId = new Map(all.map((f) => [f.id, f]))
  const common = all.filter((f) => f.appliesToAllTypes && f.isActive)
  const available = all.filter((f) => !f.appliesToAllTypes && f.isActive && !rows.some((r) => r.fieldId === f.id))
  const move = (i: number, d: -1 | 1) => {
    const j = i + d
    if (j < 0 || j >= rows.length) return
    const next = [...rows]
    ;[next[i], next[j]] = [next[j]!, next[i]!]
    setRows(next)
  }
  return (
    <Dialog title={`Fields of ${type.name}`} onClose={onClose}>
      <form className="dialog-form" aria-label="Type fields" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        {fields.isPending && <Loading label="Loading fields" />}
        <ErrorNotice error={fields.error} />
        {common.length > 0 && (
          <p className="muted small">
            Common fields apply to every type and are asked first: {common.map((f) => f.label).join(', ')}.
          </p>
        )}
        {rows.length === 0 && <p className="empty small">No type-specific fields yet.</p>}
        <ul className="link-list">
          {rows.map((r, i) => {
            const f = byId.get(r.fieldId)
            const label = f?.label ?? r.fieldId
            return (
              <li key={r.fieldId} className="link-row">
                <span className="row-actions">
                  <button type="button" className="ghost" aria-label={`Move ${label} up`} disabled={i === 0} onClick={() => move(i, -1)}>
                    ↑
                  </button>
                  <button type="button" className="ghost" aria-label={`Move ${label} down`} disabled={i === rows.length - 1} onClick={() => move(i, 1)}>
                    ↓
                  </button>
                </span>
                <span>
                  <strong>{label}</strong> {f && !f.isActive && <span className="badge">Inactive</span>}
                </span>
                <select
                  aria-label={`Requirement for ${label}`}
                  value={r.required === null ? '' : r.required ? 'yes' : 'no'}
                  onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, required: e.target.value === '' ? null : e.target.value === 'yes' } : x)))}
                >
                  <option value="">{f?.required ? 'Required (field default)' : 'Optional (field default)'}</option>
                  <option value="yes">Required for this type</option>
                  <option value="no">Optional for this type</option>
                </select>
                <button type="button" className="ghost" aria-label={`Remove ${label}`} onClick={() => setRows(rows.filter((_, j) => j !== i))}>
                  Remove
                </button>
              </li>
            )
          })}
        </ul>
        {available.length > 0 && (
          <Field label="Add a field">
            {(p) => (
              <select {...p} value="" onChange={(e) => e.target.value && setRows([...rows, { fieldId: e.target.value, required: null }])}>
                <option value="">Choose a field…</option>
                {available.map((f) => (
                  <option key={f.id} value={f.id}>
                    {f.label}
                  </option>
                ))}
              </select>
            )}
          </Field>
        )}
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending}>
            Save fields
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Ticket types: what kind of ticket this is, and which fields that kind asks (permission department.forms.manage). */
export default function TicketTypesSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: typesKey(department.id), queryFn: () => listTicketTypes(department.id) })
  const [editing, setEditing] = useState<TicketType | 'new' | null>(null)
  const [fieldsFor, setFieldsFor] = useState<TicketType | null>(null)
  const reorder = useMutation({ mutationFn: (ids: string[]) => reorderTicketTypes(department.id, ids), onSuccess: (d) => qc.setQueryData(typesKey(department.id), d) })
  const types = q.data ?? []
  const move = (i: number, d: -1 | 1) => {
    const ids = types.map((t) => t.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorder.mutate(ids)
  }
  return (
    <section aria-labelledby="types-h">
      <div className="section-head">
        <h2 id="types-h">Ticket types</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New ticket type
        </button>
      </div>
      <p className="muted">
        A type says what kind of ticket this is (for example Incident or Access request). It chooses which extra fields agents see. Request forms say how a requester submits that kind of ticket.
      </p>
      {q.isPending && <Loading label="Loading ticket types" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && types.length === 0 && <p className="empty">No ticket types yet. Tickets are created without a type until you add one.</p>}
      {types.map((t, i) => (
        <div key={t.id} className="form-card">
          <div className="form-card-head">
            <span className="row-actions">
              <button type="button" className="ghost" aria-label={`Move ${t.name} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)}>
                ↑
              </button>
              <button type="button" className="ghost" aria-label={`Move ${t.name} down`} disabled={i === types.length - 1 || reorder.isPending} onClick={() => move(i, 1)}>
                ↓
              </button>
            </span>
            <div>
              <strong>{t.name}</strong> <ActiveBadge active={t.isActive} /> {t.isDefault && <span className="badge">Default</span>}
              <p className="muted small">
                {t.description ? `${t.description} · ` : ''}
                {t.fields.length} {t.fields.length === 1 ? 'field' : 'fields'}
                {t.fields.length > 0 ? `: ${t.fields.map((f) => f.label).join(', ')}` : ''}
              </p>
            </div>
            <span className="row-actions">
              <button type="button" onClick={() => setEditing(t)}>
                Edit<span className="sr-only"> {t.name}</span>
              </button>
              <button type="button" onClick={() => setFieldsFor(t)}>
                Fields
              </button>
            </span>
          </div>
        </div>
      ))}
      <ErrorNotice error={reorder.error} />
      {editing && <TypeDialog department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
      {fieldsFor && <FieldsDialog department={department} type={fieldsFor} onClose={() => setFieldsFor(null)} />}
    </section>
  )
}
