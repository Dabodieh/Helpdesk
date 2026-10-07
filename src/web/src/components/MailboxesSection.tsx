import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { createMailbox, listAttention, listMailboxes, retryMail, updateMailbox, type AttentionItem, type Mailbox } from '../api/mail'
import { canInDepartment } from '../api/permissions'
import type { Department } from '../api/schemas'
import { useMe } from '../auth'
import { formatDateTime } from '../lib/format'
import AutoReplySection from './AutoReplySection'
import { ActiveBadge, ErrorNotice, Field, Loading, StatusBadge, type Tone } from './common'
import { isOutcomeUncertain } from '../api/errors'
import Dialog from './Dialog'

const HEALTH_TONE: Record<string, Tone> = { Healthy: 'ok', Attention: 'warn', Failed: 'bad', NotConfigured: 'off', Disabled: 'off' }
const HEALTH_LABEL: Record<string, string> = { Healthy: 'Healthy', Attention: 'Attention', Failed: 'Failed', NotConfigured: 'Not set up', Disabled: 'Disabled' }
const HEALTH_REASON: Record<string, string> = {
  subscription_not_created: 'No inbound subscription yet',
  subscription_missing: 'Inbound subscription is missing',
  subscription_failed: 'Inbound subscription failed',
  subscription_expired: 'Inbound subscription expired',
  subscription_expiring: 'Inbound subscription renewal is overdue',
  sync_failing: 'Inbound sync is failing',
  sync_stale: 'Inbound sync has not run recently',
  outbound_uncertain: 'Some sends have an uncertain outcome',
  outbound_failed: 'Some sends failed',
  inbound_failed: 'Some inbound mail failed',
  mailbox_inactive: 'Mailbox is deactivated',
}

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
    <Dialog title="Add mailbox" onClose={onClose}>
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
        <td>
          {m.address} {m.isDefault && <span className="badge">Default</span>}
        </td>
        <td>{m.displayName}</td>
        <td>
          <ActiveBadge active={m.isActive} />
        </td>
        <td>
          {m.health ? (
            <>
              <StatusBadge tone={HEALTH_TONE[m.health.status] ?? 'off'}>{HEALTH_LABEL[m.health.status] ?? m.health.status}</StatusBadge>
              {m.health.reasons.length > 0 && <div className="muted small">{m.health.reasons.map((r) => HEALTH_REASON[r] ?? r).join(' · ')}</div>}
              <div className="muted small">{m.lastSendAt ? `Last send accepted ${formatDateTime(m.lastSendAt)}` : 'No send recorded yet'}</div>
            </>
          ) : null}
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
          <td colSpan={8}>
            {editing && (
              <form
                className="form inline"
                aria-label={`Edit ${m.address}`}
                onSubmit={(e) => {
                  e.preventDefault()
                  patch.mutate({ displayName: name.trim() })
                }}
              >
                <Field label="Display name (the From name of replies)" error={server.displayName} hint="Replies always leave from this mailbox address, whichever agent sends them.">
                  {(p) => <input {...p} value={name} maxLength={200} required onChange={(e) => setName(e.target.value)} />}
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
                  : i.state === 'Uncertain'
                    ? 'Send outcome uncertain (not retried automatically)'
                    : i.lastError === 'ndr_delivery_failed'
                      ? 'Delivery failed (non-delivery report received)'
                      : `Failed${i.lastError ? ` (${i.lastError})` : ''} after ${i.attempts} attempt(s)`}
              </td>
              <td>
                {i.state === 'Uncertain' && (
                  <button type="button" onClick={() => setUncertain(i)}>
                    Resolve…
                  </button>
                )}
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
    <>
      <section aria-labelledby="mailboxes-h">
        <div className="section-head">
          <h2 id="mailboxes-h">Mailboxes</h2>
          <button type="button" className="primary" onClick={() => setAdding(true)}>
            Add mailbox
          </button>
        </div>
        <p className="muted">
          Email sent to these shared mailboxes becomes tickets of this department. A ticket keeps the mailbox it arrived at (or was created with) as its sender for good; replies are sent from it with that address as Reply-To, whichever agent or team handles the ticket.
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
                <th scope="col">Health</th>
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
        {adding && <CreateMailbox departmentId={department.id} onClose={() => setAdding(false)} />}
      </section>
      <AutoReplySection department={department} />
      <section aria-labelledby="attention-h">
        <h2 id="attention-h">Mail needing attention</h2>
        <p className="muted">Messages that could not be processed or sent, and mail that was ignored on purpose (automatic replies, mailing lists, loops).</p>
        <Attention departmentId={department.id} />
      </section>
    </>
  )
}
