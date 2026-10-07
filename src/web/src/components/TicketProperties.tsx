import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { updateTicketProperties, type TicketProperties as Properties, type TicketProperty } from '../api/ticketing'
import { useTicketProperties } from '../hooks/useTicketProperties'
import { ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'
import { FieldControl } from './FieldControl'

const sameValue = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)
const isEmpty = (v: unknown) => v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0)

/** Configured field values as Label / Value rows. Missing required values are flagged, retired values stay readable. Editing opens the structured dialog. */
export default function FieldSummary({ reference, canEdit, onEdit }: { reference: string; canEdit: boolean; onEdit: () => void }) {
  const q = useTicketProperties(reference)
  const p = q.data
  return (
    <section className="prop-section" aria-label="Details">
      <h3>
        Details
        {p?.fields.some((f) => f.missingRequired) && <span className="badge warn"> Needs details</span>}
      </h3>
      {q.isPending && <div className="skeleton" aria-hidden="true" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {p && (
        <dl className="prop-list">
          <div>
            <dt>Type</dt>
            <dd>{p.type ? p.type.displayName : <span className="muted">Not set</span>}</dd>
          </div>
          {p.fields.map((f) => (
            <div key={f.key}>
              <dt>{f.label}</dt>
              <dd>
                {f.display ? (
                  f.display
                ) : f.missingRequired ? (
                  <span className="missing">Required · Not set</span>
                ) : (
                  <span className="muted">Not set</span>
                )}
                {f.display && !f.active && <span className="muted small"> (field retired)</span>}
                {f.display && f.active && !f.applicable && <span className="muted small"> (not used by this type)</span>}
              </dd>
            </div>
          ))}
        </dl>
      )}
      {canEdit && p && (
        <button type="button" className="ghost" onClick={onEdit}>
          Edit details
        </button>
      )}
    </section>
  )
}

/** Opens the structured editor for the cached properties of a ticket. */
export function EditDetailsDialog({ reference, onClose }: { reference: string; onClose: () => void }) {
  const q = useTicketProperties(reference)
  if (!q.data) {
    return (
      <Dialog title="Edit ticket details" onClose={onClose}>
        {q.isError ? <ErrorNotice error={q.error} onRetry={() => void q.refetch()} /> : <Loading label="Loading details" />}
      </Dialog>
    )
  }

  return <DetailsDialog reference={reference} properties={q.data} onClose={onClose} />
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
