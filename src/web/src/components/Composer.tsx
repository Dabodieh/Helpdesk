import { useMutation } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent, type RefObject } from 'react'
import { postMessage } from '../api/tickets'
import { canTicket } from '../api/permissions'
import { STATUSES, type TicketDetail } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useApplyTicket } from '../hooks/useApplyTicket'
import { getDraft, setDraft } from '../lib/drafts'
import { Icon } from './icons'
import ProblemBanner from './ProblemBanner'

export type ComposerMode = 'Public' | 'Internal'
export type ComposerHandle = { focus: (mode: ComposerMode) => void }

/**
 * Reply box with an explicit Public reply / Internal note toggle. The mode is stated in words, with an icon,
 * a banner and a differently styled box: never by colour alone. Plain text only. The draft is kept in memory
 * per ticket so conflicts, reloads and switching tickets never lose it.
 */
export default function Composer({
  ticket,
  routeRef,
  handleRef,
  onReload,
}: {
  ticket: TicketDetail
  routeRef: string
  handleRef: RefObject<ComposerHandle | null>
  onReload: () => void
}) {
  const me = useMe()
  const apply = useApplyTicket(routeRef)
  const canReply = canTicket(me, 'tickets.reply', ticket.departmentId)
  const canNote = canTicket(me, 'tickets.note', ticket.departmentId)
  const canStatus = canTicket(me, 'tickets.update', ticket.departmentId)
  const closed = ticket.status === 'Closed'
  const publicBlocked = !canReply || closed

  const [mode, setMode] = useState<ComposerMode>(canReply && !closed ? 'Public' : 'Internal')
  const [text, setText] = useState(() => getDraft(ticket.id))
  const [statusAfter, setStatusAfter] = useState('')
  const area = useRef<HTMLTextAreaElement>(null)

  // A closed ticket cannot take public replies: fall back to a note.
  const effectiveMode: ComposerMode = mode === 'Public' && publicBlocked && canNote ? 'Internal' : mode

  useEffect(() => {
    handleRef.current = {
      focus: (m) => {
        if ((m === 'Public' && publicBlocked) || (m === 'Internal' && !canNote)) return
        setMode(m)
        area.current?.focus()
      },
    }
    return () => {
      handleRef.current = null
    }
  }, [handleRef, publicBlocked, canNote])

  const send = useMutation({
    mutationFn: () =>
      postMessage(ticket.id, {
        kind: effectiveMode,
        body: text.trim(),
        status: statusAfter && statusAfter !== ticket.status ? statusAfter : undefined,
      }),
    onSuccess: (res) => {
      setDraft(ticket.id, '')
      setText('')
      setStatusAfter('')
      apply(res.ticket)
    },
  })

  if (!canReply && !canNote) {
    return (
      <div className="composer readonly">
        <p className="muted small">You can read this ticket but not reply to it.</p>
      </div>
    )
  }

  const internal = effectiveMode === 'Internal'
  const nothing = text.trim().length === 0

  function submit(e?: FormEvent) {
    e?.preventDefault()
    if (nothing || send.isPending) return
    send.mutate()
  }

  return (
    <form className={`composer ${internal ? 'composer-internal' : 'composer-public'}`} onSubmit={submit} aria-label="Reply">
      <div className="composer-top">
        <div className="seg" role="radiogroup" aria-label="Message type">
          <label className={effectiveMode === 'Public' ? 'on' : undefined}>
            <input
              type="radio"
              name={`mode-${ticket.id}`}
              checked={effectiveMode === 'Public'}
              disabled={publicBlocked}
              onChange={() => setMode('Public')}
            />
            <Icon name="reply" /> Public reply
          </label>
          <label className={effectiveMode === 'Internal' ? 'on' : undefined}>
            <input
              type="radio"
              name={`mode-${ticket.id}`}
              checked={effectiveMode === 'Internal'}
              disabled={!canNote}
              onChange={() => setMode('Internal')}
            />
            <Icon name="lock" /> Internal note
          </label>
        </div>
        {closed && canReply && <span className="muted small">Public replies need the ticket reopened first.</span>}
      </div>
      {internal ? (
        <p className="mode-banner internal-banner" role="note">
          <Icon name="lock" /> Internal note — not visible to the requester
        </p>
      ) : (
        <p className="mode-banner public-banner" role="note">
          <Icon name="reply" /> Public reply — the requester is the audience. It is emailed from the department mailbox; with no active mailbox it is only recorded.
        </p>
      )}
      <textarea
        ref={area}
        aria-label={internal ? 'Internal note' : 'Public reply'}
        rows={4}
        value={text}
        placeholder={internal ? 'Write an internal note…' : 'Write a reply…'}
        onChange={(e) => {
          setText(e.target.value)
          setDraft(ticket.id, e.target.value)
        }}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.preventDefault()
            e.currentTarget.blur()
            document.getElementById('queue-list')?.focus()
          } else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
            submit()
          }
        }}
      />
      <ProblemBanner error={send.error} onReload={onReload} onDismiss={() => send.reset()} />
      <div className="composer-actions">
        {canStatus && (
          <label className="fsel inline">
            <span>Set status after</span>
            <select value={statusAfter} onChange={(e) => setStatusAfter(e.target.value)}>
              <option value="">Keep ({ticket.status})</option>
              {STATUSES.filter((s) => s !== ticket.status).map((s) => (
                <option key={s}>{s}</option>
              ))}
            </select>
          </label>
        )}
        <span className="grow" />
        <button type="submit" className="primary" disabled={nothing || send.isPending}>
          {send.isPending ? 'Posting…' : internal ? 'Add internal note' : 'Post public reply'}
        </button>
      </div>
    </form>
  )
}
