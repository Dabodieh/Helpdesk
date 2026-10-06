import { Field } from './common'

/** What a field control needs to render (the portal form field and the agent property both fit). */
export type FieldControlSpec = {
  key: string
  label: string
  helpText?: string | null
  type: string
  required: boolean
  options: { value: string; label: string }[]
}

/** One typed input for a ticket field: text, long text, single or multiple choice, checkbox, date, number. Values are plain JSON; the server validates. */
export function FieldControl({ field, value, onChange, error }: { field: FieldControlSpec; value: unknown; onChange: (v: unknown) => void; error?: string }) {
  const label = field.required ? field.label : `${field.label} (optional)`
  if (field.type === 'Checkbox') {
    return (
      <div className="field">
        <label className="check">
          <input type="checkbox" checked={value === true} aria-invalid={error ? true : undefined} onChange={(e) => onChange(e.target.checked)} /> {field.label}
          {field.required ? '' : ' (optional)'}
        </label>
        {(error || field.helpText) && <span className={error ? 'field-error' : 'muted'}>{error ?? field.helpText}</span>}
      </div>
    )
  }
  if (field.type === 'MultiSelect') {
    const chosen = Array.isArray(value) ? (value as string[]) : []
    return (
      <fieldset className="field choice">
        <legend>{label}</legend>
        {field.options.map((o) => (
          <label key={o.value} className="check">
            <input type="checkbox" checked={chosen.includes(o.value)} onChange={(e) => onChange(e.target.checked ? [...chosen, o.value] : chosen.filter((v) => v !== o.value))} /> {o.label}
          </label>
        ))}
        {(error || field.helpText) && <span className={error ? 'field-error' : 'muted'}>{error ?? field.helpText}</span>}
      </fieldset>
    )
  }
  return (
    <Field label={label} error={error} hint={field.helpText ?? undefined}>
      {(p) => {
        switch (field.type) {
          case 'LongText':
            return <textarea {...p} rows={4} maxLength={4000} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />
          case 'SingleSelect':
            return (
              <select {...p} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value || undefined)}>
                <option value="">Choose…</option>
                {field.options.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            )
          case 'Date':
            return <input {...p} type="date" value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value || undefined)} />
          case 'Number':
            return <input {...p} type="number" value={(value as string | number | undefined) ?? ''} onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))} />
          default:
            return <input {...p} maxLength={500} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />
        }
      }}
    </Field>
  )
}
