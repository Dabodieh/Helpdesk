import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState, type FormEvent, type RefObject } from 'react'
import { Link } from 'react-router-dom'
import { describeError } from '../api/errors'
import {
  addLink,
  LINK_TYPE_CHOICE,
  LINK_TYPES,
  listLinks,
  mergeTickets,
  RELATIONSHIP_LABEL,
  removeLink,
  searchCandidates,
  type LinkCandidate,
  type LinkType,
} from '../api/merge'
import type { TicketDetail } from '../api/ticketSchemas'
import type { ComposerHandle } from './Composer'
import MacroRunner from './MacroRunner'
import { EditDetailsDialog } from './TicketProperties'
import { useApplyTicket } from '../hooks/useApplyTicket'
import { livePolling } from '../queries'
import { ErrorNotice, Loading } from './common'
import Dialog from './Dialog'

export type TicketDialogName = 'merge' | 'link' | 'macro' | 'details'

/** "More" actions of the ticket: secondary actions kept out of the header. Only offered to agents who can update the ticket. */
export function MoreMenu({ onOpen }: { onOpen: (dialog: TicketDialogName) => void }) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panelId = useId()

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false)
        trigger.current?.focus()
      }
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const item = (dialog: TicketDialogName, label: string) => (
    <button
      type="button"
      onClick={() => {
        setOpen(false)
        onOpen(dialog)
      }}
    >
      {label}
    </button>
  )

  return (
    <div className="more-menu" ref={root}>
      <button ref={trigger} type="button" aria-haspopup="true" aria-expanded={open} aria-controls={open ? panelId : undefined} onClick={() => setOpen((v) => !v)}>
        More
      </button>
      {open && (
        <div id={panelId} className="popover more-popover" role="group" aria-label="More ticket actions">
          {item('macro', 'Run macro')}
          {item('merge', 'Merge ticket')}
          {item('link', 'Link ticket')}
          {item('details', 'Edit details')}
        </div>
      )}
    </div>
  )
}

/** The dialogs the header actions, the More menu and the properties panel open. One at a time, owned by the ticket pane. */
export function TicketDialogs({
  dialog,
  ticket,
  routeRef,
  composerRef,
  onClose,
}: {
  dialog: TicketDialogName | null
  ticket: TicketDetail
  routeRef: string
  composerRef: RefObject<ComposerHandle | null>
  onClose: () => void
}) {
  switch (dialog) {
    case 'macro':
      return <MacroRunner ticket={ticket} routeRef={routeRef} composerRef={composerRef} onClose={onClose} />
    case 'merge':
      return <MergeDialog ticket={ticket} routeRef={routeRef} onClose={onClose} />
    case 'link':
      return <LinkDialog ticket={ticket} onClose={onClose} />
    case 'details':
      return <EditDetailsDialog reference={ticket.reference} onClose={onClose} />
    default:
      return null
  }
}

/** Debounced server-side search; at least two characters. Results never include inaccessible, merged or the same ticket. */
function CandidateSearch({
  ticketId,
  selected,
  multiple,
  onToggle,
}: {
  ticketId: string
  selected: LinkCandidate[]
  multiple: boolean
  onToggle: (c: LinkCandidate) => void
}) {
  const [text, setText] = useState('')
  const [term, setTerm] = useState('')
  const inputId = useId()
  useEffect(() => {
    const h = setTimeout(() => setTerm(text.trim()), 250)
    return () => clearTimeout(h)
  }, [text])
  const results = useQuery({
    queryKey: ['ticket', ticketId, 'candidates', term],
    queryFn: () => searchCandidates(ticketId, term),
    enabled: term.length >= 2,
  })
  const isSelected = (c: LinkCandidate) => selected.some((s) => s.id === c.id)
  return (
    <div className="candidate-search">
      <label htmlFor={inputId}>Find a ticket by reference or subject</label>
      <input id={inputId} data-autofocus type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder="e.g. IT-1047 or VPN" autoComplete="off" />
      {term.length > 0 && term.length < 2 && <p className="muted small">Type at least two characters.</p>}
      {results.isFetching && <Loading label="Searching tickets" />}
      {results.isError && <ErrorNotice error={results.error} />}
      {results.isSuccess && results.data.length === 0 && <p className="empty small">No matching tickets in this department.</p>}
      {results.isSuccess && results.data.length > 0 && (
        <ul className="candidate-list" aria-label="Matching tickets">
          {results.data.map((c) => (
            <li key={c.id}>
              <label>
                <input
                  type={multiple ? 'checkbox' : 'radio'}
                  name="candidate"
                  checked={isSelected(c)}
                  onChange={() => onToggle(c)}
                  aria-label={`${c.reference} ${c.subject}`}
                />
                <span className="ref">{c.reference}</span> <span>{c.subject}</span>
                <span className="muted small">
                  {' '}
                  · {c.requesterName} · {c.status}
                </span>
              </label>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

type Direction = 'into-other' | 'keep-this'

/**
 * Merge flow. Either this ticket goes into another one (the usual "this is a duplicate" case) or other tickets go into this one.
 * The ticket that stays is always spelled out in the preview before anything changes.
 */
function MergeDialog({ ticket: t, routeRef, onClose }: { ticket: TicketDetail; routeRef: string; onClose: () => void }) {
  const qc = useQueryClient()
  const apply = useApplyTicket(routeRef)
  const [direction, setDirection] = useState<Direction>('into-other')
  const [picked, setPicked] = useState<LinkCandidate[]>([])

  const toggle = (c: LinkCandidate) =>
    setPicked((cur) => (cur.some((x) => x.id === c.id) ? cur.filter((x) => x.id !== c.id) : direction === 'into-other' ? [c] : [...cur, c]))

  const merge = useMutation({
    mutationFn: () =>
      direction === 'keep-this'
        ? mergeTickets(t.id, { version: t.version, sources: picked.map((c) => ({ ticketId: c.id, version: c.version })) })
        : mergeTickets(picked[0].id, { version: picked[0].version, sources: [{ ticketId: t.id, version: t.version }] }),
    onSuccess: async (res) => {
      if (direction === 'keep-this') apply(res)
      await qc.invalidateQueries({ queryKey: ['ticket'] })
      await qc.invalidateQueries({ queryKey: ['tickets'] })
      onClose()
    },
  })

  const keep = direction === 'keep-this' ? { reference: t.reference, subject: t.subject } : picked[0]
  const merged = direction === 'keep-this' ? picked.map((c) => ({ reference: c.reference, subject: c.subject })) : [{ reference: t.reference, subject: t.subject }]

  // Requesters of the tickets that go away who are not the requester of the ticket that stays (compared by address, case-insensitively).
  const keepRequester = (direction === 'keep-this' ? t.requester.email : picked[0]?.requesterEmail ?? '').toLowerCase()
  const mergedRequesters = direction === 'keep-this' ? picked.map((c) => ({ name: c.requesterName, email: c.requesterEmail })) : [{ name: t.requester.displayName, email: t.requester.email }]
  const otherRequesters = [...new Map(mergedRequesters.filter((r) => r.email.toLowerCase() !== keepRequester).map((r) => [r.email.toLowerCase(), `${r.name} <${r.email}>`])).values()]

  function submit(e: FormEvent) {
    e.preventDefault()
    if (picked.length > 0) merge.mutate()
  }

  return (
    <Dialog title="Merge ticket" onClose={onClose}>
      <form className="dialog-body merge-form" onSubmit={submit}>
        <fieldset>
          <legend>Which ticket stays?</legend>
          <label className="inline">
            <input
              type="radio"
              name="direction"
              checked={direction === 'into-other'}
              onChange={() => {
                setDirection('into-other')
                setPicked([])
              }}
            />{' '}
            Merge {t.reference} into another ticket
          </label>
          <label className="inline">
            <input
              type="radio"
              name="direction"
              checked={direction === 'keep-this'}
              onChange={() => {
                setDirection('keep-this')
                setPicked([])
              }}
            />{' '}
            Keep {t.reference} and merge other tickets into it
          </label>
        </fieldset>
        <CandidateSearch ticketId={t.id} selected={picked} multiple={direction === 'keep-this'} onToggle={toggle} />
        {picked.length > 0 && keep && (
          <section className="merge-preview" aria-label="Merge preview">
            <p>
              <strong>Keep:</strong>
              <br />
              <span className="ref">{keep.reference}</span> — {keep.subject}
            </p>
            <p>
              <strong>Merge into it:</strong>
            </p>
            <ul>
              {merged.map((m) => (
                <li key={m.reference}>
                  <span className="ref">{m.reference}</span> — {m.subject}
                </li>
              ))}
            </ul>
            {otherRequesters.length > 0 && (
              <p role="note" className="notice info">
                <strong>Another requester will join the conversation.</strong>{' '}
                {otherRequesters.join(', ')} will be added to {keep.reference} as a protected participant: they will be copied on every future public reply and can see the request in
                the portal, and they cannot be removed from it.
              </p>
            )}
            <p>This action will:</p>
            <ul>
              <li>preserve conversations and attachments</li>
              <li>make {merged.map((m) => m.reference).join(' and ')} read-only</li>
              <li>route future strongly correlated replies to {keep.reference}</li>
            </ul>
          </section>
        )}
        {merge.isError && (
          <p role="alert" className="notice error">
            {describeError(merge.error)}
          </p>
        )}
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={picked.length === 0 || merge.isPending}>
            {merge.isPending ? 'Merging…' : 'Merge tickets'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}

function LinkDialog({ ticket: t, onClose }: { ticket: TicketDetail; onClose: () => void }) {
  const qc = useQueryClient()
  const [picked, setPicked] = useState<LinkCandidate[]>([])
  const [type, setType] = useState<LinkType>('Related')
  const add = useMutation({
    mutationFn: () => addLink(t.id, { ticketId: picked[0].id, type }),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['ticket', t.id, 'links'] })
      await qc.invalidateQueries({ queryKey: ['ticket', t.id, 'activity'] })
      onClose()
    },
  })
  return (
    <Dialog title="Link ticket" onClose={onClose}>
      <form
        className="dialog-body"
        onSubmit={(e) => {
          e.preventDefault()
          if (picked.length > 0) add.mutate()
        }}
      >
        <CandidateSearch ticketId={t.id} selected={picked} multiple={false} onToggle={(c) => setPicked([c])} />
        <label>
          The selected ticket is
          <select value={type} onChange={(e) => setType(e.target.value as LinkType)}>
            {LINK_TYPES.map((x) => (
              <option key={x} value={x}>
                {LINK_TYPE_CHOICE[x]}
              </option>
            ))}
          </select>
        </label>
        <p className="muted small">Linked tickets stay independent: nothing about either ticket changes.</p>
        {add.isError && (
          <p role="alert" className="notice error">
            {describeError(add.error)}
          </p>
        )}
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={picked.length === 0 || add.isPending}>
            {add.isPending ? 'Saving…' : 'Link ticket'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Informational relationships of the ticket. Clicking one opens that ticket in the normal workspace. */
export function RelatedTickets({ ticket: t, canEdit, onLink }: { ticket: TicketDetail; canEdit: boolean; onLink?: () => void }) {
  const qc = useQueryClient()
  const links = useQuery({ queryKey: ['ticket', t.id, 'links'], queryFn: () => listLinks(t.id), ...livePolling })
  const remove = useMutation({
    mutationFn: (id: string) => removeLink(t.id, id),
    onSettled: () => qc.invalidateQueries({ queryKey: ['ticket', t.id] }),
  })
  if (links.isPending) return <div className="skeleton" aria-hidden="true" />
  if (links.isSuccess && links.data.length === 0 && !(canEdit && onLink)) return null
  return (
    <section className="related" aria-label="Related tickets">
      <h3>Related tickets</h3>
      {links.isSuccess && links.data.length === 0 && <p className="muted small">No linked tickets.</p>}
      {links.isError && <ErrorNotice error={links.error} onRetry={() => void links.refetch()} />}
      <ul>
        {links.data?.map((l) => (
          <li key={l.id}>
            <span className="rel-type">{RELATIONSHIP_LABEL[l.relationship] ?? l.relationship}</span>
            <Link to={`/tickets/${encodeURIComponent(l.reference)}`}>
              <span className="ref">{l.reference}</span> — {l.subject}
            </Link>
            <span className="muted small">{l.mergedInto ? `Merged into ${l.mergedInto}` : l.status}</span>
            {canEdit && (
              <button type="button" className="ghost" disabled={remove.isPending} onClick={() => remove.mutate(l.id)} aria-label={`Remove link to ${l.reference}`}>
                Remove
              </button>
            )}
          </li>
        ))}
      </ul>
      {canEdit && onLink && (
        <button type="button" className="ghost" onClick={onLink}>
          + Link ticket
        </button>
      )}
    </section>
  )
}
