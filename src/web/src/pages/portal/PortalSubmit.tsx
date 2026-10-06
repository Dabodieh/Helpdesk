import { useMutation, useQuery } from '@tanstack/react-query'
import { useMemo, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { fieldErrors } from '../../api/errors'
import { createPortalRequest, getPortalForm, listPortalForms, replyToRequest, searchKnowledge, type PortalFormListItem } from '../../api/portal'
import { FieldControl as CustomField } from '../../components/FieldControl'
import { ErrorNotice, Field, Loading } from '../../components/common'
import { useDebounced } from '../../lib/useDebounced'

/** Step 1: choose what the request is about. Forms are grouped by the department that will handle them. */
function ChooseForm() {
  const forms = useQuery({ queryKey: ['portal', 'forms'], queryFn: listPortalForms, staleTime: 60_000 })
  const groups = useMemo(() => {
    const map = new Map<string, PortalFormListItem[]>()
    for (const f of forms.data ?? []) map.set(f.department, [...(map.get(f.department) ?? []), f])
    return [...map.entries()]
  }, [forms.data])
  return (
    <>
      <h1>Submit a request</h1>
      <p className="muted">Choose the option that best describes what you need.</p>
      {forms.isPending && <Loading label="Loading request types" />}
      {forms.isError && <ErrorNotice error={forms.error} onRetry={() => void forms.refetch()} />}
      {forms.isSuccess && groups.length === 0 && <p className="empty">No request types are available yet.</p>}
      {groups.map(([department, items]) => (
        <section key={department} className="portal-section" aria-labelledby={`dept-${department}`}>
          <h2 id={`dept-${department}`}>{department}</h2>
          <ul className="tiles">
            {items.map((f) => (
              <li key={f.id}>
                <Link to={`/portal/submit/${f.id}`} className="tile">
                  <strong>{f.name}</strong>
                  {f.description && <span className="small">{f.description}</span>}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </>
  )
}

/** Step 2: the form. Knowledge suggestions appear while typing the subject; they assist and never block. */
function RequestForm({ formId }: { formId: string }) {
  const form = useQuery({ queryKey: ['portal', 'form', formId], queryFn: () => getPortalForm(formId) })
  const [subject, setSubject] = useState('')
  const [description, setDescription] = useState('')
  const [values, setValues] = useState<Record<string, unknown>>({})
  const [files, setFiles] = useState<File[]>([])
  const [submissionId] = useState(() => crypto.randomUUID())
  const [done, setDone] = useState<{ reference: string; label: string; filesFailed: boolean } | null>(null)
  const typed = useDebounced(subject.trim(), 350)
  const suggestions = useQuery({
    queryKey: ['portal', 'knowledge', 'suggest', typed],
    queryFn: () => searchKnowledge({ q: typed, limit: 3 }),
    enabled: typed.length >= 3,
    staleTime: 30_000,
  })

  const submit = useMutation({
    mutationFn: async () => {
      const fields = Object.fromEntries(Object.entries(values).filter(([, v]) => v !== undefined && v !== '' && !(Array.isArray(v) && v.length === 0)))
      const created = await createPortalRequest({ formId, subject: subject.trim(), description: description.trim(), fields, submissionId })
      let filesFailed = false
      if (files.length > 0) {
        try {
          await replyToRequest(created.reference, 'Files attached to this request.', files)
        } catch {
          filesFailed = true // the request exists; the files can be added from the request page
        }
      }
      return { reference: created.reference, label: created.statusLabel, filesFailed }
    },
    onSuccess: (r) => setDone(r),
  })
  const server = fieldErrors(submit.error)

  if (form.isPending) return <Loading label="Loading the form" />
  if (form.isError) return <ErrorNotice error={form.error} onRetry={() => void form.refetch()} />

  if (done) {
    return (
      <section className="portal-success" role="status" aria-labelledby="done-h">
        <h1 id="done-h">Request received</h1>
        <p>
          Your reference is <strong className="ref">{done.reference}</strong>. We’ll update you here and by email.
        </p>
        {done.filesFailed && <p className="field-error">Your request was sent, but the files could not be attached. You can add them from the request page.</p>}
        <p>
          <Link to={`/portal/requests/${encodeURIComponent(done.reference)}`} className="button-link primary-link">
            View your request
          </Link>{' '}
          <Link to="/portal/requests">My requests</Link>
        </p>
      </section>
    )
  }

  const f = form.data
  function onSubmit(e: FormEvent) {
    e.preventDefault()
    if (subject.trim() && description.trim()) submit.mutate()
  }

  return (
    <>
      <p>
        <Link to="/portal/submit">← All request types</Link>
      </p>
      <h1>{f.name}</h1>
      <p className="muted">{f.description ?? `Handled by ${f.department}.`}</p>
      <form className="portal-form" onSubmit={onSubmit} aria-label={f.name} noValidate>
        <Field label="Subject" error={server.subject} hint="A short summary of what you need.">
          {(p) => <input {...p} value={subject} maxLength={200} required autoComplete="off" onChange={(e) => setSubject(e.target.value)} />}
        </Field>
        {suggestions.isSuccess && suggestions.data.items.length > 0 && (
          <aside className="suggestions" aria-label="Articles that might help">
            <strong>These articles might help</strong>
            <ul>
              {suggestions.data.items.map((a) => (
                <li key={a.slug}>
                  <Link to={`/portal/knowledge/${a.slug}`} target="_blank" rel="noopener noreferrer">
                    {a.title}
                  </Link>
                </li>
              ))}
            </ul>
            <span className="muted small">You can still submit your request.</span>
          </aside>
        )}
        <Field label="Describe what you need" error={server.description}>
          {(p) => <textarea {...p} rows={6} required value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        {f.fields.map((field) => (
          <CustomField key={field.key} field={field} value={values[field.key]} error={server[`fields.${field.key}`]} onChange={(v) => setValues((cur) => ({ ...cur, [field.key]: v }))} />
        ))}
        <Field label="Attach files (optional)" hint="Screenshots, documents or logs. Files are checked before anyone can open them.">
          {(p) => <input {...p} type="file" multiple onChange={(e) => setFiles([...(e.target.files ?? [])].slice(0, 5))} />}
        </Field>
        <ErrorNotice error={submit.error && Object.keys(server).length === 0 ? submit.error : null} />
        <button type="submit" className="primary big" disabled={submit.isPending || !subject.trim() || !description.trim()}>
          {submit.isPending ? 'Sending…' : 'Submit request'}
        </button>
      </form>
    </>
  )
}

export default function PortalSubmit() {
  const { formId } = useParams()
  return formId ? <RequestForm formId={formId} /> : <ChooseForm />
}
