import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { describeError } from '../api/errors'
import { addFollower, followMe, getFollowers, removeFollower, searchFollowerCandidates, unfollowMe, type Followers as FollowerState } from '../api/followers'
import type { TicketDetail } from '../api/ticketSchemas'
import { livePolling } from '../queries'
import Dialog from './Dialog'

/**
 * Internal followers (not the assignee, not email CC). Following needs only read access; adding or removing somebody else needs tickets.update.
 * The Follow button only ever changes the signed-in user's own follow.
 */
export default function Followers({ ticket: t, canManage }: { ticket: TicketDetail; canManage: boolean }) {
  const qc = useQueryClient()
  const key = ['ticket', t.id, 'followers']
  const q = useQuery({ queryKey: key, queryFn: () => getFollowers(t.id), ...livePolling })
  const [adding, setAdding] = useState(false)
  const [problem, setProblem] = useState<unknown>(null)
  const done = (s: FollowerState) => {
    setProblem(null)
    qc.setQueryData(key, s)
    void qc.invalidateQueries({ queryKey: ['ticket', t.id, 'activity'] })
    void qc.invalidateQueries({ queryKey: ['tickets'] })
  }
  const toggle = useMutation({ mutationFn: (follow: boolean) => (follow ? followMe(t.id) : unfollowMe(t.id)), onSuccess: done, onError: setProblem })
  const remove = useMutation({ mutationFn: (userId: string) => removeFollower(t.id, userId), onSuccess: done, onError: setProblem })
  const following = q.data?.following ?? false

  return (
    <section className="followers" aria-label="Followers">
      <h3>Followers</h3>
      <button
        type="button"
        className={following ? 'follow-toggle on' : 'follow-toggle'}
        aria-pressed={following}
        disabled={!q.data || toggle.isPending}
        onClick={() => toggle.mutate(!following)}
      >
        {following ? '✓ Following' : 'Follow'}
      </button>
      <ul className="follower-list" aria-label="People following this ticket">
        {q.data?.items.map((f) => (
          <li key={f.userId}>
            <span className="badge off">{f.displayName}</span>
            {canManage && (
              <button type="button" className="ghost" disabled={remove.isPending} aria-label={`Remove ${f.displayName} as a follower`} onClick={() => remove.mutate(f.userId)}>
                ×
              </button>
            )}
          </li>
        ))}
        {q.isSuccess && q.data.items.length === 0 && <li className="muted small">No followers yet</li>}
      </ul>
      {canManage && (
        <button type="button" className="ghost" onClick={() => setAdding(true)}>
          + Add
        </button>
      )}
      {problem != null && (
        <p role="alert" className="notice error">
          {describeError(problem)}
        </p>
      )}
      {adding && <AddDialog ticket={t} onAdded={done} onClose={() => setAdding(false)} />}
    </section>
  )
}

function AddDialog({ ticket: t, onAdded, onClose }: { ticket: TicketDetail; onAdded: (s: FollowerState) => void; onClose: () => void }) {
  const [text, setText] = useState('')
  const [term, setTerm] = useState('')
  useEffect(() => {
    const h = setTimeout(() => setTerm(text.trim()), 250)
    return () => clearTimeout(h)
  }, [text])
  const candidates = useQuery({ queryKey: ['ticket', t.id, 'follower-candidates', term], queryFn: () => searchFollowerCandidates(t.id, term) })
  const add = useMutation({
    mutationFn: (userId: string) => addFollower(t.id, userId),
    onSuccess: (s) => {
      onAdded(s)
      onClose()
    },
  })
  return (
    <Dialog title="Add follower" onClose={onClose}>
      <div className="dialog-body">
        <label htmlFor="follower-search">Search by name or email</label>
        <input id="follower-search" data-autofocus type="search" value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
        {candidates.isSuccess && candidates.data.length === 0 && <p className="empty small">No matching colleagues in this department.</p>}
        <ul className="candidate-list" aria-label="Colleagues">
          {candidates.data?.map((c) => (
            <li key={c.userId}>
              <button type="button" disabled={add.isPending} onClick={() => add.mutate(c.userId)}>
                {c.displayName}
                {c.email ? <span className="muted small"> · {c.email}</span> : null}
              </button>
            </li>
          ))}
        </ul>
        {add.isError && (
          <p role="alert" className="notice error">
            {describeError(add.error)}
          </p>
        )}
      </div>
    </Dialog>
  )
}
