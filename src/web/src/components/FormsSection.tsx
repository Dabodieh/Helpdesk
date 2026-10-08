import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { lookupUsers } from '../api/organisation'
import type { Department } from '../api/schemas'
import { createForm, listForms, listTicketFields, listTicketTypes, reorderForms, setFormFields, updateForm, type FieldLinkInput, type RequestForm } from '../api/ticketing'
import { PRIORITIES } from '../api/ticketSchemas'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

const formsKey = (d: string) => ['request-forms', d]

const MIN_QUERY = 3

type Person = { userId: string; displayName: string; email?: string | null }

/** Selected audience: a controlled picker over the user directory (no raw ids). Needs the same permission as adding members; the server validates every id. */
function AudiencePicker({ people, onChange }: { people: Person[]; onChange: (p: Person[]) => void }) {
  const [text, setText] = useState('')
  const [q, setQ] = useState('')
  const results = useQuery({ queryKey: ['userLookup', q], queryFn: () => lookupUsers(q), enabled: q.length >= MIN_QUERY })
  const search = () => setQ(text.trim().length >= MIN_QUERY ? text.trim() : '')
  const chosen = new Set(people.map((p) => p.userId))
  return (
    <div className="audience-picker">
      <ul className="link-list" aria-label="Selected people">
        {people.length === 0 && <li className="empty small">Nobody yet: no one can find or submit this form.</li>}
        {people.map((p) => (
          <li key={p.userId} className="link-row">
            <span>
              <strong>{p.displayName}</strong> {p.email && <span className="muted small">{p.email}</span>}
            </span>
            <button type="button" className="ghost" aria-label={`Remove ${p.displayName}`} onClick={() => onChange(people.filter((x) => x.userId !== p.userId))}>
              Remove
            </button>
          </li>
        ))}
      </ul>
      <Field label="Find people to add" hint={`Type at least ${MIN_QUERY} characters of a name or email.`}>
        {(p) => (
          <span className="inline">
            <input
              {...p}
              type="search"
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  search()
                }
              }}
            />
            <button type="button" onClick={search} disabled={text.trim().length < MIN_QUERY}>
              Search
            </button>
          </span>
        )}
      </Field>
      <ErrorNotice error={results.error} />
      {results.isSuccess && results.data.length === 0 && <p className="empty small">No matching people.</p>}
      {results.isSuccess && results.data.length > 0 && (
        <ul className="link-list" aria-label="Search results">
          {results.data.map((u) => (
            <li key={u.id} className="link-row">
              <span>
                <strong>{u.displayName}</strong> {u.email && <span className="muted small">{u.email}</span>}
              </span>
              <button type="button" disabled={chosen.has(u.id)} aria-label={`Add ${u.displayName}`} onClick={() => onChange([...people, { userId: u.id, displayName: u.displayName, email: u.email }])}>
                {chosen.has(u.id) ? 'Added' : 'Add'}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function FormDialog({ department, existing, onClose }: { department: Department; existing?: RequestForm; onClose: () => void }) {
  const qc = useQueryClient()
  const types = useQuery({ queryKey: ['ticket-types', department.id], queryFn: () => listTicketTypes(department.id) })
  const [name, setName] = useState(existing?.name ?? '')
  const [description, setDescription] = useState(existing?.description ?? '')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [visible, setVisible] = useState(existing?.visibleToRequesters ?? true)
  const [priority, setPriority] = useState(existing?.defaultPriority ?? '')
  const [typeId, setTypeId] = useState(existing?.ticketTypeId ?? '')
  // A new form starts restricted: whoever creates it must deliberately choose who may use it.
  const [audience, setAudience] = useState<'AllEmployees' | 'Selected'>(existing?.audience ?? 'Selected')
  const [people, setPeople] = useState<Person[]>(existing?.audienceUsers ?? [])
  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(),
        description: description.trim(),
        isActive: active,
        visibleToRequesters: visible,
        defaultPriority: priority || null,
        clearDefaultPriority: !priority,
        ticketTypeId: typeId || null,
        clearTicketType: !typeId,
        audience,
        audienceUserIds: people.map((p) => p.userId),
      }
      return existing ? updateForm(department.id, existing.id, { ...body, version: existing.version }) : createForm(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: formsKey(department.id) })
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
        <Field label="Ticket type" hint="The kind of ticket this form creates. Without one, the department default type applies." error={server.tickettypeid}>
          {(p) => (
            <select {...p} value={typeId} onChange={(e) => setTypeId(e.target.value)}>
              <option value="">Department default</option>
              {(types.data ?? [])
                .filter((t) => t.isActive || t.id === existing?.ticketTypeId)
                .map((t) => (
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
        <fieldset className="audience">
          <legend>Requester access</legend>
          <p className="muted small">Who can find and submit this form? This does not give anyone access to the department, its tickets or its settings.</p>
          <label className="check">
            <input type="radio" name="audience" checked={audience === 'AllEmployees'} onChange={() => setAudience('AllEmployees')} /> All employees
          </label>
          <label className="check">
            <input type="radio" name="audience" checked={audience === 'Selected'} onChange={() => setAudience('Selected')} /> Selected people only
          </label>
          {audience === 'Selected' && <AudiencePicker people={people} onChange={setPeople} />}
          {(server.audience ?? server.audienceuserids) && <p className="field-error" role="alert">{(server.audience ?? server.audienceuserids)![0]}</p>}
        </fieldset>
        <label className="check">
          <input type="checkbox" checked={visible} onChange={(e) => setVisible(e.target.checked)} /> Offer this form in the requester portal
        </label>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active
        </label>
        <p className="muted small">Requesters never choose the assignee, priority or department: the form decides.</p>
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

type Row = { fieldId: string; required: boolean | null }

/** Which ticket fields a form asks, in which order, with an optional requirement override. */
function FormFieldsDialog({ department, form, onClose }: { department: Department; form: RequestForm; onClose: () => void }) {
  const qc = useQueryClient()
  const fields = useQuery({ queryKey: ['ticket-fields', department.id], queryFn: () => listTicketFields(department.id) })
  const [rows, setRows] = useState<Row[]>(() => form.fields.map((f) => ({ fieldId: f.fieldId, required: f.requiredOverride })))
  const save = useMutation({
    mutationFn: () => setFormFields(department.id, form.id, rows.map<FieldLinkInput>((r) => ({ fieldId: r.fieldId, required: r.required }))),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: formsKey(department.id) })
      await qc.invalidateQueries({ queryKey: ['ticket-fields', department.id] })
      onClose()
    },
  })
  const all = fields.data ?? []
  const byId = new Map(all.map((f) => [f.id, f]))
  const available = all.filter((f) => f.isActive && !rows.some((r) => r.fieldId === f.id))
  const move = (i: number, d: -1 | 1) => {
    const j = i + d
    if (j < 0 || j >= rows.length) return
    const next = [...rows]
    ;[next[i], next[j]] = [next[j]!, next[i]!]
    setRows(next)
  }
  return (
    <Dialog title={`Fields of ${form.name}`} onClose={onClose}>
      <form className="dialog-form" aria-label="Form fields" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        {fields.isPending && <Loading label="Loading fields" />}
        <ErrorNotice error={fields.error} />
        {rows.length === 0 && <p className="empty small">No fields. Requesters only give a subject and a description.</p>}
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
                  {f && !f.requesterVisible && <span className="badge">Agent only: not asked</span>}
                </span>
                <select
                  aria-label={`Requirement for ${label}`}
                  value={r.required === null ? '' : r.required ? 'yes' : 'no'}
                  onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, required: e.target.value === '' ? null : e.target.value === 'yes' } : x)))}
                >
                  <option value="">{f?.required ? 'Required (field default)' : 'Optional (field default)'}</option>
                  <option value="yes">Required on this form</option>
                  <option value="no">Optional on this form</option>
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

/** Request forms: how a requester submits a kind of ticket (permission department.forms.manage). */
export default function FormsSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: formsKey(department.id), queryFn: () => listForms(department.id) })
  const types = useQuery({ queryKey: ['ticket-types', department.id], queryFn: () => listTicketTypes(department.id) })
  const [editing, setEditing] = useState<RequestForm | 'new' | null>(null)
  const [fieldsFor, setFieldsFor] = useState<RequestForm | null>(null)
  const reorder = useMutation({ mutationFn: (ids: string[]) => reorderForms(department.id, ids), onSuccess: (d) => qc.setQueryData(formsKey(department.id), d) })
  const forms = q.data ?? []
  const typeName = (id?: string | null) => (types.data ?? []).find((t) => t.id === id)?.name
  const move = (i: number, d: -1 | 1) => {
    const ids = forms.map((f) => f.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorder.mutate(ids)
  }
  return (
    <section aria-labelledby="forms-h">
      <div className="section-head">
        <h2 id="forms-h">Request forms</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New request form
        </button>
      </div>
      <p className="muted">
        A form is how requesters submit a kind of ticket: it picks the ticket type and which of the department&rsquo;s fields to ask. Tickets keep the answers they were given even if a form changes later.
      </p>
      {q.isPending && <Loading label="Loading request forms" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && forms.length === 0 && <p className="empty">No request forms yet. Requesters cannot submit requests to this department until you add one.</p>}
      {forms.map((f, i) => (
        <div key={f.id} className="form-card">
          <div className="form-card-head">
            <span className="row-actions">
              <button type="button" className="ghost" aria-label={`Move ${f.name} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)}>
                ↑
              </button>
              <button type="button" className="ghost" aria-label={`Move ${f.name} down`} disabled={i === forms.length - 1 || reorder.isPending} onClick={() => move(i, 1)}>
                ↓
              </button>
            </span>
            <div>
              <strong>{f.name}</strong> <ActiveBadge active={f.isActive} /> {!f.visibleToRequesters && <span className="badge">Hidden from portal</span>}{' '}
              <span className="badge">{f.audience === 'AllEmployees' ? 'All employees' : `Restricted: ${f.audienceUsers.length} ${f.audienceUsers.length === 1 ? 'person' : 'people'}`}</span>
              <p className="muted small">
                {typeName(f.ticketTypeId) ? `Type ${typeName(f.ticketTypeId)} · ` : ''}
                {f.fields.length} {f.fields.length === 1 ? 'field' : 'fields'}
                {f.defaultPriority ? ` · default priority ${f.defaultPriority}` : ''}
              </p>
            </div>
            <span className="row-actions">
              <button type="button" onClick={() => setEditing(f)}>
                Edit<span className="sr-only"> {f.name}</span>
              </button>
              <button type="button" onClick={() => setFieldsFor(f)}>
                Fields
              </button>
            </span>
          </div>
        </div>
      ))}
      <ErrorNotice error={reorder.error} />
      {editing && <FormDialog department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
      {fieldsFor && <FormFieldsDialog department={department} form={fieldsFor} onClose={() => setFieldsFor(null)} />}
    </section>
  )
}
