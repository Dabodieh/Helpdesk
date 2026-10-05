import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { createMailbox, listAttention, listMailboxes, retryMail, updateMailbox, type AttentionItem, type Mailbox } from '../api/mail'
import { canInDepartment } from '../api/permissions'
import type { Department } from '../api/schemas'
import { useMe } from '../auth'
import { formatDateTime } from '../lib/format'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'

const IGNORE_REASONS: Record<string, string> = {
  own_address: 'sent from a helpdesk mailbox',
  auto_submitted: 'automatic reply',
  precedence_bulk: 'bulk mail',
  auto_response_suppress: 'automatic reply',
  mailing_list: 'mailing list',
  bounce: 'delivery report',
  draft: 'draft',
  no_sender: 'no sender',
  sender_rate_limited: 'too many messages from this sender',
  duplicate_internet_message_id: 'duplicate',
}

function CreateMailbox({ departmentId }: { departmentId: string }) {
  const qc = useQueryClient()
  const [address, setAddress] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [signature, setSignature] = useState('')
  const create = useMutation({
    mutationFn: () => createMailbox(departmentId, { address: address.trim(), displayName: displayName.trim(), signature: signature.trim() || undefined }),
    onSuccess: async () => {
      setAddress('')
      setDisplayName('')
      setSignature('')
      await qc.invalidateQueries({ queryKey: ['mailboxes', departmentId] })
    },
  })
  const server = fieldErrors(create.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (address.trim() && displayName.trim()) create.mutate()
  }
  return (
    <form className="form" onSubmit={submit} aria-label="New mailbox">
      <Field label="Mailbox address" error={server.address} hint="The shared mailbox in Microsoft 365 that receives this department's email.">
        {(p) => <input {...p} type="email" value={address} maxLength={320} required onChange={(e) => setAddress(e.target.value)} />}
      </Field>
      <Field label="Sender name" error={server.displayName} hint="Shown as the sender of replies.">
        {(p) => <input {...p} value={displayName} maxLength={200} required onChange={(e) => setDisplayName(e.target.value)} />}
      </Field>
      <Field label="Signature (optional)" error={server.signature} hint="Plain text appended to every reply.">
        {(p) => <textarea {...p} rows={3} value={signature} maxLength={4000} onChange={(e) => setSignature(e.target.value)} />}
      </Field>
      <button type="submit" className="primary" disabled={create.isPending}>
        Add mailbox
      </button>
      <ErrorNotice error={create.error} />
    </form>
  )
}

function MailboxRow({ departmentId, mailbox: m }: { departmentId: string; mailbox: Mailbox }) {
  const qc = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(m.displayName)
  const [signature, setSignature] = useState(m.signature ?? '')
  const refresh = () => qc.invalidateQueries({ queryKey: ['mailboxes', departmentId] })
  const patch = useMutation({
    mutationFn: (b: { displayName?: string; signature?: string; isActive?: boolean; isDefault?: boolean }) =>
      updateMailbox(departmentId, m.id, { ...b, version: m.version }),
    onSuccess: () => setEditing(false),
    onSettled: refresh,
  })
  const server = fieldErrors(patch.error)
  return (
    <>
      <tr>
        <td>
          {m.address} {m.isDefault && <span className="badge">Default</span>}
        </td>
        <td>{m.displayName}</td>
        <td>
          <ActiveBadge active={m.isActive} />
        </td>
        <td>
          {m.subscription.state}
          {m.subscription.expiresAt ? <span className="muted small"> until {formatDateTime(m.subscription.expiresAt)}</span> : null}
        </td>
        <td>
          {m.lastSyncAt ? formatDateTime(m.lastSyncAt) : 'Never'}
          {m.lastSyncError && <span className="field-error small"> ({m.lastSyncError})</span>}
        </td>
        <td>{m.attentionCount > 0 ? <strong>{m.attentionCount} failed</strong> : 0}</td>
        <td className="row-actions">
          <button type="button" onClick={() => setEditing((v) => !v)} aria-expanded={editing}>
            {editing ? 'Close' : 'Edit'}
          </button>
          {m.isActive && !m.isDefault && (
            <button type="button" disabled={patch.isPending} onClick={() => patch.mutate({ isDefault: true })}>
              Make default
            </button>
          )}
          <button type="button" disabled={patch.isPending} onClick={() => patch.mutate({ isActive: !m.isActive })}>
            {m.isActive ? 'Deactivate' : 'Activate'}
          </button>
        </td>
      </tr>
      {(editing || patch.isError) && (
        <tr>
          <td colSpan={7}>
            {editing && (
              <form
                className="form inline"
                aria-label={`Edit ${m.address}`}
                onSubmit={(e) => {
                  e.preventDefault()
                  patch.mutate({ displayName: name.trim(), signature })
                }}
              >
                <Field label="Sender name" error={server.displayName}>
                  {(p) => <input {...p} value={name} maxLength={200} required onChange={(e) => setName(e.target.value)} />}
                </Field>
                <Field label="Signature" error={server.signature}>
                  {(p) => <textarea {...p} rows={3} value={signature} maxLength={4000} onChange={(e) => setSignature(e.target.value)} />}
                </Field>
                <button type="submit" className="primary" disabled={patch.isPending}>
                  Save
                </button>
              </form>
            )}
            <ErrorNotice error={patch.error} />
          </td>
        </tr>
      )}
    </>
  )
}

function Attention({ departmentId }: { departmentId: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['mail-attention', departmentId], queryFn: () => listAttention(departmentId, null), refetchInterval: 30_000 })
  const retry = useMutation({
    mutationFn: (item: AttentionItem) => retryMail(departmentId, item.id),
    onSettled: () => qc.invalidateQueries({ queryKey: ['mail-attention', departmentId] }),
  })
  if (q.isPending) return <Loading label="Loading mail needing attention" />
  if (q.isError) return <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />
  if (q.data.items.length === 0) return <p className="empty">No failed or ignored mail.</p>
  return (
    <>
      <table className="grid">
        <caption className="sr-only">Mail needing attention</caption>
        <thead>
          <tr>
            <th scope="col">When</th>
            <th scope="col">Direction</th>
            <th scope="col">From / to</th>
            <th scope="col">Subject</th>
            <th scope="col">Outcome</th>
            <th scope="col" />
          </tr>
        </thead>
        <tbody>
          {q.data.items.map((i) => (
            <tr key={i.id}>
              <td>{formatDateTime(i.occurredAt)}</td>
              <td>{i.direction}</td>
              <td>{i.address}</td>
              <td>{i.subject}</td>
              <td>
                {i.state === 'Ignored'
                  ? `Ignored: ${IGNORE_REASONS[i.ignoreReason ?? ''] ?? i.ignoreReason ?? 'unknown'}`
                  : `Failed${i.lastError ? ` (${i.lastError})` : ''} after ${i.attempts} attempt(s)`}
              </td>
              <td>
                {i.state === 'Failed' && (
                  <button type="button" disabled={retry.isPending} onClick={() => retry.mutate(i)}>
                    Retry
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <ErrorNotice error={retry.error} />
    </>
  )
}

/** Department email configuration (permission department.mailboxes.manage; the server enforces it). */
export default function MailboxesSection({ department }: { department: Department }) {
  const me = useMe()
  const allowed = canInDepartment(me, 'department.mailboxes.manage', department.id)
  const q = useQuery({ queryKey: ['mailboxes', department.id], queryFn: () => listMailboxes(department.id), enabled: allowed })
  if (!allowed) return null
  return (
    <>
      <section aria-labelledby="mailboxes-h">
        <h2 id="mailboxes-h">Mailboxes</h2>
        <p className="muted">
          Email sent to these shared mailboxes becomes tickets of this department. Replies leave from the ticket's mailbox, or the default one.
        </p>
        {q.isPending && <Loading label="Loading mailboxes" />}
        {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
        {q.isSuccess && q.data.length === 0 && <p className="empty">No mailbox configured yet: replies are recorded but not emailed.</p>}
        {q.isSuccess && q.data.length > 0 && (
          <table className="grid">
            <caption className="sr-only">Mailboxes of {department.name}</caption>
            <thead>
              <tr>
                <th scope="col">Address</th>
                <th scope="col">Sender name</th>
                <th scope="col">Status</th>
                <th scope="col">Webhook subscription</th>
                <th scope="col">Last sync</th>
                <th scope="col">Mail needing attention</th>
                <th scope="col" />
              </tr>
            </thead>
            <tbody>
              {q.data.map((m) => (
                <MailboxRow key={m.id} departmentId={department.id} mailbox={m} />
              ))}
            </tbody>
          </table>
        )}
        <CreateMailbox departmentId={department.id} />
      </section>
      <section aria-labelledby="attention-h">
        <h2 id="attention-h">Mail needing attention</h2>
        <p className="muted">Messages that could not be processed or sent, and mail that was ignored on purpose (automatic replies, mailing lists, loops).</p>
        <Attention departmentId={department.id} />
      </section>
    </>
  )
}
