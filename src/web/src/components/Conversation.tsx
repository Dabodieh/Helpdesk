import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { canTicket } from '../api/permissions'
import { attachmentHref, listMessages, retryDelivery } from '../api/tickets'
import type { Attachment, Message } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { pollInterval } from '../lib/realtime'
import { formatDateTime } from '../lib/format'
import { livePolling, PENDING_POLL_MS } from '../queries'
import { ErrorNotice, Loading } from './common'
import { Icon } from './icons'

export const NOT_EMAILED = 'Recorded, not emailed (this department has no active mailbox to send from)'
export const QUEUED = 'Queued to send by email'
export const SENT = 'Sent by email from the department mailbox'

const BLOCK_REASONS: Record<string, string> = {
  blocked_type: 'blocked file type',
  content_mismatch: 'content does not match the file type',
  too_large: 'too large',
  empty: 'empty file',
  infected: 'malware detected',
}

const formatBytes = (n: number) => (n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`)

/**
 * Sanitised email HTML is shown ONLY inside a sandboxed frame with no scripts, no forms, no remote content (CSP) and links that open
 * in a new tab. The plain-text body stays the default view.
 */
function FormattedBody({ html }: { html: string }) {
  const doc =
    `<!doctype html><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'">` +
    `<base target="_blank"><style>body{font:14px system-ui,sans-serif;margin:8px;color:#222;background:#fff;word-wrap:break-word}</style>${html}`
  return <iframe className="formatted" title="Formatted email" sandbox="allow-popups" referrerPolicy="no-referrer" srcDoc={doc} />
}

function AttachmentList({ ticketId, items }: { ticketId: string; items: Attachment[] }) {
  return (
    <ul className="attachments" aria-label="Attachments">
      {items.map((a) => (
        <li key={a.id}>
          <Icon name="attach" />
          {a.downloadable ? (
            <a href={attachmentHref(ticketId, a.id)} download>
              {a.fileName}
            </a>
          ) : (
            <span>{a.fileName}</span>
          )}
          <span className="muted small">
            {formatBytes(a.size)}
            {a.isInline ? ' · inline' : ''}
            {a.state === 'Blocked' ? ` · not available: ${BLOCK_REASONS[a.blockReason ?? ''] ?? 'blocked'}` : ''}
            {a.state === 'Stored' && !a.downloadable ? ' · awaiting malware scan' : ''}
          </span>
        </li>
      ))}
    </ul>
  )
}

function Delivery({ message: m }: { message: Message }) {
  const qc = useQueryClient()
  const me = useMe()
  const retry = useMutation({
    mutationFn: () => retryDelivery(m.ticketId, m.id),
    onSettled: () => qc.invalidateQueries({ queryKey: ['ticket'] }),
  })
  if (m.authorType !== 'Agent' || m.kind !== 'Public') return null
  const canRetry = me.memberships.some((x) => canTicket(me, 'tickets.reply', x.departmentId))
  switch (m.deliveryState) {
    case 'NotDelivered':
      return (
        <p className="delivery small">
          <Icon name="nomail" /> {NOT_EMAILED}
        </p>
      )
    case 'Pending':
      return (
        <p className="delivery small" role="status">
          <Icon name="clock" /> {QUEUED}
        </p>
      )
    case 'Sent':
      return (
        <p className="delivery small">
          <Icon name="mail" /> {SENT}
        </p>
      )
    case 'Failed':
      return (
        <p className="delivery small failed" role="alert">
          <Icon name="nomail" /> Email delivery failed{m.deliveryError ? ` (${m.deliveryError})` : ''}.{' '}
          {canRetry && (
            <button type="button" disabled={retry.isPending} onClick={() => retry.mutate()}>
              Retry sending
            </button>
          )}
        </p>
      )
    default:
      return null
  }
}

/** One conversation entry. Public vs internal differ by label, icon, border style and background, never colour alone. */
export function MessageItem({ message: m }: { message: Message }) {
  const internal = m.kind === 'Internal'
  const label = internal ? 'Internal note' : m.authorType === 'Requester' ? 'Requester message' : 'Public reply'
  const [formatted, setFormatted] = useState(false)
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
      {m.importedFrom && (
        <p className="msg-note small">
          <span className="msg-imported">↳ Imported from {m.importedFrom}</span>
        </p>
      )}
      {/* Plain text by default: React escapes it and CSS preserves the line breaks. */}
      {formatted && m.bodyHtml ? <FormattedBody html={m.bodyHtml} /> : <p className="msg-body">{m.body}</p>}
      {!internal && m.bodyHtml && (
        <button type="button" className="linklike small" aria-pressed={formatted} onClick={() => setFormatted((v) => !v)}>
          {formatted ? 'Show plain text' : 'Show formatted'}
        </button>
      )}
      {m.attachments.length > 0 && <AttachmentList ticketId={m.ticketId} items={m.attachments} />}
      <Delivery message={m} />
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
    refetchInterval: (query) =>
      query.state.data?.pages.some((p) => p.items.some((m) => m.deliveryState === 'Pending')) ? PENDING_POLL_MS : pollInterval(),
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
    <div className="conv-scroll" ref={scroller} tabIndex={0}>
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
