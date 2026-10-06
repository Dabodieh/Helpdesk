import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { getTicketProperties, updateTicketProperties, type TicketProperties as Properties, type TicketProperty } from '../api/ticketing'
import { livePolling } from '../queries'
import { ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'
import { FieldControl } from './FieldControl'

const sameValue = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)
const isEmpty = (v: unknown) => v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0)

/** Type and configured fields of the ticket. Read-only list with a focused editor; the editor touches nothing but these. */
export default function TicketProperties({ reference, canEdit }: { reference: string; canEdit: boolean }) {
  const [editing, setEditing] = useState(false)
  const q = useQuery({ queryKey: ['ticket', reference, 'properties'], queryFn: () => getTicketProperties(reference), ...livePolling })
  const p = q.data
  return (
    <details className="t-props">
      <summary>
        Details
        {p && <span className="muted small"> · {p.type ? p.type.displayName : 'No type'}</span>}
        {p?.fields.some((f) => f.missingRequired) && <span className="badge"> Needs details</span>}
      </summary>
      {q.isPending && <Loading label="Loading details" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {p && (
        <dl className="t-meta">
          <div>
            <dt>Type</dt>
            <dd>{p.type ? p.type.displayName : <span className="muted">Not set</span>}</dd>
          </div>
          {p.fields.map((f) => (
            <div key={f.key}>
              <dt>{f.label}</dt>
              <dd>
                {f.display ? f.display : f.missingRequired ? <span className="muted">Required, not set</span> : <span className="muted">Not set</span>}
                {f.display && !f.active && <span className="muted small"> (field retired)</span>}
              </dd>
            </div>
          ))}
        </dl>
      )}
      {canEdit && p && (
        <p>
          <button type="button" onClick={() => setEditing(true)}>
            Edit details
          </button>
        </p>
      )}
      {editing && p && <DetailsDialog reference={reference} properties={p} onClose={() => setEditing(false)} />}
    </details>
  )
}

function DetailsDialog({ reference, properties, onClose }: { reference: string; properties: Properties; onClose: () => void }) {
  const qc = useQueryClient()
  const editable = properties.fields.filter((f) => f.editable)
  const [typeId, setTypeId] = useState(properties.type?.id ?? '')
  const [values, setValues] = useState<Record<string, unknown>>(() => Object.fromEntries(editable.map((f) => [f.key, f.value ?? undefined])))
  const typeChanged = typeId !== (properties.type?.id ?? '')
  const save = useMutation({
    mutationFn: () => {
      const fields: Record<string, unknown> = {}
      for (const f of editable) {
        if (!sameValue(values[f.key], f.value)) fields[f.key] = isEmpty(values[f.key]) ? null : values[f.key]
      }
      return updateTicketProperties(reference, {
        version: properties.version,
        ...(typeChanged ? (typeId ? { ticketTypeId: typeId } : { clearTicketType: true }) : {}),
        ...(Object.keys(fields).length > 0 ? { fields } : {}),
      })
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['ticket'] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  const dirty = typeChanged || editable.some((f) => !sameValue(values[f.key], f.value))
  function submit(e: FormEvent) {
    e.preventDefault()
    if (dirty) save.mutate()
  }
  const spec = (f: TicketProperty) => ({ key: f.key, label: f.label, helpText: f.helpText, type: f.type, required: f.required, options: f.options.filter((o) => o.isActive || sameValue(o.value, f.value)) })
  return (
    <Dialog title="Edit ticket details" onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Ticket details">
        <Field label="Type" hint={typeChanged ? 'The fields of the new type appear after you save.' : undefined} error={server.tickettypeid}>
          {(p) => (
            <select {...p} value={typeId} onChange={(e) => setTypeId(e.target.value)}>
              <option value="">No type</option>
              {properties.types
                .filter((t) => t.isActive || t.id === properties.type?.id)
                .map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                    {t.isActive ? '' : ' (retired)'}
                  </option>
                ))}
            </select>
          )}
        </Field>
        {editable.map((f) => (
          <FieldControl key={f.key} field={spec(f)} value={values[f.key]} onChange={(v) => setValues({ ...values, [f.key]: v })} error={server[`fields.${f.key}`]} />
        ))}
        {editable.length === 0 && <p className="muted small">This type has no fields to fill in.</p>}
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !dirty}>
            Save details
          </button>
        </div>
      </form>
    </Dialog>
  )
}
