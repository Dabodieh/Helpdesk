import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { getSignature, previewSignature, saveSignature } from '../api/emailIdentity'
import { ErrorNotice, Loading, PageHeader } from '../components/common'
import RichText, { SafeHtmlFrame } from '../components/RichText'
import { useMe } from '../auth'

/** The signed-in agent: who they are (read-only, from their account) and their own email signature for each department where they can reply. */
export default function ProfilePage() {
  const me = useMe()
  const departments = me.memberships.filter((m) => m.permissions.includes('tickets.reply'))
  const [departmentId, setDepartmentId] = useState(departments[0]?.departmentId ?? '')
  return (
    <div className="page">
      <PageHeader title="My profile" subtitle="Account details and reply signature." />
      <section aria-labelledby="identity-h">
        <h2 id="identity-h">Identity</h2>
        <dl className="prop-list">
          <div>
            <dt>Name</dt>
            <dd>{me.displayName}</dd>
          </div>
          <div>
            <dt>Email</dt>
            <dd>{me.email ?? <span className="muted">Not set</span>}</dd>
          </div>
        </dl>
        <p className="muted small">These come from your sign-in account and cannot be changed here.</p>
      </section>
      <section aria-labelledby="signature-h">
        <h2 id="signature-h">Email signature</h2>
        <p className="muted">
          Replies to requesters are always sent from the ticket&apos;s helpdesk mailbox, never from your own address. Your signature for the ticket&apos;s department is added to the end of your public replies when you
          send them. Internal notes never get one. There is no shared department footer.
        </p>
        {departments.length === 0 ? (
          <p className="empty">You do not belong to a department where you can reply to requesters.</p>
        ) : (
          <>
            {departments.length > 1 && (
              <label className="field">
                <span>Department</span>
                <select value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
                  {departments.map((d) => (
                    <option key={d.departmentId} value={d.departmentId}>
                      {d.departmentName}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {departmentId && <SignatureEditor key={departmentId} departmentId={departmentId} />}
          </>
        )}
        <p className="muted small">
          <Link to="/">Back to the workspace</Link>
        </p>
      </section>
    </div>
  )
}

function SignatureEditor({ departmentId }: { departmentId: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['signature', departmentId], queryFn: () => getSignature(departmentId) })
  const [html, setHtml] = useState<string | null>(null)
  const [previewHtml, setPreviewHtml] = useState<string | null>(null)
  const current = html ?? q.data?.html ?? ''
  const preview = useMutation({ mutationFn: () => previewSignature(departmentId, current), onSuccess: (r) => setPreviewHtml(r.html) })
  const save = useMutation({
    mutationFn: () => saveSignature(departmentId, current),
    onSuccess: async (saved) => {
      qc.setQueryData(['signature', departmentId], saved)
      setHtml(null)
      await qc.invalidateQueries({ queryKey: ['signature', departmentId] })
    },
  })

  if (q.isPending) return <Loading label="Loading signature" />
  if (q.isError) return <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />
  return (
    <div className="signature-editor">
      <p className="small muted">
        Signature for <strong>{q.data.departmentName}</strong>. Placeholders: {q.data.placeholders.map((p) => `{{${p}}}`).join(', ')}
      </p>
      <RichText label="Signature" value={q.data.html ?? ''} resetKey={departmentId} onChange={setHtml} />
      <div className="dialog-actions">
        <button type="button" disabled={preview.isPending} onClick={() => preview.mutate()}>
          Preview
        </button>
        <button type="button" className="primary" disabled={save.isPending} onClick={() => save.mutate()}>
          {save.isPending ? 'Saving…' : 'Save signature'}
        </button>
      </div>
      <ErrorNotice error={save.error ?? preview.error} />
      {save.isSuccess && <p role="status" className="small">Signature saved.</p>}
      {previewHtml !== null && (
        <section aria-label="Signature preview">
          <h3>Preview</h3>
          {previewHtml ? <SafeHtmlFrame html={previewHtml} title="Signature preview" /> : <p className="muted small">No signature: replies go out without one.</p>}
        </section>
      )}
    </div>
  )
}
