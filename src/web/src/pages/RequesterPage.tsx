import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { getRequester, HISTORY_STATES, listRequesterTickets, type HistoryState } from '../api/requesters'
import { ErrorNotice, Loading, PageHeader } from '../components/common'
import { HistoryRow } from '../components/RequesterDrawer'
import { formatDateTime } from '../lib/format'

const LABEL: Record<HistoryState, string> = { active: 'Open', resolved: 'Resolved and closed', all: 'All' }

/** Full support history of one requester: the tickets this agent may read, newest visible activity first, server-side paged. */
export default function RequesterPage() {
  const { requesterId = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const state = HISTORY_STATES.find((s) => s === params.get('state')) ?? 'all'
  const profile = useQuery({ queryKey: ['requester', requesterId], queryFn: () => getRequester(requesterId) })
  const list = useInfiniteQuery({
    queryKey: ['requester', requesterId, 'tickets', state],
    queryFn: ({ pageParam }) => listRequesterTickets(requesterId, state, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
  })
  const items = list.data?.pages.flatMap((p) => p.items) ?? []
  const r = profile.data

  return (
    <main className="page requester-page" aria-label="Requester history">
      {profile.isError ? (
        <ErrorNotice error={profile.error} onRetry={() => void profile.refetch()} />
      ) : (
        <PageHeader
          title={r?.displayName ?? 'Requester'}
          subtitle={r ? `${r.email} · ${r.openCount} open · ${r.resolvedCount} resolved or closed · ${r.totalCount} total` : undefined}
          crumbs={<Link to="/">Back to queue</Link>}
        />
      )}
      {r && (
        <p className="muted small">
          First seen {formatDateTime(r.firstSeenAt)}
          {r.lastActivityAt ? ` · Last activity ${formatDateTime(r.lastActivityAt)}` : ''}
        </p>
      )}
      <div className="tablist" role="tablist" aria-label="Show tickets">
        {HISTORY_STATES.map((s) => (
          <button key={s} type="button" role="tab" aria-selected={s === state} onClick={() => setParams(s === 'all' ? {} : { state: s }, { replace: true })}>
            {LABEL[s]}
          </button>
        ))}
      </div>
      <div role="tabpanel" aria-label={LABEL[state]}>
        {list.isPending && !profile.isError && <Loading label="Loading history" />}
        {list.isError && !profile.isError && <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />}
        {list.isSuccess && items.length === 0 && <p className="muted">No tickets to show.</p>}
        <ul className="history-list">
          {items.map((t) => (
            <HistoryRow key={t.id} ticket={t} />
          ))}
        </ul>
        {list.hasNextPage && (
          <button type="button" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage}>
            {list.isFetchingNextPage ? 'Loading…' : 'Show more'}
          </button>
        )}
      </div>
    </main>
  )
}
