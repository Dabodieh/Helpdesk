import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { Link, Navigate, useParams, useSearchParams } from 'react-router-dom'
import { fetchReport, fetchReportTickets, type ReportParams, type ReportTable } from '../api/reports'
import { ErrorNotice, Loading, PageHeader } from '../components/common'
import ReportView, { type DrillRequest } from '../components/analytics/ReportView'
import { Icon } from '../components/icons'
import { useDirectory } from '../hooks/useDirectory'
import { DEFAULT_PARAMS, PRESETS, daysBetween, defaultGroup, downloadCsv, formatCell, presetRange, readStore, reportByKey, today, writeStore, type PresetId } from '../lib/analytics'
import { useAnalyticsDepartments } from './AnalyticsHome'

const PRIORITIES = ['Urgent', 'High', 'Normal', 'Low']
const STATUSES = ['Open', 'Pending', 'Resolved', 'Closed']

function readParams(sp: URLSearchParams): ReportParams {
  const d = DEFAULT_PARAMS()
  const from = sp.get('from') ?? d.from
  const to = sp.get('to') ?? d.to
  const requested = sp.get('group')
  const group = requested && ['day', 'week', 'month'].includes(requested) && !(requested === 'day' && daysBetween(from, to) > 92) ? requested : defaultGroup(from, to)
  return { from, to, group, priority: sp.getAll('priority'), status: sp.getAll('status'), assignee: sp.get('assignee') ?? '', unassigned: sp.get('unassigned') === 'true' }
}

function writeParams(sp: URLSearchParams, p: ReportParams): URLSearchParams {
  const next = new URLSearchParams()
  const dept = sp.get('dept')
  if (dept) next.set('dept', dept)
  next.set('from', p.from)
  next.set('to', p.to)
  next.set('group', p.group)
  p.priority.forEach((v) => next.append('priority', v))
  p.status.forEach((v) => next.append('status', v))
  if (p.unassigned) next.set('unassigned', 'true')
  else if (p.assignee) next.set('assignee', p.assignee)
  return next
}

function presetOf(p: ReportParams): PresetId {
  for (const id of ['today', '7', '30', 'month', 'lastmonth'] as const) {
    const r = presetRange(id)
    if (r.from === p.from && r.to === p.to) return id
  }
  return 'custom'
}

export default function AnalyticsReportPage() {
  const { report: key = '' } = useParams()
  const def = reportByKey(key)
  const departments = useAnalyticsDepartments()
  const [sp, setSp] = useSearchParams()
  const dept = departments.find((d) => d.id === sp.get('dept'))?.id ?? departments[0]?.id ?? ''
  const params = useMemo(() => readParams(sp), [sp])
  const directory = useDirectory(dept || undefined)
  const [drill, setDrill] = useState<DrillRequest | null>(null)
  const [store, setStore] = useState(readStore)
  const [saveName, setSaveName] = useState('')
  const [exported, setExported] = useState('')
  const preset = presetOf(params)

  useEffect(() => {
    if (def) setStore(writeStore((s) => ({ ...s, recent: [def.key, ...s.recent.filter((k) => k !== def.key)].slice(0, 8) })))
  }, [def])
  useEffect(() => setDrill(null), [key, dept, sp])

  const q = useQuery({ queryKey: ['report', dept, key, params], queryFn: () => fetchReport(dept, key, params), enabled: !!dept && !!def, staleTime: 30_000, retry: false })
  const behind = useQuery({ queryKey: ['report-tickets', dept, drill, params], queryFn: () => fetchReportTickets(dept, drill!.metric, params, drill!.bucket), enabled: !!drill && !!dept, retry: false })

  if (!def) return <Navigate to="/analytics" replace />
  if (departments.length === 0) return <Navigate to="/analytics" replace />

  const update = (patch: Partial<ReportParams>) => {
    const next = { ...params, ...patch }
    if (patch.from || patch.to) next.group = defaultGroup(next.from, next.to)
    setSp(writeParams(sp, next), { replace: true })
  }
  const toggle = (list: string[], v: string) => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v])
  const setPreset = (id: PresetId) => {
    if (id !== 'custom') update(presetRange(id))
  }
  const filtersActive = params.priority.length + params.status.length + (params.assignee || params.unassigned ? 1 : 0)
  const saved = store.saved.filter((s) => s.report === key)
  const isFavourite = store.favourites.includes(key)

  const exportRows = (t?: ReportTable) => {
    const r = q.data
    if (!r) return
    const rows: unknown[][] = [[def.title], ['Department', departments.find((d) => d.id === dept)?.name ?? ''], ['Period', `${r.period.from} to ${r.period.to}`, `Grouped by ${r.period.group}`, 'Europe/London'], []]
    const tables = t ? [t] : r.tables
    if (!t) {
      rows.push(['Key figures'], ...r.kpis.map((k) => [k.label, k.value ?? '', k.unit]), [])
      for (const s of r.series) rows.push([s.title], ['', ...s.buckets], ...s.lines.map((l) => [l.label, ...l.values.map((v) => v ?? '')]), [])
    }
    for (const tb of tables) rows.push([tb.title], tb.columns.map((c) => c.label), ...tb.rows.map((row) => tb.columns.map((c, i) => formatCell(row[i], c.type === 'number' ? 'count' : c.type))), [])
    downloadCsv(`${key}${t ? `-${t.key}` : ''}-${params.from}-to-${params.to}.csv`, rows)
    setExported(`Exported ${t ? t.title : def.title}`)
  }

  return (
    <div className="page analytics">
      <PageHeader
        crumbs={<><Link to={`/analytics${dept ? `?dept=${dept}` : ''}`}>Analytics</Link> / {def.category}</>}
        title={def.title}
        subtitle={def.description}
        actions={<>
          <button type="button" className={`ghost star-toggle${isFavourite ? ' on' : ''}`} aria-pressed={isFavourite} onClick={() => setStore(writeStore((s) => ({ ...s, favourites: isFavourite ? s.favourites.filter((k) => k !== key) : [...s.favourites, key] })))}><Icon name="star" size={16} /> {isFavourite ? 'Favourite' : 'Add to favourites'}</button>
          <button type="button" onClick={() => exportRows()} disabled={!q.data}>Export CSV</button>
        </>}
      />
      <div className="report-filters" role="search" aria-label="Report filters">
        {departments.length > 1 && (
          <label className="fsel"><span>Department</span>
            <select value={dept} onChange={(e) => setSp(writeParams(new URLSearchParams({ dept: e.target.value }), params), { replace: true })}>{departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select>
          </label>
        )}
        <label className="fsel"><span>Period</span>
          <select value={preset} onChange={(e) => setPreset(e.target.value as PresetId)}>{PRESETS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}</select>
        </label>
        <label className="fsel"><span>From</span><input type="date" value={params.from} max={params.to} onChange={(e) => e.target.value && update({ from: e.target.value })} /></label>
        <label className="fsel"><span>To</span><input type="date" value={params.to} min={params.from} max={today()} onChange={(e) => e.target.value && update({ to: e.target.value })} /></label>
        <label className="fsel"><span>Group by</span>
          <select value={params.group} onChange={(e) => update({ group: e.target.value })}>
            <option value="day" disabled={daysBetween(params.from, params.to) > 92}>Day</option><option value="week">Week</option><option value="month">Month</option>
          </select>
        </label>
        <details className="report-filter-menu">
          <summary>Filters{filtersActive > 0 ? ` · ${filtersActive}` : ''}</summary>
          <div className="report-filter-panel">
            <fieldset><legend>Priority (when raised)</legend>{PRIORITIES.map((p) => <label key={p}><input type="checkbox" checked={params.priority.includes(p)} onChange={() => update({ priority: toggle(params.priority, p) })} /> {p}</label>)}</fieldset>
            <fieldset><legend>Status (now)</legend>{STATUSES.map((s) => <label key={s}><input type="checkbox" checked={params.status.includes(s)} onChange={() => update({ status: toggle(params.status, s) })} /> {s}</label>)}</fieldset>
            <label className="fsel"><span>Assignee (now)</span>
              <select value={params.unassigned ? 'none' : params.assignee} onChange={(e) => update({ assignee: e.target.value === 'none' ? '' : e.target.value, unassigned: e.target.value === 'none' })}>
                <option value="">Anyone</option><option value="none">Unassigned</option>
                {directory.members.filter((m) => m.role !== 'Viewer').map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
              </select>
            </label>
            {filtersActive > 0 && <button type="button" className="ghost" onClick={() => update({ priority: [], status: [], assignee: '', unassigned: false })}>Clear filters</button>}
          </div>
        </details>
        <details className="report-filter-menu">
          <summary>Saved{saved.length ? ` · ${saved.length}` : ''}</summary>
          <div className="report-filter-panel">
            {saved.length === 0 && <p className="muted small">Save the current period and filters to reuse them. Saved views stay in this browser.</p>}
            {saved.map((s) => (
              <div key={s.id} className="saved-row">
                <button type="button" className="ghost" onClick={() => setSp(writeParams(sp, s.params), { replace: true })}>{s.name}</button>
                <button type="button" className="ghost" aria-label={`Delete saved view ${s.name}`} onClick={() => setStore(writeStore((st) => ({ ...st, saved: st.saved.filter((x) => x.id !== s.id) })))}>✕</button>
              </div>
            ))}
            <form onSubmit={(e) => { e.preventDefault(); if (saveName.trim()) { setStore(writeStore((s) => ({ ...s, saved: [...s.saved, { id: crypto.randomUUID(), report: key, name: saveName.trim(), params }] }))); setSaveName('') } }}>
              <input aria-label="Name for this saved view" placeholder="Name this view…" value={saveName} onChange={(e) => setSaveName(e.target.value)} maxLength={60} />
              <button type="submit" disabled={!saveName.trim()}>Save view</button>
            </form>
          </div>
        </details>
      </div>
      <p className="muted small report-period" role="status">
        {q.data ? `${q.data.period.from} to ${q.data.period.to}, by ${q.data.period.group}, ${q.data.period.timeZone} days. Updated ${new Date(q.data.period.generatedAt).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}.` : ' '} {exported}
      </p>
      {q.isPending && <Loading label="Loading report" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && <ReportView report={q.data} onDrill={setDrill} onExportTable={(t) => exportRows(t)} />}
      {drill && (
        <section className="drill-panel" aria-labelledby="drill-h">
          <header>
            <h3 id="drill-h">Tickets behind “{drill.title}”</h3>
            <button type="button" className="ghost" onClick={() => setDrill(null)}>Close</button>
          </header>
          {behind.isPending && <Loading label="Loading tickets" />}
          {behind.isError && <ErrorNotice error={behind.error} onRetry={() => void behind.refetch()} />}
          {behind.data && (behind.data.items.length === 0 ? <p className="empty small">No tickets.</p> : (
            <>
              <p className="muted small">{behind.data.total} {behind.data.total === 1 ? 'ticket' : 'tickets'}{behind.data.truncated ? ', showing the 200 most recent' : ''}.</p>
              <div className="table-scroll"><table>
                <thead><tr><th scope="col">Ticket</th><th scope="col">Subject</th><th scope="col">Status</th><th scope="col">Priority</th><th scope="col">Assignee</th><th scope="col" className="num">Age (days)</th></tr></thead>
                <tbody>{behind.data.items.map((t) => <tr key={t.id}><td><Link to={`/tickets/${encodeURIComponent(t.reference)}`}>{t.reference}</Link></td><td>{t.subject}</td><td>{t.status}</td><td>{t.priority}</td><td>{t.assignee ?? <span className="muted">Unassigned</span>}</td><td className="num">{t.ageDays}</td></tr>)}</tbody>
              </table></div>
            </>
          ))}
        </section>
      )}
    </div>
  )
}
