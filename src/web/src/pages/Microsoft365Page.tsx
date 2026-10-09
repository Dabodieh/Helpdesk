import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { getMicrosoft365, type M365Mailbox, type Microsoft365Status } from '../api/microsoft365'
import { ErrorNotice, Loading, PageHeader, StatusBadge } from '../components/common'
import Microsoft365Wizard from '../components/Microsoft365Wizard'
import { formatDateTime } from '../lib/format'
import { HEALTH_LABEL, HEALTH_REASON, HEALTH_TONE } from '../lib/mailHealth'

const ISSUE_TEXT: Record<string, { tone: 'warn' | 'info' | 'bad'; text: string }> = {
  simulated_provider: { tone: 'info', text: 'Mail runs on the built-in simulator. No Microsoft 365 mailbox is connected.' },
  sign_in_not_configured: { tone: 'warn', text: 'Microsoft Entra sign-in is not configured.' },
  dev_sign_in_enabled: { tone: 'warn', text: 'Development sign-in is enabled. It must be off outside development.' },
  tenant_differs: { tone: 'warn', text: 'Sign-in and mail use different tenants. Check this is intended.' },
  no_mailboxes: { tone: 'info', text: 'No mailbox is connected yet.' },
  mailbox_failed: { tone: 'bad', text: 'At least one mailbox has failed. See the table below.' },
  mailbox_attention: { tone: 'warn', text: 'At least one mailbox needs attention. See the table below.' },
}

function Overview({ s }: { s: Microsoft365Status }) {
  return (
    <ul className="m365-cards" aria-label="Configuration">
      <li>
        <h3>Sign-in</h3>
        <StatusBadge tone={s.signIn.entraConfigured ? 'ok' : 'warn'}>{s.signIn.entraConfigured ? 'Microsoft Entra ID' : 'Not configured'}</StatusBadge>
        <dl className="kv">
          <dt>Tenant</dt>
          <dd>{s.signIn.tenantId ?? '–'}</dd>
          <dt>Client id</dt>
          <dd>{s.signIn.clientId ?? '–'}</dd>
          <dt>Secret</dt>
          <dd>{s.signIn.secretPresent ? 'Configured' : 'None'}</dd>
        </dl>
      </li>
      <li>
        <h3>Mail processing</h3>
        <StatusBadge tone={s.simulated ? 'info' : s.graph.configured ? 'ok' : 'bad'}>{s.simulated ? 'Simulator' : s.graph.configured ? 'Microsoft Graph' : 'Not configured'}</StatusBadge>
        <dl className="kv">
          <dt>Tenant</dt>
          <dd>{s.graph.tenantId ?? '–'}</dd>
          <dt>Client id</dt>
          <dd>{s.graph.clientId ?? '–'}</dd>
          <dt>Credential</dt>
          <dd>{s.graph.credential === 'none' ? 'None' : s.graph.credential === 'certificate' ? 'Certificate' : 'Client secret'}</dd>
        </dl>
      </li>
      <li>
        <h3>Receiving mail</h3>
        <StatusBadge tone={s.notifications.mode === 'webhook' ? 'ok' : 'info'}>{s.notifications.mode === 'webhook' ? 'Notifications + check' : 'Simulator'}</StatusBadge>
        <dl className="kv">
          <dt>Notification URL</dt>
          <dd>{s.notifications.baseUrl ?? '–'}</dd>
          <dt>Regular check</dt>
          <dd>Every {s.notifications.reconcileMinutes} min</dd>
          <dt>Subscription life</dt>
          <dd>{s.notifications.subscriptionLifetimeDays} days</dd>
        </dl>
      </li>
      <li>
        <h3>Mailbox creation</h3>
        <StatusBadge tone="off">{s.provisioning.available ? 'Automatic' : 'Manual'}</StatusBadge>
        <p className="muted small">New shared mailboxes are created by a tenant administrator using a script Helpdesk prepares. Automatic creation needs a separate Exchange administration identity, which is not configured.</p>
      </li>
    </ul>
  )
}

function Permissions({ s }: { s: Microsoft365Status }) {
  return (
    <section aria-labelledby="perm-h">
      <h2 id="perm-h">Required permissions</h2>
      <div className="table-wrap">
        <table className="grid compact">
          <thead>
            <tr>
              <th scope="col">Permission</th>
              <th scope="col">Used for</th>
              <th scope="col">Scope</th>
              <th scope="col">Needed</th>
            </tr>
          </thead>
          <tbody>
            {s.requiredPermissions.map((p) => (
              <tr key={p.name}>
                <td>{p.name}</td>
                <td>{p.purpose}</td>
                <td>{p.scope}</td>
                <td>
                  <StatusBadge tone="off">{p.required ? 'Yes' : 'No'}</StatusBadge>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function MailboxRow({ m }: { m: M365Mailbox }) {
  const [open, setOpen] = useState(false)
  const id = `diag-${m.id}`
  return (
    <>
      <tr>
        <td>
          <strong>{m.address}</strong>
          {m.isDefault && <span className="muted small"> · default</span>}
        </td>
        <td>{m.departmentName}</td>
        <td>
          <StatusBadge tone={HEALTH_TONE[m.health] ?? 'off'}>{HEALTH_LABEL[m.health] ?? m.health}</StatusBadge>
        </td>
        <td>{m.lastSyncAt ? formatDateTime(m.lastSyncAt) : 'Not yet'}</td>
        <td>
          <button type="button" className="linklike" aria-expanded={open} aria-controls={id} onClick={() => setOpen((v) => !v)}>
            {open ? 'Hide' : 'Details'}
          </button>
        </td>
      </tr>
      {open && (
        <tr id={id} className="m365-detail">
          <td colSpan={5}>
            <dl className="kv">
              <dt>Inbound subscription</dt>
              <dd>
                {m.subscriptionState}
                {m.subscriptionExpiresAt ? `, renews before ${formatDateTime(m.subscriptionExpiresAt)}` : ''}
              </dd>
              <dt>Last mail check</dt>
              <dd>{m.lastSyncAt ? `${formatDateTime(m.lastSyncAt)}${m.lastSyncFailed ? ' (failed)' : ''}` : 'Not yet'}</dd>
              <dt>Replies</dt>
              <dd>
                {m.outboundFailed} failed · {m.outboundUncertain} uncertain
                {m.outboundUncertain > 0 && ' (not retried automatically; a mailbox administrator checks them)'}
              </dd>
              <dt>Findings</dt>
              <dd>{m.reasons.length === 0 ? 'None' : m.reasons.map((r) => HEALTH_REASON[r] ?? r).join(' · ')}</dd>
            </dl>
            <Link to={`/admin/departments/${m.departmentId}?section=mailboxes`}>Manage in {m.departmentName}</Link>
          </td>
        </tr>
      )}
    </>
  )
}

function Mailboxes({ s }: { s: Microsoft365Status }) {
  return (
    <section aria-labelledby="mb-h">
      <h2 id="mb-h">Mailboxes</h2>
      {s.mailboxes.length === 0 ? (
        <p className="empty small">No mailbox is connected.</p>
      ) : (
        <div className="table-wrap m365-mailboxes">
          <table className="grid compact">
            <thead>
              <tr>
                <th scope="col">Mailbox</th>
                <th scope="col">Department</th>
                <th scope="col">Health</th>
                <th scope="col">Last check</th>
                <th scope="col">
                  <span className="sr-only">Details</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {s.mailboxes.map((m) => (
                <MailboxRow key={m.id} m={m} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

/** Platform-level view of how Helpdesk uses Microsoft 365. Read-only status, a guided mailbox setup and per-mailbox diagnostics; secrets are never shown or stored here. */
export default function Microsoft365Page() {
  const q = useQuery({ queryKey: ['microsoft-365'], queryFn: getMicrosoft365, refetchInterval: 60_000 })
  return (
    <section aria-label="Microsoft 365">
      <PageHeader
        title="Microsoft 365"
        subtitle="Staff sign-in, mail processing and shared mailboxes. Sign-in, mail processing and mailbox creation use separate identities."
        actions={
          <button type="button" disabled={q.isFetching} onClick={() => void q.refetch()}>
            {q.isFetching ? 'Refreshing…' : 'Refresh'}
          </button>
        }
      />
      {q.isPending && <Loading label="Loading Microsoft 365 status" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && (
        <>
          {q.data.issues.length > 0 && (
            <ul className="m365-issues" aria-label="Findings">
              {q.data.issues.map((code) => {
                const i = ISSUE_TEXT[code]
                return i ? (
                  <li key={code}>
                    <StatusBadge tone={i.tone}>{i.tone === 'bad' ? 'Failed' : i.tone === 'warn' ? 'Attention' : 'Note'}</StatusBadge> {i.text}
                  </li>
                ) : null
              })}
            </ul>
          )}
          <Overview s={q.data} />
          <Mailboxes s={q.data} />
          <Microsoft365Wizard status={q.data} />
          <Permissions s={q.data} />
        </>
      )}
    </section>
  )
}
