import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { searchArticleLinks, type ArticleLink } from '../api/tickets'
import { useDebounced } from '../lib/useDebounced'
import { ErrorNotice, Loading } from './common'
import Dialog from './Dialog'
import { Icon } from './icons'

/**
 * Insert a link to a Knowledge article into a PUBLIC reply. The server offers only published articles the ticket's requester can open (and none when the requester
 * has no linked account), so what is listed is always safe to send. Picking one only inserts text; nothing is sent.
 */
export default function ArticleLinkPicker({ ticketId, onPick }: { ticketId: string; onPick: (text: string) => void }) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const term = useDebounced(text.trim(), 250)
  const q = useQuery({ queryKey: ['article-links', ticketId, term], queryFn: () => searchArticleLinks(ticketId, term), enabled: open, staleTime: 15_000, retry: false })

  function choose(a: ArticleLink) {
    onPick(`${a.title}: ${window.location.origin}${a.path}`)
    setOpen(false)
    setText('')
  }

  return (
    <>
      <button type="button" className="ghost insert-btn" onClick={() => setOpen(true)} aria-haspopup="dialog">
        <Icon name="snippet" /> Insert article link
      </button>
      {open && (
        <Dialog title="Link a Knowledge article" onClose={() => setOpen(false)}>
          <input type="search" data-autofocus aria-label="Search articles" placeholder="Search articles" value={text} onChange={(e) => setText(e.target.value)} />
          {q.isPending && <Loading label="Searching articles" />}
          {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
          {q.isSuccess && q.data.length === 0 && (
            <p className="empty small">
              {term ? `No article matches “${term}” for this requester.` : 'No article can be linked for this requester.'} Only published articles the requester can open are offered, and only when the requester has a linked account.
            </p>
          )}
          <ul className="canned-list" aria-label="Articles">
            {(q.data ?? []).map((a) => (
              <li key={a.slug}>
                <button type="button" onClick={() => choose(a)}>
                  <span className="canned-title">
                    {a.title} {a.category && <span className="badge">{a.category}</span>}
                  </span>
                  <span className="muted small">Insert link</span>
                </button>
              </li>
            ))}
          </ul>
        </Dialog>
      )}
    </>
  )
}
