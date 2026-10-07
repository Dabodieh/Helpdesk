import { ApiError } from './http'

/** Structured problem codes (docs/api/PHASE2_API.md). Clients branch on `code`, never on message text. */
export const isVersionConflict = (e: unknown) => e instanceof ApiError && e.code === 'version_conflict'
export const isOutcomeUncertain = (e: unknown) => e instanceof ApiError && e.code === 'outcome_uncertain'

/** Human-readable message for an API failure. 404 deliberately does not say which of "missing" or "no access" applies. */
export function describeError(e: unknown): string {
  if (!(e instanceof ApiError)) return 'Something went wrong. Please try again.'
  switch (e.code) {
    case 'version_conflict':
      return 'This item was changed by someone else. Reload the latest version and try again.'
    case 'csrf_invalid':
      return 'Your security token expired. Please try again.'
    case 'duplicate':
      return e.detail ?? 'An item with these details already exists.'
    case 'last_admin':
      return e.detail ?? 'This would remove the last administrator, which is not allowed.'
    case 'inactive':
      return e.detail ?? 'This cannot be done because a related item is inactive.'
    case 'invalid_state':
      return e.detail ?? 'This is not allowed in the current state.'
  }
  switch (e.status) {
    case 0:
      return e.detail ?? e.title
    case 400: {
      const lines = e.errors
        ? Object.entries(e.errors).flatMap(([field, msgs]) => msgs.map((m) => (field ? `${field}: ${m}` : m)))
        : []
      return lines.length ? lines.join(' ') : (e.detail ?? e.title)
    }
    case 401:
      return 'Your session has ended. Please sign in again.'
    case 403:
      return 'You do not have permission to do this.'
    case 404:
      return 'Not found, or you do not have access to it.'
    default:
      return e.detail ?? (e.status >= 500 ? 'The server had a problem. Please try again.' : e.title)
  }
}

/** Field-level messages from a 400 `errors` map (keys lower-cased for lookup). */
export function fieldErrors(e: unknown): Record<string, string> {
  const out: Record<string, string> = {}
  if (e instanceof ApiError && e.status === 400 && e.errors) {
    for (const [k, v] of Object.entries(e.errors)) out[k.toLowerCase()] = v.join(' ')
  }
  return out
}
