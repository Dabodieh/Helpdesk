import { useIsFetching, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, Navigate, useParams } from 'react-router-dom'
import { getDashboard, getVolume, type Dashboard } from '../api/dashboard'
import { canTicket } from '../api/permissions'
import { listTickets } from '../api/tickets'
import type { SlaFilter } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { ActivityChart, AttentionList, BacklogAge, KpiCard, OldestTable, Panel, ServiceHealth, AgentWorkload, queueUrl, type AttentionRow } from '../components/DashboardParts'
import { ErrorNotice, Loading, PageHeader, StatusBadge } from '../components/common'
import { Icon } from '../components/icons'

const RANGES = [7, 14, 30] as const
const isDashboardQuery = (key: readonly unknown[]) => typeof key[0] === 'string' && key[0].startsWith('dashboard')

/** Tickets of one SLA state in this department (the queue engine's own filter, so the list and its "view all" link are the same set). */
const attention = (departmentId: string, sla: SlaFilter) =>
  listTickets({ queue: 'all', statuses: ['Open', 'Pending'], priorities: [], departmentId, sla, sort: 'sla_due_asc' }, null)

function ActivityPanel({ departmentId }: { departmentId: string }) {
  const [days, setDays] = useState<(typeof RANGES)[number]>(14)
  const q = useQuery({ queryKey: ['dashboard-volume', departmentId, days], queryFn: () => getVolume(departmentId, days), refetchInterval: 120_000 })
  return (
    <Panel
      title="Ticket activity"
      id="activity-h"
      className="span-7"
      action={undefined}
    >
      <div className="dash-range" role="group" aria-label="Period">
        {RANGES.map((r) => (
          <button key={r} type="button" aria-pressed={days === r} className={days === r ? 'on' : ''} onClick={() => setDays(r)}>
            {r} days
          </button>
        ))}
      </div>
      {q.isPending && <Loading label="Loading ticket activity" />}
      {q.isError && !q.data && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && <ActivityChart volume={q.data} />}
    </Panel>
  )
}

function AttentionPanel({ d }: { d: Dashboard }) {
  const id = d.departmentId
  const overdue = useQuery({ queryKey: ['dashboard-attention', id, 'breached'], queryFn: () => attention(id, 'breached'), refetchInterval: 60_000, enabled: d.sla.overdue > 0 })
  const soon = useQuery({ queryKey: ['dashboard-attention', id, 'duesoon'], queryFn: () => attention(id, 'duesoon'), refetchInterval: 60_000, enabled: d.sla.dueSoon > 0 })
  const toRows = (items: { id: string; reference: string; subject: string; priority: string; assignee: { displayName: string } | null; sla?: { dueAt?: string | null } | null }[] | undefined, breached: boolean): AttentionRow[] =>
    (items ?? []).map((t) => ({ id: t.id, reference: t.reference, subject: t.subject, priority: t.priority, assignee: t.assignee?.displayName ?? null, breached, dueAt: t.sla?.dueAt ?? null }))
  const rows = [...toRows(overdue.data?.items, true), ...toRows(soon.data?.items, false)].slice(0, 6)
  const total = d.sla.overdue + d.sla.dueSoon
  const loading = (d.sla.overdue > 0 && overdue.isPending) || (d.sla.dueSoon > 0 && soon.isPending)
  const failed = (overdue.isError && !overdue.data) || (soon.isError && !soon.data)
  return (
    <Panel title="Needs attention" id="attention-list-h" className="span-4" action={total > 0 ? { to: queueUrl(id, { sla: d.sla.overdue > 0 ? 'breached' : 'duesoon' }), label: 'Open queue' } : undefined}>
      {loading && <Loading label="Loading tickets" />}
      {failed && <ErrorNotice error={overdue.error ?? soon.error} onRetry={() => void Promise.all([overdue.refetch(), soon.refetch()])} />}
      {!loading && !failed && <AttentionList rows={rows} total={total} />}
    </Panel>
  )
}

function Content({ d }: { d: Dashboard }) {
  const id = d.departmentId
  const active = d.backlog.open + d.backlog.pending
  const delivery = d.sends.failedTickets + d.sends.uncertainTickets
  return (
    <div className="dash">
      <ul className="dash-kpis" aria-label="Needs attention now">
        <KpiCard label="Active tickets" value={active} sub={`${d.backlog.open} open · ${d.backlog.pending} pending`} to={queueUrl(id)} tone="neutral" icon="inbox" />
        <KpiCard label="Unassigned" value={d.backlog.unassigned} to={queueUrl(id, { assignee: 'none' })} tone="warn" icon="unassigned" />
        <KpiCard label="Awaiting first response" value={d.backlog.awaitingFirstResponse} to={queueUrl(id, { sla: 'awaitingresponse' })} tone="warn" icon="reply" />
        <KpiCard label="SLA overdue" value={d.sla.overdue} to={queueUrl(id, { sla: 'breached' })} tone="bad" icon="alert" />
        <KpiCard label="Due soon" value={d.sla.dueSoon} sub={`within ${d.sla.dueSoonWindowMinutes / 60} h`} to={queueUrl(id, { sla: 'duesoon' })} tone="warn" icon="clock" />
        <KpiCard
          label="Email delivery problems"
          value={delivery}
          sub={delivery > 0 ? `${d.sends.failedTickets} failed · ${d.sends.uncertainTickets} uncertain` : undefined}
          to={delivery > 0 ? queueUrl(id, { send: d.sends.failedTickets > 0 ? 'failed' : 'uncertain' }) : undefined}
          tone={d.sends.failedTickets > 0 ? 'bad' : 'warn'}
          icon="nomail"
        />
      </ul>

      <div className="dash-grid">
        <ActivityPanel departmentId={id} />
        <Panel title="Workload by assignee" id="agents-h" className="span-5" action={{ to: queueUrl(id), label: 'All tickets' }}>
          <AgentWorkload agents={d.agents} dept={id} />
        </Panel>

        <Panel title="Backlog age" id="age-h" className="span-4">
          <BacklogAge ageing={d.ageing} />
        </Panel>
        <AttentionPanel d={d} />
        <Panel title="Service health" id="health-h" className="span-4">
          <ServiceHealth d={d} />
        </Panel>

        <Panel title="Oldest unresolved" id="oldest-h" className="span-12" action={{ to: queueUrl(id), label: 'View in queue' }}>
          <OldestTable oldest={d.oldest} />
        </Panel>
      </div>
    </div>
  )
}

/** Current operational state of ONE department the caller can read tickets in. Never an aggregate across departments. */
export default function DashboardPage() {
  const { departmentId } = useParams()
  const me = useMe()
  const qc = useQueryClient()
  const readable = me.memberships.filter((m) => canTicket(me, 'tickets.read', m.departmentId))
  const q = useQuery({ queryKey: ['dashboard', departmentId], queryFn: () => getDashboard(departmentId!), enabled: !!departmentId, refetchInterval: 60_000 })
  const refreshing = useIsFetching({ predicate: (query) => isDashboardQuery(query.queryKey) }) > 0

  if (!departmentId) {
    if (readable.length === 1) return <Navigate to={`/dashboard/${readable[0]!.departmentId}`} replace />
    return (
      <div className="page">
        <PageHeader title="Dashboard" subtitle="Choose a department." />
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

  const updated = q.data ? new Date(q.data.generatedAt).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }) : null
  return (
    <div className="page dashboard">
      <PageHeader
        title={q.data ? `${q.data.departmentName} dashboard` : 'Dashboard'}
        subtitle={
          q.data ? (
            <>
              Updated {updated} {q.isError && <StatusBadge tone="warn">Could not refresh: showing the last data</StatusBadge>}
            </>
          ) : undefined
        }
        actions={
          <>
            {readable.length > 1 && <Link to="/dashboard">Change department</Link>}
            <button type="button" className="dash-refresh" onClick={() => void qc.invalidateQueries({ predicate: (query) => isDashboardQuery(query.queryKey) })} disabled={refreshing} aria-busy={refreshing}>
              <Icon name="clock" /> {refreshing ? 'Refreshing…' : 'Refresh'}
            </button>
          </>
        }
      />
      {q.isPending && <Loading label="Loading the dashboard" />}
      {q.isError && !q.data && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && <Content d={q.data} />}
    </div>
  )
}
