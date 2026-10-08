import type { Report, ReportKpi, ReportTable } from '../../api/reports'
import { formatCell, formatValue } from '../../lib/analytics'
import SeriesChart from './SeriesChart'

export type DrillRequest = { metric: string; bucket?: string; title: string }

function delta(kpi: ReportKpi) {
  if (kpi.value === null || kpi.value === undefined || kpi.previous === null || kpi.previous === undefined) return null
  const diff = kpi.value - kpi.previous
  if (diff === 0) return { text: 'same as the previous period', tone: 'flat' }
  const pct = kpi.previous === 0 ? '' : ` (${diff > 0 ? '+' : ''}${Math.round((diff / kpi.previous) * 100)}%)`
  return { text: `${diff > 0 ? '+' : ''}${formatValue(diff, kpi.unit)}${pct} vs previous period`, tone: diff > 0 ? 'up' : 'down' }
}

export function KpiCards({ kpis, onDrill }: { kpis: ReportKpi[]; onDrill: (d: DrillRequest) => void }) {
  return (
    <ul className="kpi-row" aria-label="Key figures">
      {kpis.map((k) => {
        const d = delta(k)
        const body = (
          <>
            <span className="kpi-label">{k.label}</span>
            <strong className="kpi-value">{formatValue(k.value, k.unit)}</strong>
            {d && <span className={`kpi-delta ${d.tone}`}>{d.text}</span>}
            {k.note && <span className="kpi-note">{k.note}</span>}
          </>
        )
        return (
          <li key={k.key} className={`kpi tone-${k.tone}`}>
            {k.drill ? (
              <button type="button" className="kpi-button" onClick={() => onDrill({ metric: k.drill!, title: k.label })} aria-label={`${k.label}: ${formatValue(k.value, k.unit)}. Show the tickets`}>
                {body}
              </button>
            ) : (
              body
            )}
          </li>
        )
      })}
    </ul>
  )
}

export function DataTable({ table, onExport }: { table: ReportTable; onExport?: () => void }) {
  return (
    <section className="report-table" aria-labelledby={`t-${table.key}`}>
      <header>
        <h3 id={`t-${table.key}`}>{table.title}</h3>
        {onExport && table.rows.length > 0 && (
          <button type="button" className="ghost" onClick={onExport}>Export CSV</button>
        )}
      </header>
      {table.rows.length === 0 ? (
        <p className="empty small">Nothing to show for this period.</p>
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>{table.columns.map((c) => <th key={c.key} scope="col" className={c.type === 'text' || c.type === 'date' ? '' : 'num'}>{c.label}</th>)}</tr>
            </thead>
            <tbody>
              {table.rows.map((row, ri) => (
                <tr key={ri}>
                  {table.columns.map((c, ci) => (
                    <td key={c.key} className={c.type === 'text' || c.type === 'date' ? '' : 'num'}>{formatCell(row[ci], c.type)}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

export default function ReportView({ report, onDrill, onExportTable }: { report: Report; onDrill: (d: DrillRequest) => void; onExportTable: (t: ReportTable) => void }) {
  const wide = (key: string) => key.includes('trend') || key === 'volume' || key === 'flow' || key === 'activity' || key === 'delivery' || key === 'over-time' || key === 'compliance' || key === 'backlog'
  return (
    <div className="report-view">
      <KpiCards kpis={report.kpis} onDrill={onDrill} />
      <div className="chart-grid">
        {report.series.map((s) => (
          <section key={s.key} className={`chart-card${wide(s.key) ? ' wide' : ''}`} aria-labelledby={`c-${s.key}`}>
            <header>
              <h3 id={`c-${s.key}`}>{s.title}</h3>
              {s.subtitle && <p className="muted small">{s.subtitle}</p>}
            </header>
            <SeriesChart series={s} group={report.period.group} onDrill={(metric, bucket, label) => onDrill({ metric, bucket, title: label })} />
          </section>
        ))}
      </div>
      {report.tables.map((t) => <DataTable key={t.key} table={t} onExport={() => onExportTable(t)} />)}
      {(report.notes.length > 0 || report.unavailable.length > 0) && (
        <aside className="report-notes" aria-label="About these figures">
          {report.notes.map((n) => <p key={n}>{n}</p>)}
          {report.unavailable.map((n) => <p key={n} className="unavailable"><strong>Not available:</strong> {n}</p>)}
        </aside>
      )}
    </div>
  )
}
