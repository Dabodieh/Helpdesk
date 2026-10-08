import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { createMailbox, listAttention, listMailboxes, releaseMail, retryMail, updateMailbox, type AttentionItem, type Mailbox } from '../api/mail'
import { canInDepartment } from '../api/permissions'
import type { Department } from '../api/schemas'
import { useMe } from '../auth'
import { formatDateTime } from '../lib/format'
import { HEALTH_LABEL, HEALTH_REASON, HEALTH_TONE } from '../lib/mailHealth'
import { ActiveBadge, ErrorNotice, Field, Loading, StatusBadge } from './common'
import { isOutcomeUncertain } from '../api/errors'
import Dialog from './Dialog'

/** Ignored mail an administrator may turn into a ticket (D12). Delivery reports, loops, duplicates, drafts and mail without a sender never. */
const RELEASABLE = new Set(['auto_submitted', 'precedence_bulk', 'auto_response_suppress', 'mailing_list', 'sender_rate_limited'])

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

function CreateMailbox({ departmentId, onClose }: { departmentId: string; onClose: () => void }) {
  const qc = useQueryClient()
  const [address, setAddress] = useState('')
  const [displayName, setDisplayName] = useState('')
  const create = useMutation({
    mutationFn: () => createMailbox(departmentId, { address: address.trim(), displayName: displayName.trim() }),
    onSuccess: async () => {
      setAddress('')
      setDisplayName('')
      await qc.invalidateQueries({ queryKey: ['mailboxes', departmentId] })
      onClose()
    },
  })
  const server = fieldErrors(create.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (address.trim() && displayName.trim()) create.mutate()
  }
  return (
    <Dialog title="Add mailbox" variant="drawer" onClose={onClose}>
    <form className="dialog-form" onSubmit={submit} aria-label="New mailbox">
      <Field label="Mailbox address" error={server.address} hint="The shared mailbox in Microsoft 365 that receives this department's email.">
        {(p) => <input {...p} data-autofocus type="email" value={address} maxLength={320} required onChange={(e) => setAddress(e.target.value)} />}
      </Field>
      <Field label="Sender name" error={server.displayName} hint="Shown as the sender of replies.">
        {(p) => <input {...p} value={displayName} maxLength={200} required onChange={(e) => setDisplayName(e.target.value)} />}
      </Field>
      <ErrorNotice error={create.error} />
      <div className="dialog-actions">
        <button type="button" onClick={onClose}>
          Cancel
        </button>
        <button type="submit" className="primary" disabled={create.isPending}>
          Add mailbox
        </button>
      </div>
    </form>
    </Dialog>
  )
}

function MailboxRow({ departmentId, mailbox: m }: { departmentId: string; mailbox: Mailbox }) {
  const qc = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(m.displayName)
  const refresh = () => qc.invalidateQueries({ queryKey: ['mailboxes', departmentId] })
  const patch = useMutation({
    mutationFn: (b: { displayName?: string; isActive?: boolean; isDefault?: boolean }) =>
      updateMailbox(departmentId, m.id, { ...b, version: m.version }),
    onSuccess: () => setEditing(false),
    onSettled: refresh,
  })
  const server = fieldErrors(patch.error)
  return (
    <>
      <tr>
        <td className="mailbox-identity">
          <strong>{m.displayName}</strong> {m.isDefault && <span className="badge">Default</span>}
          <span className="mailbox-address">{m.address}</span>
        </td>
        <td>
          <ActiveBadge active={m.isActive} />

          {m.health ? (
            <>
              <StatusBadge tone={HEALTH_TONE[m.health.status] ?? 'off'}>{HEALTH_LABEL[m.health.status] ?? m.health.status}</StatusBadge>
              {m.health.reasons.length > 0 && <div className="muted small">{m.health.reasons.map((r) => HEALTH_REASON[r] ?? r).join(' · ')}</div>}
              <div className="muted small">{m.lastSendAt ? `Last send accepted ${formatDateTime(m.lastSendAt)}` : 'No send recorded yet'}</div>
            </>
          ) : null}
        </td>
        <td>
          <span className="mailbox-sync">{m.lastSyncAt ? formatDateTime(m.lastSyncAt) : 'Never synced'}</span>
          <details className="mailbox-subscription"><summary>Subscription: {m.subscription.state}</summary>
            {m.subscription.expiresAt ? <span className="muted small">Expires {formatDateTime(m.subscription.expiresAt)}</span> : <span className="muted small">No expiry recorded</span>}
          </details>
          {m.lastSyncError && <span className="field-error small"> ({m.lastSyncError})</span>}
        </td>
        <td>{m.attentionCount > 0 ? <strong>{m.attentionCount} failed</strong> : 0}</td>
        <td className="row-actions">
          <button
            type="button"
            onClick={() => {
              if (!editing) setName(m.displayName) // always start from the stored name, never a stale one (UX-015)
              setEditing((v) => !v)
            }}
            aria-expanded={editing}
          >
            {editing ? 'Close' : 'Edit'}
            <span className="sr-only"> {m.address}</span>
          </button>
          {m.isActive && !m.isDefault && (
            <button type="button" disabled={patch.isPending} onClick={() => patch.mutate({ isDefault: true })}>
              Make default<span className="sr-only"> {m.address}</span>
            </button>
          )}
          <button
            type="button"
            disabled={patch.isPending}
            onClick={() => {
              // D28: deactivating is allowed (even the default mailbox) but never silently.
              if (m.isActive && !window.confirm(`Deactivate ${m.address}? Tickets that use this mailbox will not be able to email public replies until it is activated again (replies are recorded as not delivered, and the sender is never switched).${m.isDefault ? ' It is the default mailbox: new requests and tickets will have no sender until another default exists.' : ''}`)) return
              patch.mutate({ isActive: !m.isActive })
            }}
          >
            {m.isActive ? 'Deactivate' : 'Activate'}
            <span className="sr-only"> {m.address}</span>
          </button>
        </td>
      </tr>
      {(editing || patch.isError) && (
        <tr>
          <td colSpan={5}>
            {editing && (
              <Dialog title="Edit mailbox" variant="drawer" onClose={() => setEditing(false)}>
              <p className="mailbox-drawer-address">{m.address}</p>
              <form
                className="dialog-form"
                aria-label={`Edit ${m.address}`}
                onSubmit={(e) => {
                  e.preventDefault()
                  patch.mutate({ displayName: name.trim() })
                }}
              >
                <Field label="Display name (the From name of replies)" error={server.displayName} hint="Replies always leave from this mailbox address, whichever agent sends them.">
                  {(p) => <input {...p} value={name} maxLength={200} required onChange={(e) => setName(e.target.value)} />}
                </Field>
                <div className="sender-preview"><span className="muted small">Sender preview</span><strong>{name.trim() || m.displayName}</strong><span>{m.address}</span></div>
                <ErrorNotice error={patch.error} />
                <div className="dialog-actions"><button type="button" onClick={() => setEditing(false)}>Cancel</button><button type="submit" className="primary" disabled={patch.isPending}>Save</button></div>
              </form>
              </Dialog>
            )}
            {!editing && <ErrorNotice error={patch.error} />}
          </td>
        </tr>
      )}
    </>
  )
}

/** An Uncertain send: check the provider first; re-sending is a separate, explicit choice that can duplicate the email. */
function UncertainDialog({ departmentId, item, onClose }: { departmentId: string; item: AttentionItem; onClose: () => void }) {
  const qc = useQueryClient()
  const [warned, setWarned] = useState(false)
  const done = async () => {
    await qc.invalidateQueries({ queryKey: ['mail-attention', departmentId] })
    await qc.invalidateQueries({ queryKey: ['mailboxes', departmentId] })
    onClose()
  }
  const check = useMutation({
    mutationFn: (acceptDuplicateRisk: boolean) => retryMail(departmentId, item.id, acceptDuplicateRisk),
    onSuccess: done,
    onError: (e) => {
      if (isOutcomeUncertain(e)) setWarned(true)
    },
  })
  return (
    <Dialog title="Send outcome uncertain" onClose={onClose}>
      <div className="dialog-form">
        <p>
          The connection to the mail service failed while this message was being sent, so it is not known whether it was sent. It is not retried automatically, because that could email the
          requester twice.
        </p>
        <p className="muted small">Subject: {item.subject}</p>
        {!warned && <p>First check the mailbox&rsquo;s Sent Items. “Check with the mail service” looks for the sent copy and marks the message as sent if it is there.</p>}
        {warned && (
          <p className="notice error" role="alert">
            The mail service has no record of this message. If you re-send it and the original did go out, the requester receives it twice.
          </p>
        )}
        {check.isError && !warned && <ErrorNotice error={check.error} />}
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Close
          </button>
          {!warned ? (
            <button type="button" className="primary" disabled={check.isPending} onClick={() => check.mutate(false)}>
              Check with the mail service
            </button>
          ) : (
            <button type="button" disabled={check.isPending} onClick={() => check.mutate(true)}>
              Re-send anyway (may duplicate)
            </button>
          )}
        </div>
      </div>
    </Dialog>
  )
}

function Attention({ departmentId }: { departmentId: string }) {
  const qc = useQueryClient()
  const [uncertain, setUncertain] = useState<AttentionItem | null>(null)
  const q = useQuery({ queryKey: ['mail-attention', departmentId], queryFn: () => listAttention(departmentId, null), refetchInterval: 30_000 })
  const retry = useMutation({
    mutationFn: (item: AttentionItem) => retryMail(departmentId, item.id),
    onSettled: () => qc.invalidateQueries({ queryKey: ['mail-attention', departmentId] }),
  })
  const release = useMutation({
    mutationFn: (item: AttentionItem) => releaseMail(departmentId, item.id),
    onSettled: () => qc.invalidateQueries({ queryKey: ['mail-attention', departmentId] }),
  })
  if (q.isPending) return <Loading label="Loading mail needing attention" />
  if (q.isError) return <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />
  if (q.data.items.length === 0) return <p className="empty">No failed or ignored mail.</p>
  return (
    <>
      <table className="grid mailboxes-table">
        <caption className="sr-only">Mail needing attention</caption>
        <thead>
          <tr>
            <th scope="col">When</th>
            <th scope="col">Direction</th>
            <th scope="col">From / to</th>
            <th scope="col">Subject</th>
            <th scope="col">Outcome</th>
            <th scope="col">
              <span className="sr-only">Actions</span>
            </th>
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
                  : i.state === 'Uncertain'
                    ? 'Send outcome uncertain (not retried automatically)'
                    : i.lastError === 'ndr_delivery_failed'
                      ? 'Delivery failed (non-delivery report received)'
                      : `Failed${i.lastError ? ` (${i.lastError})` : ''} after ${i.attempts} attempt(s)`}
              </td>
              <td>
                {i.state === 'Uncertain' && (
                  <button type="button" onClick={() => setUncertain(i)}>
                    Resolve…<span className="sr-only"> {i.subject || i.address}</span>
                  </button>
                )}
                {i.state === 'Ignored' && RELEASABLE.has(i.ignoreReason ?? '') && (
                  <button type="button" disabled={release.isPending} onClick={() => release.mutate(i)}>
                    Create ticket
                    <span className="sr-only"> from the message “{i.subject}”</span>
                  </button>
                )}
                {i.state === 'Failed' && (
                  <button type="button" disabled={retry.isPending} onClick={() => retry.mutate(i)}>
                    Retry<span className="sr-only"> {i.subject || i.address}</span>
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <ErrorNotice error={retry.error ?? release.error} />
      {uncertain && <UncertainDialog departmentId={departmentId} item={uncertain} onClose={() => setUncertain(null)} />}
    </>
  )
}

/** Department email configuration (permission department.mailboxes.manage; the server enforces it). */
export default function MailboxesSection({ department }: { department: Department }) {
  const me = useMe()
  const allowed = canInDepartment(me, 'department.mailboxes.manage', department.id)
  const [adding, setAdding] = useState(false)
  const q = useQuery({ queryKey: ['mailboxes', department.id], queryFn: () => listMailboxes(department.id), enabled: allowed })
  if (!allowed) return null
  return (
    <div className="mailboxes-page">
      <section aria-labelledby="mailboxes-h">
        <div className="section-head">
          <h2 id="mailboxes-h">Mailboxes</h2>
          {me.isPlatformAdmin && (
            <button type="button" className="primary" onClick={() => setAdding(true)}>
              Add mailbox
            </button>
          )}
        </div>
        <p className="muted">
          Connect shared mailboxes, manage sender identity and monitor mail health for this department.
        </p>
        {q.data && <div className="mail-summary" aria-label="Mailbox summary"><div><strong>{q.data.filter(m => m.isActive).length}</strong><span>Active mailboxes</span></div><div><strong>{q.data.filter(m => m.health?.status === 'Attention' || m.health?.status === 'Failed').length}</strong><span>Need attention</span></div><div><span>Default sender</span><strong className="mail-summary-default">{q.data.find(m => m.isDefault && m.isActive)?.address ?? 'No active default'}</strong></div></div>}
        {q.isPending && <Loading label="Loading mailboxes" />}
        {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
        {q.isSuccess && q.data.length === 0 && <p className="empty">No mailbox configured yet: replies are recorded but not emailed.</p>}
        {q.isSuccess && q.data.length > 0 && (
          <div className="table-scroll">
          <table className="grid mailboxes-table">
            <caption className="sr-only">Mailboxes of {department.name}</caption>
            <thead>
              <tr>
                <th scope="col">Mailbox / sender</th>
                <th scope="col">Status & health</th>
                <th scope="col">Last sync</th>
                <th scope="col">Mail needing attention</th>
                <th scope="col">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {q.data.map((m) => (
                <MailboxRow key={m.id} departmentId={department.id} mailbox={m} />
              ))}
            </tbody>
          </table>
          </div>
        )}
        {adding && <CreateMailbox departmentId={department.id} onClose={() => setAdding(false)} />}
      </section>
      <p className="mail-help muted small">Each ticket keeps its stored mailbox for From and Reply-To. Changing the default affects new tickets only. An unavailable mailbox never falls back to a different sender.</p>
      <section aria-labelledby="attention-h">
        <h2 id="attention-h">Mail needing attention</h2>
        <p className="muted">Messages that could not be processed or sent, and mail that was ignored on purpose (automatic replies, mailing lists, loops).</p>
        <Attention departmentId={department.id} />
      </section>
    </div>
  )
}
