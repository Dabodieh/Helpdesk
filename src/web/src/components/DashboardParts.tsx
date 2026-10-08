import { Link } from 'react-router-dom'
import type { Dashboard, Volume } from '../api/dashboard'
import { formatDateTime, timeAgo } from '../lib/format'
import { HEALTH_LABEL, HEALTH_REASON, HEALTH_TONE } from '../lib/mailHealth'
import { Icon } from './icons'

type IconName = Parameters<typeof Icon>[0]['name']
import { StatusBadge, type Tone } from './common'

/** Drill-downs are ordinary queue URLs (the queue engine is the only one): `dept` always names the department, so a figure and its list are the same set. */
export const queueUrl = (dept: string, params: Record<string, string> = {}) => `/queue/all?${new URLSearchParams({ dept, ...params }).toString()}`

// ---------- KPI cards ----------

export type KpiTone = 'neutral' | 'bad' | 'warn' | 'ok'

/** One headline figure. Colour only appears when the figure needs attention (value above zero); it is always paired with text, never colour alone. */
export function KpiCard({ label, value, sub, to, tone, icon }: { label: string; value: number; sub?: string; to?: string; tone: KpiTone; icon: IconName }) {
  const active = value > 0 && tone !== 'neutral' ? tone : 'neutral'
  const body = (
    <>
      <span className="kpi-icon" aria-hidden="true">
        <Icon name={icon} />
      </span>
      <span className="kpi-value">{value}</span>
      <span className="kpi-label">{label}</span>
      {sub && <span className="kpi-sub">{sub}</span>}
    </>
  )
  return <li>{to ? <Link to={to} className={`kpi kpi-${active} kpi-link`}>{body}</Link> : <span className={`kpi kpi-${active}`}>{body}</span>}</li>
}

// ---------- ticket activity chart ----------

const dayLabel = (iso: string) => new Date(`${iso}T12:00:00`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })

/** Axis maximum with a whole-number midline: an even number up to 20, then multiples of 10 (and 50 above 100), so the gridlines read as round numbers. */
function niceMax(v: number) {
  if (v <= 4) return 4
  if (v <= 20) return v + (v % 2)
  if (v <= 100) return Math.ceil(v / 10) * 10
  return Math.ceil(v / 50) * 50
}

/**
 * Received versus resolved per day, from the server's per-day series (the same definitions as the department volume report). Nothing is estimated: a day with no tickets
 * is an empty slot, and a period with nothing at all says so instead of drawing an empty chart.
 */
export function ActivityChart({ volume }: { volume: Volume }) {
  const days = volume.daily
  const empty = volume.received === 0 && volume.resolved === 0
  const W = 640
  const H = 210
  const pad = { l: 34, r: 8, t: 10, b: 26 }
  const max = niceMax(Math.max(1, ...days.map((d) => Math.max(d.received, d.resolved))))
  const slot = (W - pad.l - pad.r) / Math.max(1, days.length)
  const bar = Math.max(3, Math.min(16, slot / 2 - 2))
  const y = (v: number) => pad.t + (H - pad.t - pad.b) * (1 - v / max)
  const every = Math.ceil(days.length / 8)
  const ticks = [0, max / 2, max]
  return (
    <div className="activity">
      <ul className="activity-totals" aria-label="Totals for the period">
        <li>
          <span className="swatch swatch-received" aria-hidden="true" />
          <strong>{volume.received}</strong> received
        </li>
        <li>
          <span className="swatch swatch-resolved" aria-hidden="true" />
          <strong>{volume.resolved}</strong> resolved
        </li>
        <li className="muted">{volume.resolved - volume.received >= 0 ? '+' : '−'}{Math.abs(volume.resolved - volume.received)} net on the backlog</li>
      </ul>
      {empty ? (
        <p className="empty small">No tickets were received or resolved in this period.</p>
      ) : (
        <>
          <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`Tickets received and resolved per day, ${dayLabel(volume.from)} to ${dayLabel(volume.to)}`}>
            {ticks.map((t) => (
              <g key={t}>
                <line x1={pad.l} x2={W - pad.r} y1={y(t)} y2={y(t)} className="chart-grid" />
                <text x={pad.l - 6} y={y(t) + 3} textAnchor="end" className="chart-axis">
                  {t}
                </text>
              </g>
            ))}
            {days.map((d, i) => {
              const cx = pad.l + slot * i + slot / 2
              return (
                <g key={d.date}>
                  <title>{`${dayLabel(d.date)}: ${d.received} received, ${d.resolved} resolved`}</title>
                  <rect x={cx - bar - 1} y={y(d.received)} width={bar} height={Math.max(0, H - pad.b - y(d.received))} rx="2" className="bar-received" />
                  <rect x={cx + 1} y={y(d.resolved)} width={bar} height={Math.max(0, H - pad.b - y(d.resolved))} rx="2" className="bar-resolved" />
                  {i % every === 0 && (
                    <text x={cx} y={H - 8} textAnchor="middle" className="chart-axis">
                      {dayLabel(d.date)}
                    </text>
                  )}
                </g>
              )
            })}
          </svg>
          <table className="sr-only">
            <caption>Tickets received and resolved per day</caption>
            <thead>
              <tr>
                <th>Day</th>
                <th>Received</th>
                <th>Resolved</th>
              </tr>
            </thead>
            <tbody>
              {days.map((d) => (
                <tr key={d.date}>
                  <td>{dayLabel(d.date)}</td>
                  <td>{d.received}</td>
                  <td>{d.resolved}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}

// ---------- backlog age ----------

/** Horizontal bars: the older the bucket, the more it asks for attention (neutral, then warning, then danger tone for the oldest). */
export function BacklogAge({ ageing }: { ageing: Dashboard['ageing'] }) {
  const total = ageing.reduce((n, a) => n + a.count, 0)
  const max = Math.max(1, ...ageing.map((a) => a.count))
  if (total === 0) return <p className="empty small">No open or pending tickets.</p>
  return (
    <ul className="age-bars" aria-label="Backlog age">
      {ageing.map((a, i) => {
        const tone = i >= ageing.length - 1 ? 'bad' : i >= ageing.length - 2 ? 'warn' : 'neutral'
        return (
          <li key={a.key}>
            <span className="age-label">{a.label}</span>
            <span className="age-track" aria-hidden="true">
              <span className={`age-fill age-${tone}`} style={{ width: `${(a.count / max) * 100}%` }} />
            </span>
            <span className="age-count">
              {a.count}
              {a.count > 0 && <span className="muted small"> {Math.round((a.count / total) * 100)}%</span>}
            </span>
          </li>
        )
      })}
    </ul>
  )
}

// ---------- agent workload ----------

/**
 * Who currently holds the Department's open backlog: assigned work in the neutral tone, the unassigned queue in the warning tone; overdue work is a counted badge.
 * A snapshot of where work sits, listed alphabetically on purpose (never ordered or scored by volume).
 */
export function AgentWorkload({ agents, dept }: { agents: Dashboard['agents']; dept: string }) {
  if (agents.length === 0) return <p className="empty small">Nothing in the backlog.</p>
  const max = Math.max(1, ...agents.map((a) => a.total))
  return (
    <>
      <p className="workload-legend" aria-hidden="true">
        <span><span className="swatch swatch-received" />Assigned</span>
        <span><span className="swatch swatch-unassigned" />Unassigned</span>
      </p>
      <ul className="workload-bars" aria-label="Backlog by assignee">
        {agents.map((a) => {
          const unassigned = !a.userId
          return (
            <li key={a.userId ?? 'none'}>
              <div className="workload-head">
                <Link to={queueUrl(dept, { assignee: a.userId ?? 'none' })} className="workload-name">
                  {a.name}
                </Link>
                <span className="workload-total">{a.total}</span>
              </div>
              <div className="workload-track" aria-hidden="true">
                <span className={unassigned ? 'workload-unassigned' : 'workload-assigned'} style={{ width: `${(a.total / max) * 100}%` }} />
              </div>
              {a.overdue > 0 && (
                <p className="workload-meta small muted">
                  <span className="workload-overdue">{a.overdue} SLA overdue</span>
                </p>
              )}
            </li>
          )
        })}
      </ul>
    </>
  )
}

// ---------- panels ----------

export function Panel({ title, id, className, action, children }: { title: string; id: string; className?: string; action?: { to: string; label: string }; children: React.ReactNode }) {
  return (
    <section className={`dash-panel ${className ?? ''}`} aria-labelledby={id}>
      <header className="dash-panel-head">
        <h2 id={id}>{title}</h2>
        {action && (
          <Link to={action.to} className="dash-panel-action">
            {action.label} →
          </Link>
        )}
      </header>
      {children}
    </section>
  )
}

const dueText = (iso: string | null | undefined, breached: boolean, now = Date.now()) => {
  if (!iso) return breached ? 'Overdue' : 'Due soon'
  const diff = Date.parse(iso) - now
  if (Number.isNaN(diff)) return breached ? 'Overdue' : 'Due soon'
  const abs = timeAgo(new Date(now - Math.abs(diff)).toISOString(), now)
  return breached ? `Overdue ${abs === 'now' ? '' : abs}`.trim() : `Due in ${abs === 'now' ? '<1m' : abs}`
}

export type AttentionRow = { id: string; reference: string; subject: string; priority: string; assignee: string | null; breached: boolean; dueAt: string | null }

/** Concise list of tickets that need action now (overdue first, then due soon); the full set is one click away in the queue. */
export function AttentionList({ rows, total }: { rows: AttentionRow[]; total: number }) {
  if (rows.length === 0) return <p className="empty small">No ticket is overdue or due soon.</p>
  return (
    <ul className="attn-list" aria-label="Tickets needing attention">
      {rows.map((r) => (
        <li key={r.id}>
          <div className="attn-main">
            <Link to={`/tickets/${encodeURIComponent(r.reference)}`} className="ref">
              {r.reference}
            </Link>
            <span className="attn-subject">{r.subject}</span>
          </div>
          <div className="attn-meta small muted">
            <StatusBadge tone={r.breached ? 'bad' : 'warn'}>{dueText(r.dueAt, r.breached)}</StatusBadge>
            <span>{r.priority}</span>
            <span>{r.assignee ?? 'Unassigned'}</span>
          </div>
        </li>
      ))}
      {total > rows.length && <li className="attn-more muted small">and {total - rows.length} more in the queues</li>}
    </ul>
  )
}

// ---------- oldest unresolved ----------

export function OldestTable({ oldest }: { oldest: Dashboard['oldest'] }) {
  if (oldest.length === 0) return <p className="empty small">No open or pending tickets.</p>
  return (
    <div className="dash-table-wrap">
      <table className="grid compact dash-table">
        <caption className="sr-only">Oldest unresolved tickets</caption>
        <thead>
          <tr>
            <th scope="col">Ticket</th>
            <th scope="col">Subject</th>
            <th scope="col">Status</th>
            <th scope="col">Priority</th>
            <th scope="col">Opened</th>
            <th scope="col">Assignee</th>
          </tr>
        </thead>
        <tbody>
          {oldest.map((o) => (
            <tr key={o.reference}>
              <td>
                <Link to={`/tickets/${encodeURIComponent(o.reference)}`} className="ref">
                  {o.reference}
                </Link>
              </td>
              <td className="dash-subject">
                {o.subject} {o.slaOverdue && <StatusBadge tone="bad">SLA overdue</StatusBadge>}
              </td>
              <td>{o.status}</td>
              <td>{o.priority}</td>
              <td title={formatDateTime(o.createdAt)}>{timeAgo(o.createdAt)}</td>
              <td>{o.assignee ?? <span className="muted">Unassigned</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// ---------- service health ----------

export function ServiceHealth({ d }: { d: Dashboard }) {
  const id = d.departmentId
  const failed = d.sends.failedTickets
  const uncertain = d.sends.uncertainTickets
  const tone: Tone = failed > 0 ? 'bad' : uncertain > 0 ? 'warn' : 'ok'
  return (
    <ul className="health-list" aria-label="Service health">
      <li>
        <div className="health-row">
          <strong>Email delivery</strong>
          <StatusBadge tone={tone}>{failed > 0 ? 'Replies not sent' : uncertain > 0 ? 'Outcome uncertain' : 'No problems'}</StatusBadge>
        </div>
        <p className="muted small health-sub">
          {failed === 0 && uncertain === 0 ? (
            'No reply on an open ticket has failed or is in doubt.'
          ) : (
            <>
              {failed > 0 && <Link to={queueUrl(id, { send: 'failed' })}>{failed} failed</Link>}
              {failed > 0 && uncertain > 0 && ' · '}
              {uncertain > 0 && <Link to={queueUrl(id, { send: 'uncertain' })}>{uncertain} uncertain (administrator to check)</Link>}
            </>
          )}
        </p>
      </li>
      {d.mailboxes == null ? (
        <li>
          <p className="muted small">Mailbox health is shown to mailbox administrators.</p>
        </li>
      ) : d.mailboxes.length === 0 ? (
        <li>
          <div className="health-row">
            <strong>Mailboxes</strong>
            <StatusBadge tone="off">None set up</StatusBadge>
          </div>
          <p className="muted small health-sub">No mailbox is configured: replies are recorded but not emailed.</p>
        </li>
      ) : (
        d.mailboxes.map((m) => (
          <li key={m.mailboxId}>
            <div className="health-row">
              <strong>{m.displayName}</strong>
              <StatusBadge tone={HEALTH_TONE[m.status] ?? 'off'}>{HEALTH_LABEL[m.status] ?? m.status}</StatusBadge>
            </div>
            {m.reasons.length > 0 && (
              <p className="muted small health-sub">
                {m.reasons.map((r) => HEALTH_REASON[r] ?? r).join(' · ')} · <Link to={`/admin/departments/${id}?section=mailboxes`}>Manage</Link>
              </p>
            )}
          </li>
        ))
      )}
    </ul>
  )
}
