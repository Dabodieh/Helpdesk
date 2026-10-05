import { QueryClient } from '@tanstack/react-query'
import { ApiError, resetCsrf } from './api/http'

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: (count, error) => !(error instanceof ApiError && error.status < 500 && error.status > 0) && count < 1,
        refetchOnWindowFocus: false,
      },
    },
  })
}

/** Session ended (sign-out or 401): drop cached data except the (now anonymous) user, and forget the CSRF token. */
export function endSession(qc: QueryClient) {
  resetCsrf()
  qc.removeQueries({ predicate: (q) => q.queryKey[0] !== 'me' })
  qc.setQueryData(['me'], null)
}
