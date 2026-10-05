/** In-memory composer drafts per ticket id: survive conflicts, reloads of ticket data and switching tickets. */
const drafts = new Map<string, string>()

export const getDraft = (ticketId: string) => drafts.get(ticketId) ?? ''
export function setDraft(ticketId: string, text: string) {
  if (text) drafts.set(ticketId, text)
  else drafts.delete(ticketId)
}
export const clearDrafts = () => drafts.clear()
