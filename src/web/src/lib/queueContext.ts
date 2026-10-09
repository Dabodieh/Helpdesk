/**
 * Where a ticket was opened from, so its page can offer "Back to queue" (same filters, sort and scroll position) and previous/next in that queue.
 * Held in this browser tab only (session storage). A direct link simply has no context and falls back to a generic queue. Nothing here grants access:
 * the ticket page still loads the ticket through the API.
 */
const KEY = 'helpdesk.queue.context'
const SCROLL = 'helpdesk.queue.scroll'

export type QueueContext = { backTo: string; refs: string[] }

export function saveQueueContext(ctx: QueueContext) {
  try {
    window.sessionStorage.setItem(KEY, JSON.stringify(ctx))
  } catch {
    /* private window: no previous/next */
  }
}

export function readQueueContext(): QueueContext | null {
  try {
    const raw = JSON.parse(window.sessionStorage.getItem(KEY) ?? 'null') as Partial<QueueContext> | null
    return raw && typeof raw.backTo === 'string' && Array.isArray(raw.refs) ? { backTo: raw.backTo, refs: raw.refs.filter((r): r is string => typeof r === 'string') } : null
  } catch {
    return null
  }
}

export type Neighbours = { backTo: string; index: number; total: number; previous?: string; next?: string }

/** Previous/next of a ticket in the remembered list. `index` is -1 when the ticket is not in it (a direct link): then Back goes to a generic queue. */
export function neighbours(ctx: QueueContext | null, reference: string): Neighbours {
  const fallback = '/queue/all'
  if (!ctx) return { backTo: fallback, index: -1, total: 0 }
  const index = ctx.refs.findIndex((r) => r.toLowerCase() === reference.toLowerCase())
  if (index < 0) return { backTo: fallback, index: -1, total: 0 }
  return { backTo: ctx.backTo, index, total: ctx.refs.length, previous: ctx.refs[index - 1], next: ctx.refs[index + 1] }
}

export function saveScroll(backTo: string, top: number) {
  try {
    window.sessionStorage.setItem(`${SCROLL}:${backTo}`, String(Math.round(top)))
  } catch {
    /* ignore */
  }
}

export function readScroll(backTo: string): number {
  try {
    return Number(window.sessionStorage.getItem(`${SCROLL}:${backTo}`)) || 0
  } catch {
    return 0
  }
}
