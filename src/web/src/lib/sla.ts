/** Plain-language SLA wording shared by the queue, the ticket header and the detail dialog. Never colour alone: every state has a word and a mark. */

export const SLA_LABEL: Record<string, string> = {
  Running: 'On track',
  Approaching: 'Due soon',
  Breached: 'Breached',
  Paused: 'Paused',
  Met: 'Met',
  MetLate: 'Met late',
  None: '',
}

export const SLA_MARK: Record<string, string> = {
  Running: '●',
  Approaching: '◐',
  Breached: '▲',
  Paused: 'Ⅱ',
  Met: '✓',
  MetLate: '✓',
  None: '',
}

/** "in 25m", "in 3h", "2h overdue", "in 2d", or the date for far deadlines. */
export function dueIn(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return ''
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return ''
  const diff = t - now
  const abs = Math.abs(diff)
  const m = Math.round(abs / 60000)
  let text: string
  if (m < 1) text = 'now'
  else if (m < 60) text = `${m}m`
  else if (m < 60 * 24) text = `${Math.floor(m / 60)}h`
  else if (m < 60 * 24 * 14) text = `${Math.floor(m / (60 * 24))}d`
  else return new Date(t).toLocaleDateString()
  if (text === 'now') return 'now'
  return diff >= 0 ? `in ${text}` : `${text} overdue`
}

/** Minutes as "4h", "90m", "1d 2h" for policy targets. */
export function formatMinutes(total: number | null | undefined): string {
  if (!total) return ''
  const d = Math.floor(total / 1440)
  const h = Math.floor((total % 1440) / 60)
  const m = total % 60
  return [d ? `${d}d` : '', h ? `${h}h` : '', m ? `${m}m` : ''].filter(Boolean).join(' ')
}
