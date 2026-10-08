import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReportSeries } from '../../api/reports'
import { formatBucket, formatValue } from '../../lib/analytics'

const TONE: Record<string, string> = {
  neutral: 'var(--text)',
  info: 'var(--accent)',
  good: 'var(--ok)',
  warn: 'var(--warn)',
  bad: 'var(--danger)',
  muted: 'var(--faint, var(--muted))',
}
const colourOf = (tone: string, index: number) => TONE[tone] ?? ['var(--accent)', 'var(--ok)', 'var(--warn)', 'var(--danger)'][index % 4]!

function useWidth() {
  const ref = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(640)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const update = () => setWidth(Math.max(280, Math.round(el.clientWidth)))
    update()
    if (typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(update)
    ro.observe(el)
    return () => ro.disconnect()
  }, [])
  return [ref, width] as const
}

function niceMax(v: number, unit: string) {
  if (unit === 'percent') return 100
  if (v <= 4) return 4
  const pow = 10 ** Math.floor(Math.log10(v))
  const n = v / pow
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow
}

type Props = {
  series: ReportSeries
  group: string
  /** Called with the line's drill metric and the bucket (date) of a clicked point. */
  onDrill?: (metric: string, bucket: string | undefined, label: string) => void
  height?: number
}

/** A single-purpose SVG chart: lines/areas, clustered or stacked columns and horizontal bars, with a legend that toggles series and a tooltip per bucket. */
export default function SeriesChart({ series, group, onDrill, height = 260 }: Props) {
  const [ref, width] = useWidth()
  const [hidden, setHidden] = useState<Set<string>>(new Set())
  const [hover, setHover] = useState<number | null>(null)
  const lines = series.lines.filter((l) => !hidden.has(l.key))
  const horizontal = series.kind === 'hbars'
  const stacked = series.kind === 'stacked' || (horizontal && series.lines.length > 1)
  const isDate = series.bucketType === 'date'
  const n = series.buckets.length

  const label = (i: number) => (isDate ? formatBucket(series.buckets[i]!, group) : series.buckets[i]!)
  const totals = useMemo(
    () => series.buckets.map((_, i) => (stacked ? lines.reduce((s, l) => s + (l.values[i] ?? 0), 0) : Math.max(0, ...lines.map((l) => l.values[i] ?? 0)))),
    [series.buckets, lines, stacked],
  )
  const yMax = niceMax(Math.max(1, ...totals), series.unit)

  const margin = horizontal ? { l: Math.min(190, Math.max(110, width * 0.28)), r: 40, t: 8, b: 22 } : { l: 46, r: 12, t: 10, b: 30 }
  const h = horizontal ? Math.max(height * 0.7, n * 34 + margin.t + margin.b) : height
  const pw = width - margin.l - margin.r
  const ph = h - margin.t - margin.b
  const empty = series.lines.every((l) => l.values.every((v) => v === null || v === 0))

  const xBand = (i: number) => margin.l + (pw / n) * i
  const yPos = (v: number) => margin.t + ph - (v / yMax) * ph
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => f * yMax)
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(2, Math.floor(pw / 70))))

  const drill = (lineKey: string, i: number) => {
    const l = series.lines.find((x) => x.key === lineKey)
    if (l?.drill && onDrill) onDrill(l.drill, isDate ? series.buckets[i] : undefined, `${l.label} · ${label(i)}`)
  }

  const shapes: React.ReactNode[] = []
  if (horizontal) {
    const rowH = ph / n
    for (let i = 0; i < n; i++) {
      let x0 = 0
      lines.forEach((l, li) => {
        const v = l.values[i] ?? 0
        if (v <= 0) return
        const w = (v / yMax) * pw
        shapes.push(
          <rect key={`${l.key}-${i}`} x={margin.l + (stacked ? x0 : 0)} y={margin.t + rowH * i + rowH * 0.18} width={Math.max(2, w)} height={rowH * 0.64} rx={3} fill={colourOf(l.tone, li)} opacity={hover === null || hover === i ? 1 : 0.55}
            onClick={() => drill(l.key, i)} style={{ cursor: l.drill ? 'pointer' : 'default' }} />,
        )
        x0 += w
      })
    }
  } else if (series.kind === 'line' || series.kind === 'area') {
    lines.forEach((l, li) => {
      const pts = l.values.map((v, i) => (v === null ? null : [xBand(i) + pw / n / 2, yPos(v)] as const))
      const segs: (readonly [number, number])[][] = []
      let cur: (readonly [number, number])[] = []
      pts.forEach((p) => {
        if (p) cur.push(p)
        else if (cur.length) { segs.push(cur); cur = [] }
      })
      if (cur.length) segs.push(cur)
      segs.forEach((seg, si) => {
        const d = seg.map((p, i) => `${i ? 'L' : 'M'}${p[0].toFixed(1)},${p[1].toFixed(1)}`).join(' ')
        if (series.kind === 'area') shapes.push(<path key={`a${l.key}${si}`} d={`${d} L${seg[seg.length - 1]![0]},${margin.t + ph} L${seg[0]![0]},${margin.t + ph} Z`} fill={colourOf(l.tone, li)} opacity={0.18} />)
        shapes.push(<path key={`l${l.key}${si}`} d={d} fill="none" stroke={colourOf(l.tone, li)} strokeWidth={2} strokeLinejoin="round" />)
        if (seg.length === 1) shapes.push(<circle key={`p${l.key}${si}`} cx={seg[0]![0]} cy={seg[0]![1]} r={3.5} fill={colourOf(l.tone, li)} />)
      })
      if (n <= 40) pts.forEach((p, i) => p && shapes.push(<circle key={`d${l.key}${i}`} cx={p[0]} cy={p[1]} r={hover === i ? 4.5 : 2.5} fill={colourOf(l.tone, li)} onClick={() => drill(l.key, i)} style={{ cursor: l.drill ? 'pointer' : 'default' }} />))
    })
  } else {
    const band = pw / n
    const inner = band * 0.72
    lines.forEach((l, li) => {
      for (let i = 0; i < n; i++) {
        const v = l.values[i] ?? 0
        if (v <= 0) continue
        let x: number
        let y: number
        let bw: number
        if (stacked) {
          const below = lines.slice(0, li).reduce((s, o) => s + (o.values[i] ?? 0), 0)
          x = xBand(i) + (band - inner) / 2
          bw = inner
          y = yPos(below + v)
          shapes.push(<rect key={`${l.key}-${i}`} x={x} y={y} width={bw} height={Math.max(1, yPos(below) - y)} fill={colourOf(l.tone, li)} opacity={hover === null || hover === i ? 1 : 0.6} onClick={() => drill(l.key, i)} style={{ cursor: l.drill ? 'pointer' : 'default' }} />)
        } else {
          bw = inner / lines.length
          x = xBand(i) + (band - inner) / 2 + bw * li
          y = yPos(v)
          shapes.push(<rect key={`${l.key}-${i}`} x={x} y={y} width={Math.max(1.5, bw - 1)} height={Math.max(1, margin.t + ph - y)} rx={2} fill={colourOf(l.tone, li)} opacity={hover === null || hover === i ? 1 : 0.6} onClick={() => drill(l.key, i)} style={{ cursor: l.drill ? 'pointer' : 'default' }} />)
        }
      }
    })
  }

  const tip = hover === null ? null : { x: horizontal ? margin.l + pw / 2 : xBand(hover) + pw / n / 2, i: hover }
  return (
    <figure className="chart" aria-label={series.title}>
      <div className="chart-legend" role="group" aria-label="Series">
        {series.lines.map((l, li) => (
          <button type="button" key={l.key} className={hidden.has(l.key) ? 'off' : ''} aria-pressed={!hidden.has(l.key)}
            onClick={() => setHidden((s) => { const c = new Set(s); if (c.has(l.key)) c.delete(l.key); else c.add(l.key); return c })}>
            <span className="swatch" style={{ background: colourOf(l.tone, li) }} />{l.label}
          </button>
        ))}
      </div>
      <div className="chart-body" ref={ref}>
        {empty ? (
          <p className="chart-empty">No data for this period.</p>
        ) : (
          <svg width={width} height={h} role="img" aria-label={`${series.title}. Data is in the table below the charts.`} onMouseLeave={() => setHover(null)}>
            {!horizontal && ticks.map((t) => (
              <g key={t}>
                <line x1={margin.l} x2={width - margin.r} y1={yPos(t)} y2={yPos(t)} className="chart-grid" />
                <text x={margin.l - 6} y={yPos(t) + 4} textAnchor="end" className="chart-axis">{formatValue(t, series.unit === 'percent' ? 'percent' : series.unit === 'count' ? 'count' : series.unit)}</text>
              </g>
            ))}
            {horizontal && [0, 0.5, 1].map((f) => (
              <g key={f}>
                <line y1={margin.t} y2={margin.t + ph} x1={margin.l + pw * f} x2={margin.l + pw * f} className="chart-grid" />
                <text x={margin.l + pw * f} y={h - 6} textAnchor={f === 1 ? 'end' : f === 0 ? 'start' : 'middle'} className="chart-axis">{formatValue(yMax * f, series.unit)}</text>
              </g>
            ))}
            {horizontal
              ? series.buckets.map((_, i) => (
                  <text key={i} x={margin.l - 8} y={margin.t + (ph / n) * i + ph / n / 2 + 4} textAnchor="end" className="chart-axis chart-cat">{label(i).length > 28 ? `${label(i).slice(0, 27)}…` : label(i)}</text>
                ))
              : series.buckets.map((_, i) => (i % labelEvery === 0 ? <text key={i} x={xBand(i) + pw / n / 2} y={h - 8} textAnchor="middle" className="chart-axis">{label(i).length > 14 ? `${label(i).slice(0, 13)}…` : label(i)}</text> : null))}
            {shapes}
            {series.buckets.map((_, i) => (
              <rect key={`hit${i}`} x={horizontal ? margin.l : xBand(i)} y={horizontal ? margin.t + (ph / n) * i : margin.t} width={horizontal ? pw : pw / n} height={horizontal ? ph / n : ph} fill="transparent" onMouseEnter={() => setHover(i)} onMouseMove={() => setHover(i)} />
            ))}
          </svg>
        )}
        {tip && !empty && (
          <div className="chart-tip" style={{ left: Math.min(width - 190, Math.max(0, tip.x - 90)), top: 4 }} role="status">
            <strong>{label(tip.i)}</strong>
            {series.lines.map((l, li) => (
              <div key={l.key}><span className="swatch" style={{ background: colourOf(l.tone, li) }} />{l.label}<b>{formatValue(l.values[tip.i] ?? null, series.unit)}</b></div>
            ))}
          </div>
        )}
      </div>
    </figure>
  )
}
