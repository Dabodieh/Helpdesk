/**
 * Composer drafts per ticket id. They survive conflicts, reloads of ticket data and switching tickets, and are kept in this tab's session storage so a reload does
 * not lose them either. Only the text is stored, and only until it is sent or cleared. The app warns before the tab is closed or the user signs out while any exist.
 */
const KEY = 'helpdesk.drafts'

function load(): Map<string, string> {
  try {
    const raw = JSON.parse(window.sessionStorage.getItem(KEY) ?? '{}') as Record<string, unknown>
    return new Map(Object.entries(raw).filter((e): e is [string, string] => typeof e[1] === 'string' && e[1].length > 0))
  } catch {
    return new Map()
  }
}

const drafts = load()

function persist() {
  try {
    window.sessionStorage.setItem(KEY, JSON.stringify(Object.fromEntries(drafts)))
  } catch {
    /* kept in memory only */
  }
}

export const getDraft = (ticketId: string) => drafts.get(ticketId) ?? ''
export function setDraft(ticketId: string, text: string) {
  if (text.trim()) drafts.set(ticketId, text)
  else drafts.delete(ticketId)
  persist()
}
export const hasDraft = (ticketId: string) => drafts.has(ticketId)
export const draftCount = () => drafts.size
export function clearDrafts() {
  drafts.clear()
  persist()
}
