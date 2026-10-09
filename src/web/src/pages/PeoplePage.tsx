import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { createInvitation, listInvitations, resendInvitation, revokeInvitation, type Invitation, type IssuedInvitation } from '../api/invitations'
import { fieldErrors } from '../api/errors'
import { listDepartments } from '../api/organisation'
import { ROLE_LABEL, ROLES } from '../api/schemas'
import { ErrorNotice, Field, Loading, PageHeader, StatusBadge, type Tone } from '../components/common'
import { formatDateTime } from '../lib/format'

const EMAIL_TEXT: Record<Invitation['emailState'], string> = {
  Accepted: 'Sent (accepted for delivery)',
  NotConfigured: 'Not sent: no system mailbox is configured',
  Failed: 'Not sent: the mail service could not send it',
  Uncertain: 'Outcome uncertain: it may have been sent',
}
const STATUS_TONE: Record<Invitation['status'], Tone> = { Pending: 'info', Accepted: 'ok', Revoked: 'off', Expired: 'warn' }

function IssuedLink({ issued, onClose }: { issued: IssuedInvitation; onClose: () => void }) {
  const i = issued.invitation
  return (
    <div className="notice info people-issued" role="status">
      <p>
        <strong>{i.emailState === 'Accepted' ? `Invitation sent to ${i.email}.` : `Invitation created for ${i.email}, but the email ${i.emailState === 'Uncertain' ? 'may not have been sent' : 'was not sent'}.`}</strong> {EMAIL_TEXT[i.emailState]}.
      </p>
      <p className="muted small">This link is shown once. It works for the invited address only, once, until {formatDateTime(i.expiresAt)}.</p>
      <div className="m365-actions">
        <code className="people-link">{issued.activationUrl}</code>
        <button type="button" onClick={() => void navigator.clipboard?.writeText(issued.activationUrl)}>
          Copy link
        </button>
        <button type="button" onClick={onClose}>
          Dismiss
        </button>
      </div>
    </div>
  )
}

function InviteForm({ onIssued }: { onIssued: (i: IssuedInvitation) => void }) {
  const qc = useQueryClient()
  const departments = useQuery({ queryKey: ['departments'], queryFn: listDepartments, staleTime: 30_000 })
  const active = (departments.data ?? []).filter((d) => d.isActive)
  const [form, setForm] = useState({ displayName: '', email: '', departmentId: '', role: 'Agent' })
  const create = useMutation({
    mutationFn: () => createInvitation({ ...form, displayName: form.displayName.trim(), email: form.email.trim() }),
    onSuccess: (issued) => {
      onIssued(issued)
      setForm((f) => ({ ...f, displayName: '', email: '' }))
      void qc.invalidateQueries({ queryKey: ['invitations'] })
    },
  })
  const server = fieldErrors(create.error)
  const set = (patch: Partial<typeof form>) => setForm((f) => ({ ...f, ...patch }))
  function submit(e: FormEvent) {
    e.preventDefault()
    if (!create.isPending) create.mutate()
  }
  return (
    <form className="people-form" onSubmit={submit} aria-label="Invite a person" noValidate>
      <Field label="Name" error={server.displayName}>
        {(p) => <input {...p} value={form.displayName} maxLength={100} onChange={(e) => set({ displayName: e.target.value })} />}
      </Field>
      <Field label="Email address" error={server.email}>
        {(p) => <input {...p} type="email" value={form.email} maxLength={320} onChange={(e) => set({ email: e.target.value })} />}
      </Field>
      <Field label="Department" error={server.departmentId}>
        {(p) => (
          <select {...p} value={form.departmentId} onChange={(e) => set({ departmentId: e.target.value })}>
            <option value="">Choose a department</option>
            {active.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
        )}
      </Field>
      <Field label="Role" error={server.role}>
        {(p) => (
          <select {...p} value={form.role} onChange={(e) => set({ role: e.target.value })}>
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {ROLE_LABEL[r]}
              </option>
            ))}
          </select>
        )}
      </Field>
      <div className="people-submit">
        <button type="submit" className="primary" disabled={create.isPending}>
          {create.isPending ? 'Sending…' : 'Send invitation'}
        </button>
      </div>
      <div className="people-error">
        <ErrorNotice error={create.error && Object.keys(server).length === 0 ? create.error : null} />
      </div>
    </form>
  )
}

function Row({ i, onIssued }: { i: Invitation; onIssued: (i: IssuedInvitation) => void }) {
  const qc = useQueryClient()
  const refresh = () => qc.invalidateQueries({ queryKey: ['invitations'] })
  const resend = useMutation({
    mutationFn: () => resendInvitation(i.id),
    onSuccess: (issued) => {
      onIssued(issued)
      void refresh()
    },
  })
  const revoke = useMutation({ mutationFn: () => revokeInvitation(i.id), onSuccess: () => void refresh() })
  const open = i.status === 'Pending' || i.status === 'Expired'
  const error = resend.error ?? revoke.error
  return (
    <>
      <tr>
        <td>{i.displayName}</td>
        <td>{i.email}</td>
        <td>{i.departmentName}</td>
        <td>{ROLE_LABEL[i.role] ?? i.role}</td>
        <td>
          <StatusBadge tone={STATUS_TONE[i.status]}>{i.status}</StatusBadge>
        </td>
        <td className="muted small">{i.status === 'Accepted' ? `Activated ${formatDateTime(i.acceptedAt ?? i.createdAt)}` : EMAIL_TEXT[i.emailState]}</td>
        <td className="people-actions">
          {open && (
            <>
              <button type="button" disabled={resend.isPending || revoke.isPending} onClick={() => resend.mutate()}>
                Resend
              </button>
              <button type="button" disabled={resend.isPending || revoke.isPending} onClick={() => revoke.mutate()}>
                Revoke
              </button>
            </>
          )}
        </td>
      </tr>
      {error && (
        <tr>
          <td colSpan={7}>
            <ErrorNotice error={error} />
          </td>
        </tr>
      )}
    </>
  )
}

/** Platform administration: invite a person to a department. They activate by opening the emailed link and signing in with Microsoft 365. */
export default function PeoplePage() {
  const q = useQuery({ queryKey: ['invitations'], queryFn: listInvitations })
  const [issued, setIssued] = useState<IssuedInvitation | null>(null)
  return (
    <section aria-label="People">
      <PageHeader title="People" subtitle="Invite a person to a department. They receive an email with a link, open it and sign in with their Microsoft 365 account; nothing is granted before that." />
      {issued && <IssuedLink issued={issued} onClose={() => setIssued(null)} />}
      <h2>Invite a person</h2>
      <InviteForm onIssued={setIssued} />
      <h2>Invitations</h2>
      {q.isPending && <Loading label="Loading invitations" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && q.data.length === 0 && <p className="empty small">No invitations yet.</p>}
      {q.data && q.data.length > 0 && (
        <div className="table-wrap">
          <table className="grid compact">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Email</th>
                <th scope="col">Department</th>
                <th scope="col">Role</th>
                <th scope="col">Status</th>
                <th scope="col">Email</th>
                <th scope="col">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {q.data.map((i) => (
                <Row key={i.id} i={i} onIssued={setIssued} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
