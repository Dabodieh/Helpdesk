import { useInfiniteQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef } from 'react'
import { listMessages } from '../api/tickets'
import type { Message } from '../api/ticketSchemas'
import { formatDateTime } from '../lib/format'
import { livePolling } from '../queries'
import { ErrorNotice, Loading } from './common'
import { Icon } from './icons'

export const NOT_EMAILED = 'Recorded, not emailed (email delivery is not enabled yet)'

/** One conversation entry. Public vs internal differ by label, icon, border style and background, never colour alone. */
export function MessageItem({ message: m }: { message: Message }) {
  const internal = m.kind === 'Internal'
  const label = internal ? 'Internal note' : m.authorType === 'Requester' ? 'Requester message' : 'Public reply'
  return (
    <article
      className={`msg ${internal ? 'msg-internal' : 'msg-public'} author-${m.authorType.toLowerCase()}`}
      aria-label={`${label} from ${m.author.displayName}`}
    >
      <header className="msg-head">
        <span className="kind">
          <Icon name={internal ? 'lock' : 'reply'} />
          {label}
        </span>
        <span className="author">{m.author.displayName}</span>
        <span className="muted small">{m.authorType}</span>
        <time className="muted small" dateTime={m.createdAt}>
          {formatDateTime(m.createdAt)}
        </time>
      </header>
      {internal && <p className="msg-note small">Not visible to the requester</p>}
      {/* Plain text only: React escapes it and CSS preserves the line breaks. */}
      <p className="msg-body">{m.body}</p>
      {!internal && m.authorType === 'Agent' && m.deliveryState === 'NotDelivered' && (
        <p className="delivery small">
          <Icon name="nomail" /> {NOT_EMAILED}
        </p>
      )}
    </article>
  )
}

export default function Conversation({ ticketId }: { ticketId: string }) {
  const q = useInfiniteQuery({
    queryKey: ['ticket', ticketId, 'messages'],
    queryFn: ({ pageParam }) => listMessages(ticketId, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    ...livePolling,
  })

  // Pages arrive newest first; the conversation reads oldest first.
  const messages = useMemo(() => {
    const seen = new Map<string, Message>()
    for (const page of q.data?.pages ?? []) for (const m of page.items) if (!seen.has(m.id)) seen.set(m.id, m)
    return [...seen.values()].reverse()
  }, [q.data])

  const scroller = useRef<HTMLDivElement>(null)
  const newest = messages[messages.length - 1]?.id
  useEffect(() => {
    const el = scroller.current
    if (el) el.scrollTop = el.scrollHeight
  }, [newest, ticketId])

  return (
    <div className="conv-scroll" ref={scroller} tabIndex={-1}>
      {q.isPending && <Loading label="Loading conversation" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.hasNextPage && (
        <div className="more">
          <button type="button" disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
            {q.isFetchingNextPage ? 'Loading…' : 'Load earlier messages'}
          </button>
        </div>
      )}
      {q.isSuccess && messages.length === 0 && <p className="empty">No messages yet.</p>}
      {messages.map((m) => (
        <MessageItem key={m.id} message={m} />
      ))}
    </div>
  )
}
