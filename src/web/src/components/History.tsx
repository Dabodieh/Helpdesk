import { useInfiniteQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { listActivity } from '../api/tickets'
import type { Activity, TicketDetail } from '../api/ticketSchemas'
import { useDirectory } from '../hooks/useDirectory'
import { actorName, describeActivity, isMessageEvent } from '../lib/activity'
import { formatDateTime } from '../lib/format'
import { livePolling } from '../queries'
import { ErrorNotice, Loading } from './common'

/** Activity timeline, newest first, as sentences. Message events are left out: the conversation shows them. */
export default function History({ ticket }: { ticket: TicketDetail }) {
  const dir = useDirectory(ticket.departmentId)
  const q = useInfiniteQuery({
    queryKey: ['ticket', ticket.id, 'activity'],
    queryFn: ({ pageParam }) => listActivity(ticket.id, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    ...livePolling,
  })

  const names = useMemo(() => {
    const n = { users: new Map(dir.names.users) }
    if (ticket.assignee) n.users.set(ticket.assignee.id, ticket.assignee.displayName)
    return n
  }, [dir.names, ticket.assignee])

  const events = useMemo(() => {
    const seen = new Map<string, Activity>()
    for (const page of q.data?.pages ?? []) for (const a of page.items) if (!seen.has(a.id)) seen.set(a.id, a)
    return [...seen.values()].filter((a) => !isMessageEvent(a))
  }, [q.data])

  return (
    <div className="conv-scroll history" tabIndex={0}>
      {q.isPending && <Loading label="Loading history" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && events.length === 0 && <p className="empty">No history to show.</p>}
      {events.length > 0 && (
        <ol className="timeline">
          {events.map((a) => (
            <li key={a.id}>
              <span>
                <strong>{actorName(a)}</strong> {describeActivity(a, names)}
              </span>
              <time className="muted small" dateTime={a.occurredAt}>
                {formatDateTime(a.occurredAt)}
              </time>
            </li>
          ))}
        </ol>
      )}
      {q.hasNextPage && (
        <div className="more">
          <button type="button" disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
            {q.isFetchingNextPage ? 'Loading…' : 'Load older history'}
          </button>
        </div>
      )}
    </div>
  )
}
