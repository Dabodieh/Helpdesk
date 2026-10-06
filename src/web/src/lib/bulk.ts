import type { BulkResult } from '../api/ticketSchemas'

const OUTCOME_TEXT: Record<string, string> = {
  not_found: 'not found or not accessible',
  forbidden: 'not allowed',
  invalid: 'not valid for the ticket’s department',
}

/** Plain-language summary of a bulk result: how many changed, and why the others did not. */
export function summarise(r: BulkResult): string {
  const parts = [`${r.updated} updated`]
  if (r.unchanged) parts.push(`${r.unchanged} already up to date`)
  const reasons = new Map<string, number>()
  for (const item of r.results) if (OUTCOME_TEXT[item.outcome]) reasons.set(item.outcome, (reasons.get(item.outcome) ?? 0) + 1)
  for (const [outcome, n] of reasons) parts.push(`${n} ${OUTCOME_TEXT[outcome]}`)
  return parts.join(', ')
}
