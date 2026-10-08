import { useInfiniteQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { listPortalRequests, REQUEST_FILTERS, type RequestFilter } from '../../api/portal'
import { ErrorNotice, Loading } from '../../components/common'
import { pollInterval } from '../../lib/realtime'
import { RequestCard } from './parts'

const LABEL: Record<RequestFilter, string> = { active: 'Open', closed: 'Resolved and closed', all: 'All' }

/** My requests: only the caller's own, newest public activity first, server-side paged. */
export default function PortalRequests() {
  const [params, setParams] = useSearchParams()
  const filter = REQUEST_FILTERS.find((f) => f === params.get('filter')) ?? 'active'
  const q = useInfiniteQuery({
    queryKey: ['portal', 'requests', filter],
    queryFn: ({ pageParam }) => listPortalRequests(filter, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    refetchInterval: () => pollInterval(),
  })
  const items = q.data?.pages.flatMap((p) => p.items) ?? []
  return (
    <>
      <div className="section-head">
        <h1>My requests</h1>
        <Link to="/portal/submit" className="button-link primary-link">
          Submit a request
        </Link>
      </div>
      <div className="tablist pressed" role="group" aria-label="Show requests">
        {REQUEST_FILTERS.map((f) => (
          <button key={f} type="button" aria-pressed={f === filter} onClick={() => setParams(f === 'active' ? {} : { filter: f }, { replace: true })}>
            {LABEL[f]}
          </button>
        ))}
      </div>
      <div role="tabpanel" aria-label={LABEL[filter]}>
        {q.isPending && <Loading label="Loading your requests" />}
        {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
        {q.isSuccess && items.length === 0 && (
          <p className="empty">{filter === 'active' ? 'You have no open requests.' : 'Nothing to show here.'}</p>
        )}
        <ul className="card-list">
          {items.map((r) => (
            <li key={r.reference}>
              <RequestCard request={r} />
            </li>
          ))}
        </ul>
        {q.hasNextPage && (
          <p>
            <button type="button" disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
              {q.isFetchingNextPage ? 'Loading…' : 'Show more'}
            </button>
          </p>
        )}
      </div>
    </>
  )
}
