import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import type { Department } from '../api/schemas'
import { createTicketField, FIELD_TYPE_LABEL, FIELD_TYPES, listTicketFields, reorderTicketFields, updateTicketField, type TicketField } from '../api/ticketing'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

const fieldsKey = (d: string) => ['ticket-fields', d]

function FieldDialog({ department, existing, onClose }: { department: Department; existing?: TicketField; onClose: () => void }) {
  const qc = useQueryClient()
  const [label, setLabel] = useState(existing?.label ?? '')
  const [help, setHelp] = useState(existing?.helpText ?? '')
  const [type, setType] = useState(existing?.type ?? 'ShortText')
  const [required, setRequired] = useState(existing?.required ?? false)
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [common, setCommon] = useState(existing?.appliesToAllTypes ?? false)
  const [requesterVisible, setRequesterVisible] = useState(existing?.requesterVisible ?? true)
  const [requesterEditable, setRequesterEditable] = useState(existing?.requesterEditable ?? false)
  const [agentVisible, setAgentVisible] = useState(existing?.agentVisible ?? true)
  const [options, setOptions] = useState<{ value?: string; label: string; isActive?: boolean }[]>(existing?.options ?? [{ label: '' }])
  const hasOptions = type === 'SingleSelect' || type === 'MultiSelect'
  const save = useMutation({
    mutationFn: () => {
      const body = {
        label: label.trim(),
        helpText: help.trim(),
        required,
        isActive: active,
        appliesToAllTypes: common,
        requesterVisible,
        requesterEditable: requesterVisible && requesterEditable,
        agentVisible,
        options: hasOptions ? options.filter((o) => o.label.trim()).map((o) => ({ value: o.value, label: o.label.trim(), isActive: o.isActive ?? true })) : undefined,
      }
      return existing ? updateTicketField(department.id, existing.id, { ...body, version: existing.version }) : createTicketField(department.id, { ...body, type })
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: fieldsKey(department.id) })
      await qc.invalidateQueries({ queryKey: ['ticket-types', department.id] })
      await qc.invalidateQueries({ queryKey: ['request-forms', department.id] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (label.trim()) save.mutate()
  }
  return (
    <Dialog title={existing ? `Edit field “${existing.label}”` : 'New ticket field'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Ticket field">
        <Field label="Label" error={server.label}>
          {(p) => <input {...p} data-autofocus value={label} maxLength={100} required onChange={(e) => setLabel(e.target.value)} />}
        </Field>
        <Field label="Help text (optional)" error={server.helptext}>
          {(p) => <input {...p} value={help} maxLength={500} onChange={(e) => setHelp(e.target.value)} />}
        </Field>
        <Field label="Type" hint={existing ? `The type cannot be changed. Technical key: ${existing.key}` : undefined} error={server.type}>
          {(p) => (
            <select {...p} value={type} disabled={!!existing} onChange={(e) => setType(e.target.value)}>
              {FIELD_TYPES.map((t) => (
                <option key={t} value={t}>
                  {FIELD_TYPE_LABEL[t]}
                </option>
              ))}
            </select>
          )}
        </Field>
        {hasOptions && (
          <fieldset className="editor">
            <legend>Choices</legend>
            {options.map((o, i) => (
              <div key={i} className="editor-row">
                <input
                  aria-label={`Choice ${i + 1}`}
                  value={o.label}
                  maxLength={100}
                  onChange={(e) => setOptions(options.map((x, j) => (j === i ? { ...x, label: e.target.value } : x)))}
                />
                {o.value ? (
                  <label className="check">
                    <input type="checkbox" checked={o.isActive ?? true} onChange={(e) => setOptions(options.map((x, j) => (j === i ? { ...x, isActive: e.target.checked } : x)))} /> Available
                  </label>
                ) : (
                  <button type="button" className="ghost" onClick={() => setOptions(options.filter((_, j) => j !== i))} aria-label={`Remove choice ${i + 1}`}>
                    Remove
                  </button>
                )}
              </div>
            ))}
            {(server.options || Object.keys(server).some((k) => k.startsWith('options'))) && <span className="field-error">{server.options ?? 'Check the choices.'}</span>}
            <button type="button" onClick={() => setOptions([...options, { label: '' }])}>
              Add choice
            </button>
            <span className="muted small">Choices that tickets already use can be renamed or made unavailable, not removed. Renaming never changes stored answers.</span>
          </fieldset>
        )}
        <label className="check">
          <input type="checkbox" checked={common} onChange={(e) => setCommon(e.target.checked)} /> Common field: asked on every ticket whatever its type
        </label>
        <label className="check">
          <input type="checkbox" checked={required} onChange={(e) => setRequired(e.target.checked)} /> Required by default (a type or form can change this)
        </label>
        <label className="check">
          <input type="checkbox" checked={agentVisible} onChange={(e) => setAgentVisible(e.target.checked)} /> Shown to and editable by agents
        </label>
        <label className="check">
          <input type="checkbox" checked={requesterVisible} onChange={(e) => setRequesterVisible(e.target.checked)} /> Visible to requesters (asked on portal forms, shown on their ticket)
        </label>
        <label className="check">
          <input type="checkbox" checked={requesterVisible && requesterEditable} disabled={!requesterVisible} onChange={(e) => setRequesterEditable(e.target.checked)} /> Requesters can change it after submitting
        </label>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active (inactive fields keep their values on existing tickets)
        </label>
        <p className="muted small">Making a field required never changes tickets that already exist; it applies when a ticket is created or when someone edits the field.</p>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !label.trim()}>
            Save field
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** The department's ticket field definitions: one place to define, then place them on types and forms (permission department.forms.manage). */
export default function TicketFieldsSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: fieldsKey(department.id), queryFn: () => listTicketFields(department.id) })
  const [editing, setEditing] = useState<TicketField | 'new' | null>(null)
  const reorder = useMutation({ mutationFn: (ids: string[]) => reorderTicketFields(department.id, ids), onSuccess: (d) => qc.setQueryData(fieldsKey(department.id), d) })
  const fields = q.data ?? []
  const move = (i: number, d: -1 | 1) => {
    const ids = fields.map((f) => f.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorder.mutate(ids)
  }
  return (
    <section aria-labelledby="fields-h">
      <div className="section-head">
        <h2 id="fields-h">Ticket fields</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New ticket field
        </button>
      </div>
      <p className="muted">Define a field once, then use it on ticket types and request forms. Tickets keep the answers they were given even if a field is renamed or retired.</p>
      {q.isPending && <Loading label="Loading ticket fields" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && fields.length === 0 && <p className="empty">No ticket fields yet.</p>}
      {fields.length > 0 && (
        <table className="grid">
          <caption className="sr-only">Ticket fields</caption>
          <thead>
            <tr>
              <th scope="col">Order</th>
              <th scope="col">Field</th>
              <th scope="col">Type</th>
              <th scope="col">Used on</th>
              <th scope="col">Shown to</th>
              <th scope="col">Status</th>
              <th scope="col" />
            </tr>
          </thead>
          <tbody>
            {fields.map((x, i) => (
              <tr key={x.id}>
                <td className="row-actions">
                  <button type="button" className="ghost" aria-label={`Move ${x.label} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)}>
                    ↑
                  </button>
                  <button type="button" className="ghost" aria-label={`Move ${x.label} down`} disabled={i === fields.length - 1 || reorder.isPending} onClick={() => move(i, 1)}>
                    ↓
                  </button>
                </td>
                <td>
                  <strong>{x.label}</strong> {x.required && <span className="badge">Required</span>}
                  <div className="muted small">{x.key}</div>
                </td>
                <td>{FIELD_TYPE_LABEL[x.type] ?? x.type}</td>
                <td className="small">
                  {x.appliesToAllTypes && <div>All types</div>}
                  {x.types.length > 0 && <div>Types: {x.types.map((t) => t.name).join(', ')}</div>}
                  {x.forms.length > 0 && <div>Forms: {x.forms.map((t) => t.name).join(', ')}</div>}
                  {!x.appliesToAllTypes && x.types.length === 0 && x.forms.length === 0 && <span className="muted">Not used yet</span>}
                </td>
                <td className="small">
                  {[x.requesterVisible ? (x.requesterEditable ? 'Requesters (editable)' : 'Requesters') : null, x.agentVisible ? 'Agents' : null].filter(Boolean).join(', ') || 'Nobody'}
                </td>
                <td>
                  <ActiveBadge active={x.isActive} />
                </td>
                <td className="row-actions">
                  <button type="button" onClick={() => setEditing(x)}>
                    Edit
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ErrorNotice error={reorder.error} />
      {editing && <FieldDialog department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
    </section>
  )
}
