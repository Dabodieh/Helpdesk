import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { createCanned, listCanned, updateCanned, type Canned } from '../api/canned'
import { fieldErrors } from '../api/errors'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

type Scope = { scope: 'Personal' } | { scope: 'Department'; departmentId: string }

function Editor({ scope, existing, onClose }: { scope: Scope; existing?: Canned; onClose: () => void }) {
  const qc = useQueryClient()
  const [title, setTitle] = useState(existing?.title ?? '')
  const [body, setBody] = useState(existing?.body ?? '')
  const save = useMutation({
    mutationFn: () =>
      existing
        ? updateCanned(existing.id, { version: existing.version, title: title.trim(), body })
        : createCanned({ scope: scope.scope, departmentId: scope.scope === 'Department' ? scope.departmentId : undefined, title: title.trim(), body }),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['canned'] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (title.trim() && body.trim()) save.mutate()
  }
  return (
    <Dialog title={existing ? 'Edit response' : 'New response'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit}>
        <Field label="Title" error={server.title} hint="What you will look for in the picker.">
          {(p) => <input {...p} data-autofocus value={title} maxLength={120} required onChange={(e) => setTitle(e.target.value)} />}
        </Field>
        <Field label="Text" error={server.body} hint="Plain text. It is inserted into the reply box; you review it before posting.">
          {(p) => <textarea {...p} rows={8} value={body} maxLength={5000} required onChange={(e) => setBody(e.target.value)} />}
        </Field>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !title.trim() || !body.trim()}>
            {save.isPending ? 'Saving…' : 'Save response'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** List + create/edit dialog for canned responses of one scope (your own, or one department's). The server enforces who may manage what. */
export default function CannedManager({ scope }: { scope: Scope }) {
  const qc = useQueryClient()
  const departmentId = scope.scope === 'Department' ? scope.departmentId : undefined
  const q = useQuery({ queryKey: ['canned', 'manage', scope.scope, departmentId], queryFn: () => listCanned({ departmentId, includeInactive: true }) })
  const [editing, setEditing] = useState<Canned | 'new' | null>(null)
  const toggle = useMutation({
    mutationFn: (c: Canned) => updateCanned(c.id, { version: c.version, isActive: !c.isActive }),
    onSettled: () => qc.invalidateQueries({ queryKey: ['canned'] }),
  })
  const rows = (q.data ?? []).filter((c) => (scope.scope === 'Personal' ? c.scope === 'Personal' : c.scope === 'Department' && c.departmentId === departmentId))

  return (
    <>
      <div className="section-actions">
        <button type="button" className="primary" onClick={() => setEditing('new')}>
          New response
        </button>
      </div>
      {q.isPending && <Loading label="Loading responses" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && rows.length === 0 && <p className="empty small">No responses yet.</p>}
      {rows.length > 0 && (
        <table className="grid">
          <caption className="sr-only">Canned responses</caption>
          <thead>
            <tr>
              <th scope="col">Title</th>
              <th scope="col">Text</th>
              <th scope="col">Status</th>
              <th scope="col" />
            </tr>
          </thead>
          <tbody>
            {rows.map((c) => (
              <tr key={c.id}>
                <td>
                  <strong>{c.title}</strong>
                </td>
                <td className="muted preview-cell">{c.body}</td>
                <td>
                  <ActiveBadge active={c.isActive} />
                </td>
                <td className="row-actions">
                  <button type="button" disabled={!c.canManage} onClick={() => setEditing(c)}>
                    Edit
                  </button>
                  <button type="button" disabled={!c.canManage || toggle.isPending} onClick={() => toggle.mutate(c)}>
                    {c.isActive ? 'Deactivate' : 'Activate'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ErrorNotice error={toggle.error} />
      {editing && <Editor scope={scope} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
    </>
  )
}
