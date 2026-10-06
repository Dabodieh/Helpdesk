import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { createField, createForm, FIELD_TYPE_LABEL, FIELD_TYPES, listForms, reorderFields, reorderForms, updateField, updateForm, type FormField, type RequestForm } from '../api/formsAdmin'
import { PRIORITIES } from '../api/ticketSchemas'
import type { Department } from '../api/schemas'
import { useDirectory } from '../hooks/useDirectory'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

function FormDialog({ department, existing, onClose }: { department: Department; existing?: RequestForm; onClose: () => void }) {
  const qc = useQueryClient()
  const dir = useDirectory(department.id)
  const [name, setName] = useState(existing?.name ?? '')
  const [description, setDescription] = useState(existing?.description ?? '')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [visible, setVisible] = useState(existing?.visibleToRequesters ?? true)
  const [team, setTeam] = useState(existing?.defaultTeamId ?? '')
  const [priority, setPriority] = useState(existing?.defaultPriority ?? '')
  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(),
        description: description.trim(),
        isActive: active,
        visibleToRequesters: visible,
        defaultTeamId: team || null,
        clearDefaultTeam: !team,
        defaultPriority: priority || null,
        clearDefaultPriority: !priority,
      }
      return existing ? updateForm(department.id, existing.id, { ...body, version: existing.version }) : createForm(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['request-forms', department.id] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim()) save.mutate()
  }
  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New request form'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Request form">
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Description shown to requesters" error={server.description}>
          {(p) => <textarea {...p} rows={2} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <Field label="Default team" hint="Tickets from this form start with this team (routing rules can change it)." error={server.defaultteamid}>
          {(p) => (
            <select {...p} value={team} onChange={(e) => setTeam(e.target.value)}>
              <option value="">No team</option>
              {dir.teams.filter((t) => t.isActive).map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label="Default priority" error={server.defaultpriority}>
          {(p) => (
            <select {...p} value={priority} onChange={(e) => setPriority(e.target.value)}>
              <option value="">Normal</option>
              {PRIORITIES.map((x) => (
                <option key={x}>{x}</option>
              ))}
            </select>
          )}
        </Field>
        <label className="check">
          <input type="checkbox" checked={visible} onChange={(e) => setVisible(e.target.checked)} /> Offer this form in the requester portal
        </label>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active
        </label>
        <p className="muted small">Requesters never choose the team, priority or department: the form decides.</p>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim()}>
            Save form
          </button>
        </div>
      </form>
    </Dialog>
  )
}

function FieldDialog({ department, form, existing, onClose }: { department: Department; form: RequestForm; existing?: FormField; onClose: () => void }) {
  const qc = useQueryClient()
  const [label, setLabel] = useState(existing?.label ?? '')
  const [help, setHelp] = useState(existing?.helpText ?? '')
  const [type, setType] = useState(existing?.type ?? 'ShortText')
  const [required, setRequired] = useState(existing?.required ?? false)
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [requesterVisible, setRequesterVisible] = useState(existing?.requesterVisible ?? true)
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
        requesterVisible,
        agentVisible,
        options: hasOptions ? options.filter((o) => o.label.trim()).map((o) => ({ value: o.value, label: o.label.trim(), isActive: o.isActive ?? true })) : undefined,
      }
      return existing ? updateField(department.id, form.id, existing.id, { ...body, version: existing.version }) : createField(department.id, form.id, { ...body, type })
    },
    onSuccess: async () => {
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
    <Dialog title={existing ? `Edit field “${existing.label}”` : `New field for ${form.name}`} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Form field">
        <Field label="Label shown to requesters" error={server.label}>
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
            <span className="muted small">Choices that are already used by tickets can be renamed or made unavailable, not removed.</span>
          </fieldset>
        )}
        <label className="check">
          <input type="checkbox" checked={required} onChange={(e) => setRequired(e.target.checked)} /> Required
        </label>
        <label className="check">
          <input type="checkbox" checked={requesterVisible} onChange={(e) => setRequesterVisible(e.target.checked)} /> Asked in the requester portal
        </label>
        <label className="check">
          <input type="checkbox" checked={agentVisible} onChange={(e) => setAgentVisible(e.target.checked)} /> Shown to agents on the ticket
        </label>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active (inactive fields keep their values on existing tickets)
        </label>
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

/** Department request forms and their custom fields (permission department.forms.manage). */
export default function FormsSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['request-forms', department.id], queryFn: () => listForms(department.id) })
  const [editing, setEditing] = useState<RequestForm | 'new' | null>(null)
  const [fieldDialog, setFieldDialog] = useState<{ form: RequestForm; field?: FormField } | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const reorderF = useMutation({ mutationFn: (ids: string[]) => reorderForms(department.id, ids), onSuccess: (d) => qc.setQueryData(['request-forms', department.id], d) })
  const reorderFields_ = useMutation({
    mutationFn: ({ formId, ids }: { formId: string; ids: string[] }) => reorderFields(department.id, formId, ids),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['request-forms', department.id] }),
  })
  const forms = q.data ?? []
  const moveForm = (i: number, d: -1 | 1) => {
    const ids = forms.map((f) => f.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorderF.mutate(ids)
  }
  const moveField = (form: RequestForm, i: number, d: -1 | 1) => {
    const ids = form.fields.map((f) => f.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorderFields_.mutate({ formId: form.id, ids })
  }
  return (
    <section aria-labelledby="forms-h">
      <div className="section-head">
        <h2 id="forms-h">Request forms</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New request form
        </button>
      </div>
      <p className="muted">Structured ways for requesters to open a normal ticket. Forms can add fields; tickets keep the answers they were given even if a form changes later.</p>
      {q.isPending && <Loading label="Loading request forms" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && forms.length === 0 && <p className="empty">No request forms yet. Requesters cannot submit requests to this department until you add one.</p>}
      {forms.map((f, i) => (
        <div key={f.id} className="form-card">
          <div className="form-card-head">
            <span className="row-actions">
              <button type="button" className="ghost" aria-label={`Move ${f.name} up`} disabled={i === 0 || reorderF.isPending} onClick={() => moveForm(i, -1)}>
                ↑
              </button>
              <button type="button" className="ghost" aria-label={`Move ${f.name} down`} disabled={i === forms.length - 1 || reorderF.isPending} onClick={() => moveForm(i, 1)}>
                ↓
              </button>
            </span>
            <div>
              <strong>{f.name}</strong> <ActiveBadge active={f.isActive} /> {!f.visibleToRequesters && <span className="badge">Hidden from portal</span>}
              <p className="muted small">
                {f.fields.length} {f.fields.length === 1 ? 'field' : 'fields'}
                {f.defaultPriority ? ` · default priority ${f.defaultPriority}` : ''}
              </p>
            </div>
            <span className="row-actions">
              <button type="button" onClick={() => setEditing(f)}>
                Edit
              </button>
              <button type="button" aria-expanded={open === f.id} onClick={() => setOpen(open === f.id ? null : f.id)}>
                Fields
              </button>
            </span>
          </div>
          {open === f.id && (
            <div className="form-card-body">
              {f.fields.length === 0 && <p className="empty small">No custom fields. Requesters only give a subject and a description.</p>}
              {f.fields.length > 0 && (
                <table className="grid">
                  <caption className="sr-only">Fields of {f.name}</caption>
                  <thead>
                    <tr>
                      <th scope="col">Order</th>
                      <th scope="col">Field</th>
                      <th scope="col">Type</th>
                      <th scope="col">Shown to</th>
                      <th scope="col">Status</th>
                      <th scope="col" />
                    </tr>
                  </thead>
                  <tbody>
                    {f.fields.map((x, n) => (
                      <tr key={x.id}>
                        <td className="row-actions">
                          <button type="button" className="ghost" aria-label={`Move ${x.label} up`} disabled={n === 0 || reorderFields_.isPending} onClick={() => moveField(f, n, -1)}>
                            ↑
                          </button>
                          <button type="button" className="ghost" aria-label={`Move ${x.label} down`} disabled={n === f.fields.length - 1 || reorderFields_.isPending} onClick={() => moveField(f, n, 1)}>
                            ↓
                          </button>
                        </td>
                        <td>
                          <strong>{x.label}</strong> {x.required && <span className="badge">Required</span>}
                          <div className="muted small">{x.key}</div>
                        </td>
                        <td>{FIELD_TYPE_LABEL[x.type] ?? x.type}</td>
                        <td className="small">{[x.requesterVisible ? 'Requesters' : null, x.agentVisible ? 'Agents' : null].filter(Boolean).join(', ') || 'Nobody'}</td>
                        <td>
                          <ActiveBadge active={x.isActive} />
                        </td>
                        <td className="row-actions">
                          <button type="button" onClick={() => setFieldDialog({ form: f, field: x })}>
                            Edit
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <p>
                <button type="button" onClick={() => setFieldDialog({ form: f })}>
                  Add field
                </button>
              </p>
            </div>
          )}
        </div>
      ))}
      <ErrorNotice error={reorderF.error ?? reorderFields_.error} />
      {editing && <FormDialog department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
      {fieldDialog && <FieldDialog department={department} form={fieldDialog.form} existing={fieldDialog.field} onClose={() => setFieldDialog(null)} />}
    </section>
  )
}
