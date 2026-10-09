import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { createEntraApp, disconnectEntra, getEntraStatus, pollEntraSetup, startEntraSetup, type EntraCreated, type EntraPoll, type EntraStart } from '../api/entra'
import { ErrorNotice, Field, Loading, StatusBadge } from './common'
import { formatDateTime } from '../lib/format'

type Stage = 'idle' | 'code' | 'signed_in' | 'done'

/** Poll Microsoft (through the server) until the administrator has entered the code. The access token never reaches the browser. */
function useDevicePolling(session: EntraStart | null, onResult: (p: EntraPoll) => void) {
  useEffect(() => {
    if (!session) return
    let cancelled = false
    let timer: ReturnType<typeof setTimeout>
    let interval = session.intervalSeconds
    const tick = async () => {
      try {
        const poll = await pollEntraSetup(session.sessionId)
        if (cancelled) return
        interval = poll.intervalSeconds
        onResult(poll)
        if (poll.state !== 'pending') return
      } catch {
        /* a missed poll is retried */
      }
      if (!cancelled) timer = setTimeout(() => void tick(), interval * 1000)
    }
    timer = setTimeout(() => void tick(), interval * 1000)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [session, onResult])
}

function Wizard({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient()
  const [stage, setStage] = useState<Stage>('idle')
  const [session, setSession] = useState<EntraStart | null>(null)
  const [poll, setPoll] = useState<EntraPoll | null>(null)
  const [origins, setOrigins] = useState<string[]>([window.location.origin])
  const [extra, setExtra] = useState('')
  const [created, setCreated] = useState<EntraCreated | null>(null)

  const start = useMutation({
    mutationFn: startEntraSetup,
    onSuccess: (s) => {
      setSession(s)
      setPoll(null)
      setStage('code')
    },
  })
  useDevicePolling(stage === 'code' ? session : null, (p) => {
    setPoll(p)
    if (p.state === 'signed_in') setStage('signed_in')
  })
  const create = useMutation({
    mutationFn: () => createEntraApp(session!.sessionId, origins),
    onSuccess: (c) => {
      setCreated(c)
      setStage('done')
      void qc.invalidateQueries({ queryKey: ['entra'] })
      void qc.invalidateQueries({ queryKey: ['microsoft-365'] })
    },
  })

  const addOrigin = () => {
    const value = extra.trim().replace(/\/+$/, '')
    if (value && !origins.includes(value)) setOrigins([...origins, value])
    setExtra('')
  }

  return (
    <div className="entra-wizard" role="group" aria-label="Connect Microsoft Entra ID">
      {stage === 'idle' && (
        <>
          <p>
            A Microsoft administrator signs in once with a short code, using their own account (Application Administrator or Global Administrator). Helpdesk then creates a single application for staff sign-in in your Microsoft tenant, with a
            12-month secret that it keeps protected in its database. It asks for no mail or directory permissions and changes nothing else.
          </p>
          <ErrorNotice error={start.error} />
          <div className="m365-actions">
            <button type="button" className="primary" disabled={start.isPending} onClick={() => start.mutate()}>
              {start.isPending ? 'Starting…' : 'Start'}
            </button>
            <button type="button" onClick={onClose}>
              Cancel
            </button>
          </div>
        </>
      )}
      {stage === 'code' && session && (
        <>
          <p>
            Open{' '}
            <a href={session.verificationUri} target="_blank" rel="noreferrer">
              {session.verificationUri}
            </a>{' '}
            and enter this code:
          </p>
          <p className="entra-code" aria-label="Sign-in code">
            {session.userCode}
          </p>
          {(!poll || poll.state === 'pending') && <Loading label="Waiting for you to sign in to Microsoft" />}
          {poll && poll.state !== 'pending' && poll.state !== 'signed_in' && (
            <p className="notice warn" role="alert">
              {poll.state === 'declined' ? 'The sign-in was declined in Microsoft.' : poll.state === 'expired' ? 'The code expired.' : 'Microsoft could not complete the sign-in.'}
            </p>
          )}
          <div className="m365-actions">
            {poll && poll.state !== 'pending' && (
              <button type="button" className="primary" disabled={start.isPending} onClick={() => start.mutate()}>
                Get a new code
              </button>
            )}
            <button type="button" onClick={onClose}>
              Cancel
            </button>
          </div>
        </>
      )}
      {stage === 'signed_in' && poll && (
        <>
          <p role="status">
            <StatusBadge tone="ok">Signed in</StatusBadge> as <strong>{poll.adminName ?? poll.adminAccount}</strong>
            {poll.adminAccount && poll.adminName ? ` (${poll.adminAccount})` : ''}, tenant {poll.tenantId}.
          </p>
          <p className="muted small">Each address people use to open Helpdesk must be registered with Microsoft, or sign-in from it will fail. Add every one (for example your public address and http://localhost:5173 while developing).</p>
          <ul className="entra-origins" aria-label="Addresses to register">
            {origins.map((o) => (
              <li key={o}>
                <code>{o}</code>
                {origins.length > 1 && (
                  <button type="button" className="linklike" aria-label={`Remove ${o}`} onClick={() => setOrigins(origins.filter((x) => x !== o))}>
                    Remove
                  </button>
                )}
              </li>
            ))}
          </ul>
          <Field label="Add another address">
            {(p) => (
              <span className="entra-add">
                <input {...p} value={extra} placeholder="https://helpdesk.example.org" onChange={(e) => setExtra(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), addOrigin())} />
                <button type="button" onClick={addOrigin}>
                  Add
                </button>
              </span>
            )}
          </Field>
          <ErrorNotice error={create.error} />
          <div className="m365-actions">
            <button type="button" className="primary" disabled={create.isPending} onClick={() => create.mutate()}>
              {create.isPending ? 'Creating…' : 'Create sign-in application'}
            </button>
            <button type="button" onClick={onClose}>
              Cancel
            </button>
          </div>
        </>
      )}
      {stage === 'done' && created && (
        <>
          <p role="status">
            <StatusBadge tone="ok">Connected</StatusBadge> Microsoft sign-in is now on. The secret is stored protected and expires {formatDateTime(created.secretExpiresAt)}.
          </p>
          <dl className="kv">
            <dt>Tenant</dt>
            <dd>{created.tenantId}</dd>
            <dt>Client id</dt>
            <dd>{created.clientId}</dd>
            <dt>Registered</dt>
            <dd>{created.redirectUris.join(', ')}</dd>
          </dl>
          <p>
            Test it: open <a href="/api/auth/login?returnUrl=/admin/microsoft-365" target="_blank" rel="noreferrer">Sign in with Microsoft</a> in a new tab. When you sign in as {created.signedInAs}, you become a platform administrator. Keep development sign-in
            on until that works, then switch it off in the server settings.
          </p>
          {created.previousApplicationObjectId && <p className="muted small">The previous application (object id {created.previousApplicationObjectId}) was left in your tenant. Remove it in Entra when you are sure it is no longer used.</p>}
          <div className="m365-actions">
            <button type="button" onClick={onClose}>
              Close
            </button>
          </div>
        </>
      )}
    </div>
  )
}

/** Staff sign-in (Microsoft Entra ID): what is connected, and the guided setup that creates the application. Platform administrators only (the page is). */
export default function EntraSetup() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['entra'], queryFn: getEntraStatus })
  const [open, setOpen] = useState(false)
  const disconnect = useMutation({
    mutationFn: disconnectEntra,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['entra'] })
      void qc.invalidateQueries({ queryKey: ['microsoft-365'] })
    },
  })

  return (
    <section className="entra-setup" aria-labelledby="entra-h">
      <h2 id="entra-h">Staff sign-in (Microsoft Entra ID)</h2>
      {q.isPending && <Loading label="Loading sign-in status" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.data && (
        <>
          <p>
            {q.data.configured ? (
              <StatusBadge tone={q.data.secretExpiresSoon ? 'warn' : 'ok'}>{q.data.source === 'server' ? 'Connected (server settings)' : 'Connected'}</StatusBadge>
            ) : (
              <StatusBadge tone="warn">Not connected</StatusBadge>
            )}{' '}
            {q.data.secretUnreadable && <span className="notice warn">The stored secret can no longer be read (the encryption keys changed). Connect again.</span>}
          </p>
          {q.data.configured && (
            <dl className="kv">
              <dt>Tenant</dt>
              <dd>{q.data.tenantId}</dd>
              <dt>Client id</dt>
              <dd>{q.data.clientId}</dd>
              {q.data.secretExpiresAt && (
                <>
                  <dt>Secret expires</dt>
                  <dd>
                    {formatDateTime(q.data.secretExpiresAt)}
                    {q.data.secretExpiresSoon ? ' (soon: reconnect to renew)' : ''}
                  </dd>
                </>
              )}
              {q.data.redirectOrigins.length > 0 && (
                <>
                  <dt>Registered addresses</dt>
                  <dd>{q.data.redirectOrigins.join(', ')}</dd>
                </>
              )}
            </dl>
          )}
          {q.data.source === 'server' && <p className="muted small">These come from the server settings, which take priority. Change them there.</p>}
          {q.data.devSignInEnabled && <p className="notice warn">Development sign-in is enabled. Once Microsoft sign-in works for you, switch it off in the server settings.</p>}
          <ErrorNotice error={disconnect.error} />
          {q.data.source !== 'server' && !open && (
            <div className="m365-actions">
              <button type="button" className="primary" onClick={() => setOpen(true)}>
                {q.data.configured ? 'Reconnect (new application and secret)' : 'Connect Microsoft Entra ID'}
              </button>
              {q.data.source === 'database' && (
                <button type="button" disabled={disconnect.isPending} onClick={() => disconnect.mutate()}>
                  Disconnect
                </button>
              )}
            </div>
          )}
          {open && <Wizard onClose={() => setOpen(false)} />}
        </>
      )}
    </section>
  )
}
