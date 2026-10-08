import { useQuery } from '@tanstack/react-query'
import { Link, Navigate, useParams } from 'react-router-dom'
import { getDashboard, type Dashboard } from '../api/dashboard'
import { canTicket } from '../api/permissions'
import { useMe } from '../auth'
import { ErrorNotice, Loading, PageHeader, StatusBadge, type Tone } from '../components/common'
import { formatDateTime } from '../lib/format'
import { HEALTH_LABEL, HEALTH_REASON, HEALTH_TONE } from '../lib/mailHealth'

/** Drill-downs are ordinary queue URLs (the queue engine is the only one): `dept` always names the department, so a figure and its list are the same set. */
const queue = (dept: string, params: Record<string, string> = {}) => `/queue/all?${new URLSearchParams({ dept, ...params }).toString()}`

function Figure({ label, value, to, tone, hint }: { label: string; value: number; to?: string; tone?: Tone; hint?: string }) {
  const body = (
    <>
      <span className="figure-value">{value}</span>
      <span className="figure-label">
        {label}
        {hint && <span className="muted small"> {hint}</span>}
      </span>
    </>
  )
  const cls = `figure${value > 0 && tone ? ` figure-${tone}` : ''}`
  return <li>{to ? <Link to={to} className={cls}>{body}</Link> : <span className={cls}>{body}</span>}</li>
}

function Content({ d }: { d: Dashboard }) {
  const id = d.departmentId
  const maxAge = Math.max(1, ...d.ageing.map((a) => a.count))
  const total = d.backlog.open + d.backlog.pending
  return (
    <>
      <section aria-labelledby="attention-h">
        <h2 id="attention-h" className="section-title">
          Needs attention now
        </h2>
        <ul className="figures" aria-label="Needs attention now">
          <Figure label="SLA overdue" value={d.sla.overdue} to={queue(id, { sla: 'breached' })} tone="bad" />
          <Figure label="Due soon" hint={`within ${d.sla.dueSoonWindowMinutes / 60} h`} value={d.sla.dueSoon} to={queue(id, { sla: 'duesoon' })} tone="warn" />
          <Figure label="Unassigned" value={d.backlog.unassigned} to={queue(id, { assignee: 'none' })} tone="warn" />
          <Figure label="Awaiting first response" hint="(human reply)" value={d.backlog.awaitingFirstResponse} to={queue(id, { sla: 'awaitingresponse' })} tone="warn" />
          <Figure label="Replies not sent" value={d.sends.failedTickets} to={queue(id, { send: 'failed' })} tone="bad" hint="(email failed)" />
          <Figure label="Send outcome uncertain" value={d.sends.uncertainTickets} to={queue(id, { send: 'uncertain' })} tone="warn" hint="(administrator to check)" />
        </ul>
      </section>

      <section aria-labelledby="backlog-h">
        <h2 id="backlog-h" className="section-title">
          Backlog
        </h2>
        <ul className="figures" aria-label="Backlog">
          <Figure label="Open" value={d.backlog.open} to={queue(id, { status: 'Open' })} />
          <Figure label="Pending" value={d.backlog.pending} to={queue(id, { status: 'Pending' })} />
          <Figure label="Open and pending" value={total} to={queue(id)} />
        </ul>
        <h3 className="section-title">Age of the backlog</h3>
        <ul className="bars" aria-label="Backlog age">
          {d.ageing.map((a) => (
            <li key={a.key}>
              <span className="bar-label">{a.label}</span>
              <span className="bar-track" aria-hidden="true">
                <span className="bar-fill" style={{ width: `${(a.count / maxAge) * 100}%` }} />
              </span>
              <span className="bar-count">{a.count}</span>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="teams-h">
        <h2 id="teams-h" className="section-title">
          Team workload
        </h2>
        {d.teams.length === 0 ? (
          <p className="empty small">Nothing in the backlog.</p>
        ) : (
          <table className="grid compact">
            <caption className="sr-only">Backlog by team</caption>
            <thead>
              <tr>
                <th scope="col">Team</th>
                <th scope="col">Open and pending</th>
                <th scope="col">Unassigned</th>
                <th scope="col">SLA overdue</th>
              </tr>
            </thead>
            <tbody>
              {d.teams.map((t) => (
                <tr key={t.teamId ?? 'none'}>
                  <td>{t.name}</td>
                  <td>{t.teamId ? <Link to={queue(id, { team: t.teamId })}>{t.total}</Link> : t.total}</td>
                  <td>{t.teamId && t.unassigned > 0 ? <Link to={queue(id, { team: t.teamId, assignee: 'none' })}>{t.unassigned}</Link> : t.unassigned}</td>
                  <td>{t.overdue > 0 ? <strong>{t.overdue}</strong> : 0}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section aria-labelledby="oldest-h">
        <h2 id="oldest-h" className="section-title">
          Oldest unresolved
        </h2>
        {d.oldest.length === 0 ? (
          <p className="empty small">No open or pending tickets.</p>
        ) : (
          <ul className="rows" aria-label="Oldest unresolved tickets">
            {d.oldest.map((o) => (
              <li key={o.reference} className="row">
                <div className="row-main">
                  <div className="row-title">
                    <Link to={`/tickets/${encodeURIComponent(o.reference)}`} className="ref">
                      {o.reference}
                    </Link>
                    <span>{o.subject}</span>
                    {o.slaOverdue && <StatusBadge tone="bad">SLA overdue</StatusBadge>}
                  </div>
                  <p className="muted small row-sub">
                    {o.status} · {o.priority} · opened {formatDateTime(o.createdAt)} · {o.assignee ?? 'Unassigned'}
                    {o.team ? ` · ${o.team}` : ''}
                  </p>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {d.mailboxes && (
        <section aria-labelledby="mail-h">
          <h2 id="mail-h" className="section-title">
            Mailboxes
          </h2>
          {d.mailboxes.length === 0 ? (
            <p className="empty small">No mailbox is configured: replies are recorded but not emailed.</p>
          ) : (
            <ul className="rows" aria-label="Mailbox health">
              {d.mailboxes.map((m) => (
                <li key={m.mailboxId} className="row">
                  <div className="row-main">
                    <div className="row-title">
                      <strong>{m.address}</strong>
                      <StatusBadge tone={HEALTH_TONE[m.status] ?? 'off'}>{HEALTH_LABEL[m.status] ?? m.status}</StatusBadge>
                    </div>
                    {m.reasons.length > 0 && <p className="muted small row-sub">{m.reasons.map((r) => HEALTH_REASON[r] ?? r).join(' · ')}</p>}
                  </div>
                  <Link to={`/admin/departments/${id}?section=mailboxes`} className="row-action">
                    Manage →
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </>
  )
}

/** Current operational state of ONE department the caller can read tickets in. Never an aggregate across departments. */
export default function DashboardPage() {
  const { departmentId } = useParams()
  const me = useMe()
  const readable = me.memberships.filter((m) => canTicket(me, 'tickets.read', m.departmentId))
  const q = useQuery({ queryKey: ['dashboard', departmentId], queryFn: () => getDashboard(departmentId!), enabled: !!departmentId, refetchInterval: 60_000 })

  if (!departmentId) {
    if (readable.length === 1) return <Navigate to={`/dashboard/${readable[0]!.departmentId}`} replace />
    return (
      <div className="page">
        <PageHeader title="Dashboard" subtitle="What needs attention right now. Choose a department: each is shown on its own." />
        {readable.length === 0 ? (
          <p className="empty">The dashboard needs a department membership with ticket access.</p>
        ) : (
          <ul className="rows" aria-label="Departments">
            {readable.map((m) => (
              <li key={m.departmentId} className="row">
                <div className="row-main">
                  <div className="row-title">
                    <strong>{m.departmentName}</strong>
                    <code className="key-chip">{m.departmentKey}</code>
                  </div>
                </div>
                <Link to={`/dashboard/${m.departmentId}`} className="row-action" aria-label={`Open the ${m.departmentName} dashboard`}>
                  Open →
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>
    )
  }

  return (
    <div className="page dashboard">
      <PageHeader
        title={q.data ? `${q.data.departmentName} dashboard` : 'Dashboard'}
        subtitle={q.data ? `Current state as of ${formatDateTime(q.data.generatedAt)}.` : undefined}
        actions={
          <>
            {readable.length > 1 && <Link to="/dashboard">Change department</Link>}
            <button type="button" onClick={() => void q.refetch()} disabled={q.isFetching}>
              Refresh
            </button>
          </>
        }
      />
      {q.isPending && <Loading label="Loading the dashboard" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && <Content d={q.data} />}
    </div>
  )
}
