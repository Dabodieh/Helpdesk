import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { fieldErrors } from '../../api/errors'
import { getPortalRequest, portalAttachmentHref, replyToRequest } from '../../api/portal'
import { ErrorNotice, Field, Loading } from '../../components/common'
import { formatDateTime } from '../../lib/format'
import { pollInterval } from '../../lib/realtime'
import { StatusBadge } from './parts'

const formatBytes = (n: number) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`)

/** One request: the public conversation only (there is no concept of internal notes here) and a reply box. */
export default function PortalRequestPage() {
  const { reference = '' } = useParams()
  // Keyed by the reference: the draft, the files and the message id of one request never carry over to another (UX-016).
  return <PortalRequest key={reference} reference={reference} />
}

function PortalRequest({ reference }: { reference: string }) {
  const qc = useQueryClient()
  const request = useQuery({ queryKey: ['portal', 'request', reference], queryFn: () => getPortalRequest(reference), refetchInterval: () => pollInterval() })
  const [text, setText] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const picker = useRef<HTMLInputElement>(null)
  // One id per composed reply, kept across retries and double clicks so the server never posts it twice (BE-003); a new one once it was saved.
  const clientMessageId = useRef(crypto.randomUUID())
  const [notSaved, setNotSaved] = useState(0)
  const send = useMutation({
    mutationFn: () => replyToRequest(reference, text.trim(), files, clientMessageId.current),
    onSuccess: async (result) => {
      clientMessageId.current = crypto.randomUUID()
      setNotSaved(result.attachmentsNotSaved)
      setText('')
      setFiles([])
      if (picker.current) picker.current.value = ''
      await qc.invalidateQueries({ queryKey: ['portal'] })
    },
  })
  const server = fieldErrors(send.error)

  if (request.isPending) return <Loading label="Loading your request" />
  if (request.isError) {
    return (
      <>
        <ErrorNotice error={request.error} onRetry={() => void request.refetch()} />
        <p>
          <Link to="/portal/requests">← My requests</Link>
        </p>
      </>
    )
  }
  const r = request.data
  if (r.mergedInto) {
    // Safe transition: the server only says this when the requester also owns the active request.
    return (
      <>
        <p>
          <Link to="/portal/requests">← My requests</Link>
        </p>
        <h1>{r.subject}</h1>
        <p className="notice info">
          This request ({r.reference}) was combined with another request of yours. Please follow it there:{' '}
          <Link to={`/portal/requests/${encodeURIComponent(r.mergedInto)}`}>open {r.mergedInto}</Link>.
        </p>
      </>
    )
  }
  function submit(e: FormEvent) {
    e.preventDefault()
    if (text.trim()) send.mutate()
  }

  return (
    <>
      <p>
        <Link to="/portal/requests">← My requests</Link>
      </p>
      <header className="request-head">
        <p className="muted">
          <span className="ref">{r.reference}</span> · {r.department}
          {r.form ? ` · ${r.form}` : ''}
        </p>
        <h1>{r.subject}</h1>
        <p>
          <StatusBadge status={r.status} label={r.statusLabel} /> <span className="muted small">Opened {formatDateTime(r.createdAt)} · Last update {formatDateTime(r.lastActivityAt)}</span>
        </p>
        {r.status === 'Waiting' && (
          <p className="notice info">
            “Waiting” means we are waiting for some information or for another team or system. It does not always mean there is something for you to do; if we need you, we will say so in the conversation below.
          </p>
        )}
        {r.status === 'Resolved' && <p className="notice info">We marked this as resolved. If it isn’t, reply below and we’ll pick it up again.</p>}
        {r.status === 'Closed' && <p className="notice info">This request is closed. Replying below will reopen it.</p>}
      </header>

      {r.fields.length > 0 && (
        <dl className="request-fields" aria-label="Details you provided">
          {r.fields.map((f) => (
            <div key={f.label}>
              <dt>{f.label}</dt>
              <dd>{f.value}</dd>
            </div>
          ))}
        </dl>
      )}

      <section aria-labelledby="conv-h">
        <h2 id="conv-h">Conversation</h2>
        <ol className="conversation">
          {r.messages.map((m) => (
            <li key={m.id} className={`bubble ${m.fromYou ? 'bubble-you' : 'bubble-support'}`}>
              <header>
                <strong>{m.author}</strong> <time className="muted small" dateTime={m.at}>{formatDateTime(m.at)}</time>
              </header>
              <p className="bubble-body">{m.body}</p>
              {m.attachments.length > 0 && (
                <ul className="attachments" aria-label="Attachments">
                  {m.attachments.map((a) => (
                    <li key={a.id}>
                      {a.downloadable ? (
                        <a href={portalAttachmentHref(r.reference, a.id)} download>
                          {a.fileName}
                        </a>
                      ) : (
                        <span>{a.fileName}</span>
                      )}{' '}
                      <span className="muted small">
                        {formatBytes(a.size)}
                        {a.downloadable ? '' : ' · being checked'}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ol>
      </section>

      {!r.canReply && <p className="notice info">You are copied on this request. You will receive the replies by email; the person who raised it can reply here.</p>}
      {r.canReply && <form className="portal-form reply-form" onSubmit={submit} aria-label="Reply">
        <Field label="Add a reply" error={server.body}>
          {(p) => <textarea {...p} rows={4} value={text} onChange={(e) => setText(e.target.value)} />}
        </Field>
        <Field label="Attach files (optional)" error={server.files}>
          {(p) => <input {...p} ref={picker} type="file" multiple onChange={(e) => setFiles([...(e.target.files ?? [])].slice(0, 5))} />}
        </Field>
        <ErrorNotice error={send.error && Object.keys(server).length === 0 ? send.error : null} />
        {notSaved > 0 && (
          <p className="notice warn" role="status">
            Your reply was sent, but {notSaved === 1 ? '1 file' : `${notSaved} files`} could not be saved. Please attach {notSaved === 1 ? 'it' : 'them'} again in a new reply.
          </p>
        )}
        <button type="submit" className="primary" disabled={send.isPending || !text.trim()}>
          {send.isPending ? 'Sending…' : 'Send reply'}
        </button>
      </form>}
    </>
  )
}
