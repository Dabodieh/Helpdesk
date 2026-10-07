import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { lookupUsers } from '../api/organisation'
import { getIdentityCase, linkRequester, listIdentityCases, type AccountRef, type IdentityCase } from '../api/requesterIdentity'
import type { Department } from '../api/schemas'
import { formatDateTime } from '../lib/format'
import { ErrorNotice, Field, Loading, StatusBadge } from './common'
import Dialog from './Dialog'

const MIN_QUERY = 3
const key = (d: string) => ['requester-identities', d]

function CaseRow({ c, onResolve }: { c: IdentityCase; onResolve: () => void }) {
  return (
    <li className="row">
      <div className="row-main">
        <div className="row-title">
          <strong>{c.displayName}</strong>
          <span className="muted">{c.email}</span>
          <StatusBadge tone={c.needsAttention ? 'warn' : 'off'}>Unlinked</StatusBadge>
        </div>
        <p className="muted small row-sub">
          {c.ticketCount} {c.ticketCount === 1 ? 'ticket' : 'tickets'} · {c.openCount} open
          {c.lastActivityAt ? ` · last activity ${formatDateTime(c.lastActivityAt)}` : ''}
          {c.matchingAccount ? ` · an account uses this address: ${c.matchingAccount.displayName}` : ''}
        </p>
      </div>
      <button type="button" onClick={onResolve} aria-label={`Resolve ${c.displayName}`}>
        Resolve
      </button>
    </li>
  )
}

/** Resolve one requester: look at the safe context, pick the account deliberately, then confirm. A matching address is only ever a suggestion. */
function ResolveDialog({ department, c, onClose }: { department: Department; c: IdentityCase; onClose: () => void }) {
  const qc = useQueryClient()
  const detail = useQuery({ queryKey: ['requester-identity', department.id, c.requesterId], queryFn: () => getIdentityCase(department.id, c.requesterId) })
  const [text, setText] = useState('')
  const [q, setQ] = useState('')
  const [chosen, setChosen] = useState<AccountRef | null>(null)
  const [confirming, setConfirming] = useState(false)
  const results = useQuery({ queryKey: ['userLookup', q], queryFn: () => lookupUsers(q), enabled: q.length >= MIN_QUERY })
  const link = useMutation({
    mutationFn: () => linkRequester(department.id, c.requesterId, chosen!.userId),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: key(department.id) })
      onClose()
    },
  })
  const search = (e: FormEvent) => {
    e.preventDefault()
    setQ(text.trim().length >= MIN_QUERY ? text.trim() : '')
  }
  const linked = detail.data?.status === 'Linked'

  if (confirming && chosen) {
    return (
      <Dialog title="Link requester to account?" onClose={onClose}>
        <div className="dialog-form">
          <dl className="prop-list">
            <div>
              <dt>Requester</dt>
              <dd>
                {c.displayName} <span className="muted">{c.email}</span>
              </dd>
            </div>
            <div>
              <dt>Account</dt>
              <dd>
                {chosen.displayName} <span className="muted">{chosen.email ?? ''}</span>
              </dd>
            </div>
          </dl>
          <p>This will make this account the stable owner of this requester and its requester-visible ticket history.</p>
          <p className="muted">It will not grant department staff permissions or access to restricted request forms. An address match is not proof: confirm this is the same person.</p>
          <ErrorNotice error={link.error} />
          <div className="dialog-actions">
            <button type="button" onClick={() => setConfirming(false)} disabled={link.isPending}>
              Back
            </button>
            <button type="button" className="primary" onClick={() => link.mutate()} disabled={link.isPending}>
              Link account
            </button>
          </div>
        </div>
      </Dialog>
    )
  }

  return (
    <Dialog title={`Resolve ${c.displayName}`} onClose={onClose}>
      <div className="dialog-form">
        <p>
          <strong>{c.displayName}</strong> <span className="muted">{c.email}</span>
        </p>
        <p className="muted small">
          {c.ticketCount} {c.ticketCount === 1 ? 'ticket' : 'tickets'} in this department, {c.openCount} open. Not linked to any account, so the person cannot use the portal for these requests.
        </p>
        {detail.isPending && <Loading label="Loading history" />}
        <ErrorNotice error={detail.error} />
        {linked && <p className="notice info">Already linked to {detail.data?.linkedAccount?.displayName ?? 'an account'}. Nothing more to do.</p>}
        {detail.data && detail.data.tickets.length > 0 && (
          <ul className="link-list" aria-label="Recent tickets">
            {detail.data.tickets.slice(0, 5).map((t) => (
              <li key={t.reference} className="link-row">
                <span>
                  <span className="ref">{t.reference}</span> {t.subject}
                </span>
                <span className="muted small">{t.status}</span>
              </li>
            ))}
          </ul>
        )}
        {!linked && (
          <>
            {c.matchingAccount && (
              <p className="notice info" role="note">
                An account uses the same address: <strong>{c.matchingAccount.displayName}</strong>. That is a coincidence to check, not proof.{' '}
                <button type="button" className="linklike" onClick={() => setChosen(c.matchingAccount ?? null)}>
                  Select this account
                </button>
              </p>
            )}
            <form className="inline" onSubmit={search} role="search" aria-label="Find account">
              <Field label="Find the account" hint={`Type at least ${MIN_QUERY} characters of a name or email.`}>
                {(p) => <input {...p} type="search" value={text} onChange={(e) => setText(e.target.value)} />}
              </Field>
              <button type="submit" disabled={text.trim().length < MIN_QUERY}>
                Search
              </button>
            </form>
            <ErrorNotice error={results.error} />
            {results.isSuccess && results.data.length === 0 && <p className="empty small">No matching accounts.</p>}
            {results.isSuccess && results.data.length > 0 && (
              <ul className="link-list" aria-label="Search results">
                {results.data.map((u) => (
                  <li key={u.id} className="link-row">
                    <span>
                      <strong>{u.displayName}</strong> <span className="muted small">{u.email}</span>
                    </span>
                    <button type="button" onClick={() => setChosen({ userId: u.id, displayName: u.displayName, email: u.email })} aria-label={`Select ${u.displayName}`}>
                      Select
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {chosen && (
              <p role="status">
                Selected account: <strong>{chosen.displayName}</strong> <span className="muted">{chosen.email ?? ''}</span>
              </p>
            )}
          </>
        )}
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Close
          </button>
          {!linked && (
            <button type="button" className="primary" disabled={!chosen} onClick={() => setConfirming(true)}>
              Link requester to account…
            </button>
          )}
        </div>
      </div>
    </Dialog>
  )
}

/** Requesters with ticket history that are not linked to an account (department-scoped; permission department.members.manage). */
export default function RequesterIdentitiesSection({ department }: { department: Department }) {
  const q = useQuery({ queryKey: key(department.id), queryFn: () => listIdentityCases(department.id) })
  const [resolving, setResolving] = useState<IdentityCase | null>(null)
  const [showOthers, setShowOthers] = useState(false)
  const all = q.data ?? []
  const attention = all.filter((c) => c.needsAttention)
  const others = all.filter((c) => !c.needsAttention)
  return (
    <section aria-labelledby="req-ident-h">
      <h2 id="req-ident-h">Requester identities</h2>
      <p className="muted">
        A requester is the department&rsquo;s record of someone who raised tickets. Linking one to an account lets that person see those requests in the portal. An email address alone
        never proves ownership, so each link is an explicit, audited decision. Linking grants no staff access.
      </p>
      {q.isPending && <Loading label="Loading requesters" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && all.length === 0 && <p className="empty">Nothing to resolve. Every requester with tickets is linked to an account.</p>}
      {attention.length > 0 && (
        <>
          <h3 className="section-title">Needs attention ({attention.length})</h3>
          <ul className="rows" aria-label="Needs attention">
            {attention.map((c) => (
              <CaseRow key={c.requesterId} c={c} onResolve={() => setResolving(c)} />
            ))}
          </ul>
        </>
      )}
      {others.length > 0 && (
        <>
          <h3 className="section-title">
            Other unlinked requesters ({others.length}){' '}
            <button type="button" className="linklike" aria-expanded={showOthers} onClick={() => setShowOthers((v) => !v)}>
              {showOthers ? 'Hide' : 'Show'}
            </button>
          </h3>
          {showOthers && (
            <ul className="rows" aria-label="Other unlinked requesters">
              {others.map((c) => (
                <CaseRow key={c.requesterId} c={c} onResolve={() => setResolving(c)} />
              ))}
            </ul>
          )}
        </>
      )}
      {resolving && <ResolveDialog department={department} c={resolving} onClose={() => setResolving(null)} />}
    </section>
  )
}
