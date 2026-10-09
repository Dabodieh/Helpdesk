import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent, type RefObject } from 'react'
import { getSignature } from '../api/emailIdentity'
import { attachmentHref, getTicketCapabilities, postMessage, searchMentionable, uploadInlineImage } from '../api/tickets'
import { describeError } from '../api/errors'
import { FOCUS_REPLY_EVENT } from './SilentResolveDialog'
import { canTicket } from '../api/permissions'
import { STATUSES, type TicketDetail } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useApplyTicket } from '../hooks/useApplyTicket'
import { getDraft, setDraft } from '../lib/drafts'
import { imageIdsOf, imageMarker, MAX_PASTED_IMAGES, PASTE_TYPES } from '../lib/inlineImages'
import ArticleLinkPicker from './ArticleLinkPicker'
import CannedPicker from './CannedPicker'
import CcRecipients from './CcRecipients'
import { Icon } from './icons'
import ProblemBanner from './ProblemBanner'

const NL = String.fromCharCode(10)

export type ComposerMode = 'Public' | 'Internal'
export type ComposerHandle = { focus: (mode: ComposerMode) => void; insertDraft: (text: string) => void }

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
  onComposing,
}: {
  ticket: TicketDetail
  routeRef: string
  handleRef: RefObject<ComposerHandle | null>
  onReload: () => void
  /** Reports only THAT a reply / internal note is being written (never the text), for the advisory presence line. */
  onComposing?: (state: 'reply' | 'note' | null) => void
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
  // Colleagues picked for @mention in this note (ids go to the server only for names still in the text).
  const [mentioned, setMentioned] = useState<{ userId: string; displayName: string }[]>([])
  const [caret, setCaret] = useState(0)
  const area = useRef<HTMLTextAreaElement>(null)
  // Pasted images: uploaded at once as pending (author-only) files and named in the message at send. Whether this is offered is decided by the server's attachment switch.
  const capabilities = useQuery({ queryKey: ['ticket-capabilities'], queryFn: getTicketCapabilities, staleTime: 30_000, retry: false })
  const imagesEnabled = capabilities.data?.attachmentsEnabled === true
  // The image is written into the text at the cursor as [image:ID] (so its position is kept and the draft keeps it); the thumbnails below are read from those markers.
  const previews = useRef(new Map<string, string>())
  const [uploading, setUploading] = useState(0)
  const [imageNote, setImageNote] = useState<string | null>(null)
  // Only to tell the agent what will be appended; the server resolves the real signature when the reply is sent.
  const signature = useQuery({ queryKey: ['signature', ticket.departmentId], queryFn: () => getSignature(ticket.departmentId), enabled: canReply, retry: false, staleTime: 60_000 })

  // A closed ticket cannot take public replies: fall back to a note.
  const effectiveMode: ComposerMode = mode === 'Public' && publicBlocked && canNote ? 'Internal' : mode

  // Collision hint (not a lock, not a version check: the server still validates every change): remember the ticket's visible activity stamp when writing starts and
  // warn, without touching the draft, if it moved while the agent was writing.
  const writing = text.trim().length > 0
  const [startedAt, setStartedAt] = useState<string | null>(null)
  if (writing && startedAt === null) setStartedAt(ticket.updatedAt)
  if (!writing && startedAt !== null) setStartedAt(null)
  useEffect(() => {
    onComposing?.(writing ? (effectiveMode === 'Internal' ? 'note' : 'reply') : null)
    return () => onComposing?.(null)
  }, [writing, effectiveMode, onComposing])
  const moved = effectiveMode === 'Public' && startedAt !== null && startedAt !== ticket.updatedAt

  // "@ab" just before the caret: look colleagues up (internal notes only; the server returns only people who can read internal notes here).
  const trigger = effectiveMode === 'Internal' ? /(^|\s)@([^\s@]{2,})$/.exec(text.slice(0, caret)) : null
  const query = trigger?.[2] ?? ''
  const found = useQuery({ queryKey: ['mentionable', ticket.id, query], queryFn: () => searchMentionable(ticket.id, query), enabled: query.length >= 2, staleTime: 30_000, retry: false })

  useEffect(() => {
    handleRef.current = {
      focus: (m) => {
        if ((m === 'Public' && publicBlocked) || (m === 'Internal' && !canNote)) return
        setMode(m)
        area.current?.focus()
      },
      // Macro reply draft: switches to a public reply and puts the text in the box. It is never sent from here.
      insertDraft: (draft) => {
        const next = text.trim() ? `${text}

${draft}` : draft
        if (!publicBlocked) setMode('Public')
        setText(next)
        setDraft(ticket.id, next)
        requestAnimationFrame(() => area.current?.focus())
      },
    }
    return () => {
      handleRef.current = null
    }
  }, [handleRef, publicBlocked, canNote, text, ticket.id])

  /** Insert canned text at the cursor (or replace the selection). Nothing is posted; the agent reviews and submits. */
  function insertText(snippet: string) {
    const el = area.current
    const start = el?.selectionStart ?? text.length
    const end = el?.selectionEnd ?? text.length
    const next = text.slice(0, start) + snippet + text.slice(end)
    setText(next)
    setDraft(ticket.id, next)
    const caret = start + snippet.length
    requestAnimationFrame(() => {
      area.current?.focus()
      area.current?.setSelectionRange(caret, caret)
    })
  }

  async function addImages(files: File[]) {
    if (!imagesEnabled) {
      setImageNote('Image uploads are unavailable.')
      return
    }
    setImageNote(null)
    const limit = capabilities.data?.maxInlineImageBytes ?? 0
    let count = imageIdsOf(text).length
    for (const file of files) {
      if (!PASTE_TYPES.includes(file.type)) {
        setImageNote('Only PNG, JPEG and GIF images can be added.')
        continue
      }
      if (count >= MAX_PASTED_IMAGES) {
        setImageNote(`At most ${MAX_PASTED_IMAGES} images per message.`)
        break
      }
      if (limit > 0 && file.size > limit) {
        setImageNote(`That image is larger than ${Math.floor(limit / (1024 * 1024))} MB.`)
        continue
      }
      count++
      setUploading((n) => n + 1)
      try {
        const up = await uploadInlineImage(ticket.id, file)
        if (typeof URL.createObjectURL === 'function') previews.current.set(up.id, URL.createObjectURL(file))
        insertImageMarker(up.id)
      } catch (e) {
        count--
        setImageNote(describeError(e))
      } finally {
        setUploading((n) => n - 1)
      }
    }
  }

  /** Puts the marker on its own line at the cursor, using the box's current text (several uploads can finish one after another). */
  function insertImageMarker(id: string) {
    const el = area.current
    const current = el?.value ?? ''
    const start = el?.selectionStart ?? current.length
    const end = el?.selectionEnd ?? current.length
    const before = current.slice(0, start)
    const after = current.slice(end)
    const snippet = `${before && !before.endsWith(NL) ? NL : ''}${imageMarker(id)}${after && !after.startsWith(NL) ? NL : ''}`
    const next = before + snippet + after
    setText(next)
    setDraft(ticket.id, next)
    const pos = before.length + snippet.length
    requestAnimationFrame(() => {
      area.current?.focus()
      area.current?.setSelectionRange(pos, pos)
    })
  }

  function removeImage(id: string) {
    // The marker and the line break after it go; when it sat between two lines, one break stays so they do not run together.
    const at = text.toLowerCase().indexOf(imageMarker(id).toLowerCase())
    if (at < 0) return
    let from = at
    let to = at + imageMarker(id).length
    const breakBefore = from > 0 && text[from - 1] === NL
    const breakAfter = text[to] === NL
    if (breakAfter) to++
    else if (breakBefore) from--
    const next = text.slice(0, from) + text.slice(to)
    setText(next)
    setDraft(ticket.id, next)
    const url = previews.current.get(id)
    if (url) URL.revokeObjectURL(url)
    previews.current.delete(id)
  }

  const picked = mentioned.filter((m) => text.includes(`@${m.displayName}`)).map((m) => m.userId)
  const mentionIds = picked.length > 0 ? picked : undefined
  // One id per composed message, kept across retries and double clicks so the server never posts it twice; a new one after it was accepted.
  const clientMessageId = useRef(crypto.randomUUID())
  useEffect(() => {
    const focus = () => area.current?.focus()
    window.addEventListener(FOCUS_REPLY_EVENT, focus)
    return () => window.removeEventListener(FOCUS_REPLY_EVENT, focus)
  }, [])
  const send = useMutation({
    mutationFn: () =>
      postMessage(ticket.id, {
        clientMessageId: clientMessageId.current,
        kind: effectiveMode,
        body: text.trim(),
        status: statusAfter && statusAfter !== ticket.status ? statusAfter : undefined,
        mentionUserIds: effectiveMode === 'Internal' ? mentionIds : undefined,
      }),
    onSuccess: (res) => {
      clientMessageId.current = crypto.randomUUID()
      setDraft(ticket.id, '')
      setText('')
      setMentioned([])
      for (const url of previews.current.values()) URL.revokeObjectURL(url)
      previews.current.clear()
      setImageNote(null)
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
  function pickMention(person: { userId: string; displayName: string }) {
    const before = text.slice(0, caret)
    const start = before.length - query.length - 1
    const insert = `@${person.displayName} `
    const next = text.slice(0, start) + insert + text.slice(caret)
    setText(next)
    setDraft(ticket.id, next)
    setMentioned((cur) => (cur.some((m) => m.userId === person.userId) ? cur : [...cur, person]))
    const pos = start + insert.length
    setCaret(pos)
    requestAnimationFrame(() => {
      area.current?.focus()
      area.current?.setSelectionRange(pos, pos)
    })
  }

  const nothing = text.trim().length === 0
  const pasted = imageIdsOf(text)

  function submit(e?: FormEvent) {
    e?.preventDefault()
    if (nothing || send.isPending || uploading > 0) return
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
        <span className="grow" />
        <CannedPicker departmentId={ticket.departmentId} ticketId={ticket.id} onPick={insertText} />
        {!internal && canReply && <ArticleLinkPicker ticketId={ticket.id} onPick={insertText} />}
      </div>
      {internal ? (
        <p className="mode-banner internal-banner" role="note">
          <Icon name="lock" /> Internal note — not visible to the requester
        </p>
      ) : (
        <p className="mode-banner public-banner" role="note">
          <Icon name="reply" />
          <span>
            Public reply to <span className="composer-recipient">{ticket.requester.displayName} &lt;{ticket.requester.email}&gt;</span>
            {ticket.participants.length > 0 ? ` (Cc ${ticket.participants.length})` : ''}
          </span>
        </p>
      )}
      {!internal && !ticket.mailbox && <p className="notice warn small" role="note">Email sending is unavailable: this ticket has no mailbox. Public replies are recorded but not emailed. Contact a mailbox administrator.</p>}
      {!internal && ticket.participants.length > 0 && <CcRecipients ticket={ticket} canRemove={canReply && !closed} />}
      {!internal && signature.data && (
        <p className="small muted composer-signature">
          {signature.data.html ? `Your ${signature.data.departmentName} signature is added when you send.` : `No ${signature.data.departmentName} signature set: this reply goes out without one (My profile).`}
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
          setCaret(e.target.selectionStart ?? e.target.value.length)
          setDraft(ticket.id, e.target.value)
        }}
        onSelect={(e) => setCaret(e.currentTarget.selectionStart ?? 0)}
        onPaste={(e) => {
          const files = [...e.clipboardData.files].filter((f) => f.type.startsWith('image/'))
          if (files.length === 0) return
          e.preventDefault()
          void addImages(files)
        }}
        onDragOver={(e) => {
          if (e.dataTransfer.types.includes('Files')) e.preventDefault()
        }}
        onDrop={(e) => {
          const files = [...e.dataTransfer.files]
          if (files.length === 0) return
          e.preventDefault()
          void addImages(files)
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
      {pasted.length > 0 && (
        <ul className="composer-images" aria-label="Pasted images">
          {pasted.map((id, n) => (
            <li key={id}>
              <img src={previews.current.get(id) ?? attachmentHref(ticket.id, id)} alt={`Pasted image ${n + 1}`} />
              <button type="button" className="icon-btn" aria-label={`Remove pasted image ${n + 1}`} onClick={() => removeImage(id)}>
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
      {uploading > 0 && (
        <p className="muted small" role="status">
          Adding image…
        </p>
      )}
      {imageNote && (
        <p className="notice warn small" role="status">
          {imageNote}
        </p>
      )}
      {internal && query.length >= 2 && found.data && (
        <div className="mention-picker" role="group" aria-label="Mention a colleague">
          <span className="sr-only" role="status">
            {found.data.length === 0 ? 'No colleagues found' : `${found.data.length} colleague${found.data.length === 1 ? '' : 's'} found`}
          </span>
          {found.data.length === 0 ? (
            <span className="muted small">No colleague with that name can read internal notes here.</span>
          ) : (
            found.data.map((p) => (
              <button key={p.userId} type="button" onClick={() => pickMention(p)}>
                @{p.displayName}
              </button>
            ))
          )}
        </div>
      )}
      {moved && (
        <p className="notice info small" role="status">
          This ticket has new activity since you started writing. Check the conversation before you send; your draft is kept.{' '}
          <button type="button" className="linklike" onClick={() => setStartedAt(ticket.updatedAt)}>
            Dismiss
          </button>
        </p>
      )}
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
        <span className="muted small send-hint">
          <kbd>Ctrl</kbd>+<kbd>Enter</kbd> sends
        </span>
        <button type="submit" className="primary" disabled={nothing || send.isPending || uploading > 0}>
          {send.isPending ? 'Posting…' : internal ? 'Add internal note' : 'Post public reply'}
        </button>
      </div>
    </form>
  )
}
