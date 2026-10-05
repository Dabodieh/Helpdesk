/** Compact relative age: "now", "5m", "3h", "2d", "6w", then the date. */
export function timeAgo(iso: string, now = Date.now()): string {
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return ''
  const s = Math.max(0, Math.round((now - t) / 1000))
  if (s < 60) return 'now'
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h`
  const d = Math.floor(h / 24)
  if (d < 14) return `${d}d`
  if (d < 90) return `${Math.floor(d / 7)}w`
  return new Date(t).toLocaleDateString()
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
}
