import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { fieldErrors } from '../api/errors'
import { getAutoReply, previewAutoReply, saveAutoReply, type AutoReply } from '../api/emailIdentity'
import type { Department } from '../api/schemas'
import { ErrorNotice, Field, Loading } from './common'

/** Department-level automatic acknowledgement of new tickets. Off until enabled; sent from the ticket's mailbox; never carries an agent signature. */
export default function AutoReplySection({ department }: { department: Department }) {
  const q = useQuery({ queryKey: ['auto-reply', department.id], queryFn: () => getAutoReply(department.id) })
  if (q.isPending) return <Loading label="Loading acknowledgement settings" />
  if (q.isError) return <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />
  return <AutoReplyForm key={q.data.version} department={department} saved={q.data} />
}

function AutoReplyForm({ department, saved }: { department: Department; saved: AutoReply }) {
  const qc = useQueryClient()
  const [enabled, setEnabled] = useState(saved.enabled)
  const [forEmail, setForEmail] = useState(saved.forEmail)
  const [forPortal, setForPortal] = useState(saved.forPortal)
  const [subject, setSubject] = useState(saved.subject)
  const [body, setBody] = useState(saved.body)
  const preview = useMutation({ mutationFn: () => previewAutoReply(department.id, { subject, body }) })
  const save = useMutation({
    mutationFn: () => saveAutoReply(department.id, { enabled, forEmail, forPortal, subject, body, version: saved.configured ? saved.version : undefined }),
    onSuccess: (r) => {
      qc.setQueryData(['auto-reply', department.id], r)
    },
  })
  const server = fieldErrors(save.error)
  return (
    <section aria-labelledby="auto-reply-h" className="auto-reply">
      <h3 id="auto-reply-h">Automatic acknowledgement</h3>
      <p className="muted small">
        Sent once when a new ticket is created, from the mailbox the ticket belongs to (the receiving mailbox for email, the department&apos;s default mailbox for the portal). It never includes an
        agent signature. Nothing is sent until you enable it.
      </p>
      <form
        className="dialog-form"
        aria-label="Automatic acknowledgement"
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate()
        }}
      >
        <label className="check">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Enabled
        </label>
        <fieldset>
          <legend>Send for</legend>
          <label className="check">
            <input type="checkbox" checked={forEmail} onChange={(e) => setForEmail(e.target.checked)} /> Email-created tickets
          </label>
          <label className="check">
            <input type="checkbox" checked={forPortal} onChange={(e) => setForPortal(e.target.checked)} /> Portal-created tickets
          </label>
        </fieldset>
        <Field label="Subject" error={server.subject}>
          {(p) => <input {...p} value={subject} maxLength={200} onChange={(e) => setSubject(e.target.value)} />}
        </Field>
        <Field label="Body" error={server.body} hint={`Placeholders: ${saved.placeholders.map((x) => `{{${x}}}`).join(', ')}`}>
          {(p) => <textarea {...p} rows={7} value={body} maxLength={5000} onChange={(e) => setBody(e.target.value)} />}
        </Field>
        <ErrorNotice error={save.error && Object.keys(server).length === 0 ? save.error : null} />
        <div className="dialog-actions">
          <button type="button" disabled={preview.isPending} onClick={() => preview.mutate()}>
            Preview
          </button>
          <button type="submit" className="primary" disabled={save.isPending}>
            Save
          </button>
        </div>
        {save.isSuccess && <p role="status" className="small">Saved.</p>}
      </form>
      {preview.data && (
        <section aria-label="Acknowledgement preview" className="merge-preview">
          <p className="muted small">
            From {preview.data.fromName} &lt;{preview.data.fromAddress}&gt; (sample requester and ticket)
          </p>
          <p>
            <strong>{preview.data.subject}</strong>
          </p>
          <p className="msg-body">{preview.data.body}</p>
        </section>
      )}
      <ErrorNotice error={preview.error} />
    </section>
  )
}
