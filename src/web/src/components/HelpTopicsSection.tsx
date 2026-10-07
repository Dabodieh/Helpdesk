import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import type { Department } from '../api/schemas'
import { listForms } from '../api/ticketing'
import { createHelpTopic, listHelpTopics, reorderHelpTopics, updateHelpTopic, type HelpTopic } from '../api/helpTopics'
import { ActiveBadge, ErrorNotice, Field, Loading, StatusBadge } from './common'
import Dialog from './Dialog'

const topicsKey = (d: string) => ['help-topics', d]

/** A topic only names and describes a request form for requesters; the form keeps its fields, audience and routing. Nothing of the form is copied here. */
function TopicDialog({ department, existing, onClose }: { department: Department; existing?: HelpTopic; onClose: () => void }) {
  const qc = useQueryClient()
  const forms = useQuery({ queryKey: ['request-forms', department.id], queryFn: () => listForms(department.id) })
  const [name, setName] = useState(existing?.name ?? '')
  const [description, setDescription] = useState(existing?.description ?? '')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [formId, setFormId] = useState(existing?.formId ?? '')
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim(), isActive: active, formId }
      return existing ? updateHelpTopic(department.id, existing.id, { ...body, version: existing.version }) : createHelpTopic(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: topicsKey(department.id) })
      await qc.invalidateQueries({ queryKey: ['portal', 'topics'] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim() && formId) save.mutate()
  }
  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New help topic'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Help topic">
        <Field label="Topic name" hint="What the requester sees, in their own words: for example “Request software”." error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Short description (optional)" error={server.description}>
          {(p) => <textarea {...p} rows={2} maxLength={300} value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <Field label="Opens request form" hint="The form decides who can use it, what it asks and where the ticket goes. A topic is only offered while its form is available to the requester." error={server.formid}>
          {(p) => (
            <select {...p} value={formId} required onChange={(e) => setFormId(e.target.value)}>
              <option value="">Choose a request form…</option>
              {(forms.data ?? []).map((f) => (
                <option key={f.id} value={f.id}>
                  {f.name}
                  {!f.isActive || !f.visibleToRequesters ? ' (not available)' : ''}
                </option>
              ))}
            </select>
          )}
        </Field>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Offer this topic to requesters
        </label>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim() || !formId}>
            Save topic
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Help topics of a department (permission department.forms.manage): the requester-facing navigation layer over request forms. */
export default function HelpTopicsSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: topicsKey(department.id), queryFn: () => listHelpTopics(department.id) })
  const [editing, setEditing] = useState<HelpTopic | 'new' | null>(null)
  const reorder = useMutation({ mutationFn: (ids: string[]) => reorderHelpTopics(department.id, ids), onSuccess: (d) => qc.setQueryData(topicsKey(department.id), d) })
  const topics = q.data ?? []
  const move = (i: number, d: -1 | 1) => {
    const ids = topics.map((t) => t.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorder.mutate(ids)
  }
  return (
    <section aria-labelledby="topics-h">
      <div className="section-head">
        <h2 id="topics-h">Help topics</h2>
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New help topic
        </button>
      </div>
      <p className="muted">
        Topics are the plain-language entries requesters pick from, such as “Request access” or “Report an IT problem”. Each one opens an existing request form, which stays in charge of who can use it, its fields and routing.
      </p>
      {q.isPending && <Loading label="Loading help topics" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && topics.length === 0 && <p className="empty">No help topics yet. Requesters can still find your request forms in the full list; add topics to give them simpler starting points.</p>}
      {topics.length > 0 && (
        <ul className="rows" aria-label="Help topics">
          {topics.map((t, i) => (
            <li key={t.id} className="row">
              <span className="row-actions">
                <button type="button" className="ghost" aria-label={`Move ${t.name} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)}>
                  ↑
                </button>
                <button type="button" className="ghost" aria-label={`Move ${t.name} down`} disabled={i === topics.length - 1 || reorder.isPending} onClick={() => move(i, 1)}>
                  ↓
                </button>
              </span>
              <div className="row-main">
                <div className="row-title">
                  <strong>{t.name}</strong> <ActiveBadge active={t.isActive} />
                  {t.isActive && !t.formAvailable && <StatusBadge tone="warn">Form not available</StatusBadge>}
                </div>
                <p className="muted small row-sub">
                  Opens: {t.formName}
                  {t.description ? ` · ${t.description}` : ''}
                </p>
              </div>
              <button type="button" onClick={() => setEditing(t)}>
                Edit
              </button>
            </li>
          ))}
        </ul>
      )}
      <ErrorNotice error={reorder.error} />
      {editing && <TopicDialog department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
    </section>
  )
}
