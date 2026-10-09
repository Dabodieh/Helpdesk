import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { checkConnection, checkMailbox, planNewMailbox, type Microsoft365Status } from '../api/microsoft365'
import { createMailbox } from '../api/mail'
import { listDepartments } from '../api/organisation'
import { ErrorNotice, Field, StatusBadge } from './common'

const STORAGE_KEY = 'helpdesk.m365.wizard'

type WizardState = { step: number; departmentId: string; mode: 'existing' | 'new'; address: string; displayName: string; alias: string; domain: string; isDefault: boolean }
const EMPTY: WizardState = { step: 0, departmentId: '', mode: 'existing', address: '', displayName: '', alias: '', domain: '', isDefault: false }

/** Setup progress is kept for the browser session only (no secrets are ever entered here), so a refresh does not lose the place. */
function load(): WizardState {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    return raw ? { ...EMPTY, ...(JSON.parse(raw) as Partial<WizardState>) } : EMPTY
  } catch {
    return EMPTY
  }
}

function save(state: WizardState) {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(state))
  } catch {
    /* storage unavailable: the wizard still works */
  }
}

const STEPS = ['Tenant', 'Sign-in', 'Mail access', 'Permissions', 'Department', 'Mailbox', 'Test access', 'Mail processing', 'Activate']

const CONNECTION_TEXT: Record<string, string> = {
  ok: 'The application signed in successfully.',
  authentication: 'The application could not sign in. Check the tenant id, client id and secret or certificate in the server configuration.',
  unavailable: 'Microsoft could not be reached from here. Try again shortly.',
}

const ACCESS_TEXT: Record<string, { tone: 'ok' | 'bad' | 'warn' | 'off'; text: string }> = {
  ok: { tone: 'ok', text: 'The application can reach this mailbox.' },
  denied: { tone: 'bad', text: 'The mailbox does not exist or is outside the application’s Exchange scope. Check the address and the scope assignment (access changes can take up to two hours to apply).' },
  unavailable: { tone: 'warn', text: 'Access could not be established right now. Nothing was registered; try again shortly.' },
  skipped: { tone: 'off', text: 'Not checked.' },
}

function Script({ lines }: { lines: string[] }) {
  const text = lines.join(String.fromCharCode(10))
  return (
    <div className="m365-script">
      <pre aria-label="Exchange Online PowerShell">{text}</pre>
      <button type="button" onClick={() => void navigator.clipboard?.writeText(text)}>
        Copy script
      </button>
    </div>
  )
}

export default function Microsoft365Wizard({ status }: { status: Microsoft365Status }) {
  const qc = useQueryClient()
  const [state, setState] = useState<WizardState>(load)
  const update = (patch: Partial<WizardState>) =>
    setState((cur) => {
      const next = { ...cur, ...patch }
      save(next)
      return next
    })
  const departments = useQuery({ queryKey: ['departments'], queryFn: listDepartments, staleTime: 30_000 })
  const active = (departments.data ?? []).filter((d) => d.isActive)
  const department = active.find((d) => d.id === state.departmentId)

  const connection = useMutation({ mutationFn: checkConnection })
  const mailboxCheck = useMutation({ mutationFn: () => checkMailbox(state.address) })
  const plan = useMutation({ mutationFn: () => planNewMailbox({ displayName: state.displayName, alias: state.alias, domain: state.domain, departmentId: state.departmentId }) })
  const register = useMutation({
    mutationFn: () => createMailbox(state.departmentId, { address: state.address.trim(), displayName: state.displayName.trim(), isDefault: state.isDefault || undefined }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['microsoft-365'] })
      void qc.invalidateQueries({ queryKey: ['mailboxes'] })
    },
  })

  const go = (step: number) => update({ step: Math.max(0, Math.min(STEPS.length - 1, step)) })
  const accessOk = mailboxCheck.data?.access === 'ok' && mailboxCheck.data.address.toLowerCase() === state.address.trim().toLowerCase() && !mailboxCheck.data.problem
  const planned = plan.data?.valid ? plan.data : null

  const canNext = (() => {
    switch (state.step) {
      case 3:
        return connection.data?.ok === true
      case 4:
        return !!department
      case 5:
        return state.mode === 'existing' && state.address.trim().length > 3 // a new mailbox continues from its own button, once the script has been prepared
      case 6:
        return accessOk
      case 7:
        return state.displayName.trim().length > 0
      default:
        return true
    }
  })()

  const reset = () => {
    register.reset()
    mailboxCheck.reset()
    plan.reset()
    update({ ...EMPTY })
  }

  const body = (() => {
    switch (state.step) {
      case 0:
        return (
          <>
            <h3>Microsoft 365 tenant</h3>
            {status.simulated ? (
              <p className="notice info">Mail runs on the built-in simulator. No Microsoft 365 tenant is connected, so nothing here reaches Microsoft.</p>
            ) : (
              <dl className="kv">
                <dt>Tenant id</dt>
                <dd>{status.graph.tenantId}</dd>
                <dt>Application (client) id</dt>
                <dd>{status.graph.clientId}</dd>
              </dl>
            )}
            <p className="muted small">Tenant details come from the server configuration. This page never asks for or stores a secret.</p>
          </>
        )
      case 1:
        return (
          <>
            <h3>Staff sign-in (Microsoft Entra ID)</h3>
            {status.signIn.entraConfigured ? (
              <dl className="kv">
                <dt>Tenant id</dt>
                <dd>{status.signIn.tenantId}</dd>
                <dt>Application (client) id</dt>
                <dd>{status.signIn.clientId}</dd>
                <dt>Client secret</dt>
                <dd>{status.signIn.secretPresent ? 'Configured (not shown)' : 'Missing'}</dd>
                <dt>Redirect URI</dt>
                <dd>
                  <code>{`${window.location.origin}${status.signIn.redirectPath}`}</code>
                </dd>
              </dl>
            ) : (
              <p className="notice warn">Microsoft sign-in is not configured{status.signIn.devSignInEnabled ? '; development sign-in is in use' : ''}.</p>
            )}
            <p className="muted small">Sign-in only identifies staff (openid, profile, email). It is separate from the mail application and reads no mail.</p>
          </>
        )
      case 2:
        return (
          <>
            <h3>Mail processing (Microsoft Graph)</h3>
            <dl className="kv">
              <dt>Provider</dt>
              <dd>{status.simulated ? 'Simulator' : 'Microsoft Graph'}</dd>
              <dt>Credential</dt>
              <dd>{status.graph.credential === 'certificate' ? 'Certificate' : status.graph.credential === 'secret' ? 'Client secret (not shown)' : 'None'}</dd>
              <dt>New mail</dt>
              <dd>
                {status.notifications.mode === 'webhook'
                  ? `Change notifications to ${status.notifications.baseUrl}, plus a check every ${status.notifications.reconcileMinutes} minutes`
                  : `Checked every ${status.notifications.reconcileMinutes} minutes (simulator)`}
              </dd>
            </dl>
            <p className="muted small">Graph change notifications need a public HTTPS address that Microsoft can reach. The regular check keeps mail flowing if a notification is missed.</p>
          </>
        )
      case 3:
        return (
          <>
            <h3>Permissions and consent</h3>
            <ul className="m365-perms">
              {status.requiredPermissions.map((p) => (
                <li key={p.name}>
                  <StatusBadge tone={p.required ? 'info' : 'off'}>{p.required ? 'Required' : 'Not needed'}</StatusBadge> <strong>{p.name}</strong>
                  <span className="muted small"> {p.purpose} {p.scope}.</span>
                </li>
              ))}
            </ul>
            <p className="muted small">Helpdesk never grants consent. A tenant administrator grants the mail roles scoped to the Helpdesk mailboxes in Exchange Online.</p>
            <div className="m365-actions">
              <button type="button" disabled={connection.isPending} onClick={() => connection.mutate()}>
                {connection.isPending ? 'Checking…' : 'Check connection'}
              </button>
              {connection.data && (
                <StatusBadge tone={connection.data.ok ? 'ok' : 'bad'}>{connection.data.ok ? 'Connected' : 'Not connected'}</StatusBadge>
              )}
            </div>
            {connection.data && <p role="status">{CONNECTION_TEXT[connection.data.code] ?? 'The check did not complete.'}</p>}
            <ErrorNotice error={connection.error} />
          </>
        )
      case 4:
        return (
          <>
            <h3>Department</h3>
            <Field label="Destination department" hint="Mail received by the mailbox creates tickets in this department.">
              {(p) => (
                <select {...p} value={state.departmentId} onChange={(e) => update({ departmentId: e.target.value })}>
                  <option value="">Choose a department</option>
                  {active.map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.name}
                    </option>
                  ))}
                </select>
              )}
            </Field>
          </>
        )
      case 5:
        return (
          <>
            <h3>Shared mailbox</h3>
            <div className="seg" role="radiogroup" aria-label="Mailbox source">
              <label className={state.mode === 'existing' ? 'on' : undefined}>
                <input type="radio" name="m365-mode" checked={state.mode === 'existing'} onChange={() => update({ mode: 'existing' })} /> Connect an existing shared mailbox
              </label>
              <label className={state.mode === 'new' ? 'on' : undefined}>
                <input type="radio" name="m365-mode" checked={state.mode === 'new'} onChange={() => update({ mode: 'new' })} /> Create a new shared mailbox
              </label>
            </div>
            {state.mode === 'existing' ? (
              <Field label="Mailbox address" hint="Helpdesk has no directory access by design, so enter the address.">
                {(p) => <input {...p} type="email" value={state.address} onChange={(e) => { mailboxCheck.reset(); update({ address: e.target.value }) }} />}
              </Field>
            ) : (
              <>
                <p className="notice info">
                  Helpdesk cannot create Microsoft 365 mailboxes. Automatic creation needs a separate Exchange administration identity, which has not been approved or configured. A tenant administrator runs the script below.
                </p>
                <div className="m365-form">
                  <Field label="Display name" error={plan.data?.problems.displayName}>
                    {(p) => <input {...p} value={state.displayName} maxLength={64} onChange={(e) => update({ displayName: e.target.value })} />}
                  </Field>
                  <Field label="Address (before @)" error={plan.data?.problems.alias}>
                    {(p) => <input {...p} value={state.alias} maxLength={64} onChange={(e) => update({ alias: e.target.value })} />}
                  </Field>
                  <Field label="Domain" error={plan.data?.problems.domain}>
                    {(p) =>
                      status.approvedDomains.length > 0 ? (
                        <select {...p} value={state.domain} onChange={(e) => update({ domain: e.target.value })}>
                          <option value="">Choose a domain</option>
                          {status.approvedDomains.map((d) => (
                            <option key={d}>{d}</option>
                          ))}
                        </select>
                      ) : (
                        <input {...p} value={state.domain} placeholder="example.org" onChange={(e) => update({ domain: e.target.value })} />
                      )
                    }
                  </Field>
                </div>
                {plan.data?.problems.departmentId && <p className="field-error">{plan.data.problems.departmentId}</p>}
                <div className="m365-actions">
                  <button type="button" disabled={plan.isPending} onClick={() => plan.mutate()}>
                    {plan.isPending ? 'Preparing…' : 'Prepare script'}
                  </button>
                </div>
                <ErrorNotice error={plan.error} />
                {planned && (
                  <>
                    <p>
                      Address: <strong>{planned.address}</strong> · Department: <strong>{planned.departmentName}</strong>
                    </p>
                    <Script lines={planned.script} />
                    <p className="muted small">
                      After the script has run and Exchange has applied the scope (up to two hours), continue to check access. Helpdesk does not delete a mailbox if setup is abandoned.
                    </p>
                    <button type="button" className="primary" onClick={() => { mailboxCheck.reset(); update({ mode: 'existing', address: planned.address ?? '', displayName: state.displayName }); go(6) }}>
                      The mailbox exists: check access
                    </button>
                  </>
                )}
              </>
            )}
          </>
        )
      case 6:
        return (
          <>
            <h3>Test access</h3>
            {state.address.trim() ? (
              <>
                <p>
                  Mailbox: <strong>{state.address.trim()}</strong>
                </p>
                <div className="m365-actions">
                  <button type="button" disabled={mailboxCheck.isPending} onClick={() => mailboxCheck.mutate()}>
                    {mailboxCheck.isPending ? 'Checking…' : 'Check mailbox'}
                  </button>
                </div>
                {mailboxCheck.data && (
                  <div role="status">
                    {!mailboxCheck.data.valid ? (
                      <p className="notice warn">That is not a valid email address.</p>
                    ) : mailboxCheck.data.problem === 'already_registered' ? (
                      <p className="notice warn">This mailbox is already connected to {mailboxCheck.data.registeredDepartmentName ?? 'a department'}. A mailbox belongs to one department.</p>
                    ) : (
                      <p>
                        <StatusBadge tone={ACCESS_TEXT[mailboxCheck.data.access].tone}>{mailboxCheck.data.access === 'ok' ? 'Reachable' : mailboxCheck.data.access === 'denied' ? 'Not reachable' : 'Unknown'}</StatusBadge>{' '}
                        {ACCESS_TEXT[mailboxCheck.data.access].text}
                      </p>
                    )}
                  </div>
                )}
                <ErrorNotice error={mailboxCheck.error} />
              </>
            ) : (
              <p className="notice warn">Choose or prepare a mailbox first.</p>
            )}
          </>
        )
      case 7:
        return (
          <>
            <h3>Mail processing</h3>
            <Field label="Display name in Helpdesk" error={state.displayName.trim() ? undefined : 'Required.'}>
              {(p) => <input {...p} value={state.displayName} maxLength={200} onChange={(e) => update({ displayName: e.target.value })} />}
            </Field>
            <label className="check">
              <input type="checkbox" checked={state.isDefault} onChange={(e) => update({ isDefault: e.target.checked })} /> Default mailbox for new tickets created by staff in this department
            </label>
            <ul className="muted small m365-notes">
              <li>Incoming mail creates or updates tickets in {department?.name ?? 'the department'}.</li>
              <li>Replies are sent from the mailbox stored on the ticket. The sender is never switched.</li>
              <li>The first mailbox in a department becomes its default automatically.</li>
            </ul>
          </>
        )
      default:
        return (
          <>
            <h3>Activate</h3>
            {register.isSuccess ? (
              <>
                <p role="status">
                  <StatusBadge tone="ok">Connected</StatusBadge> {register.data.address} is connected to {department?.name}.
                </p>
                <p className="muted small">The first mail check runs shortly. Watch its health in the table below.</p>
                <div className="m365-actions">
                  <Link to={`/admin/departments/${state.departmentId}?section=mailboxes`}>Open department mailbox settings</Link>
                  <button type="button" onClick={reset}>
                    Connect another
                  </button>
                </div>
              </>
            ) : (
              <>
                <dl className="kv">
                  <dt>Department</dt>
                  <dd>{department?.name}</dd>
                  <dt>Mailbox</dt>
                  <dd>{state.address.trim()}</dd>
                  <dt>Display name</dt>
                  <dd>{state.displayName.trim()}</dd>
                  <dt>Default</dt>
                  <dd>{state.isDefault ? 'Yes' : 'Only if it is the first'}</dd>
                </dl>
                <p className="muted small">Helpdesk checks access again, registers the mailbox, starts mail notifications and runs a first check. Nothing is changed in Microsoft 365.</p>
                <ErrorNotice error={register.error} />
                <div className="m365-actions">
                  <button type="button" className="primary" disabled={register.isPending || !department || !state.address.trim() || !state.displayName.trim()} onClick={() => register.mutate()}>
                    {register.isPending ? 'Connecting…' : 'Connect mailbox'}
                  </button>
                </div>
              </>
            )}
          </>
        )
    }
  })()

  return (
    <section className="m365-wizard" aria-labelledby="wizard-h">
      <h2 id="wizard-h">Set up a mailbox</h2>
      <ol className="m365-steps" aria-label="Setup progress">
        {STEPS.map((label, i) => (
          <li key={label} aria-current={i === state.step ? 'step' : undefined} className={i < state.step ? 'done' : i === state.step ? 'current' : undefined}>
            <button type="button" disabled={i > state.step} onClick={() => go(i)}>
              <span className="m365-step-no" aria-hidden="true">{i < state.step ? '✓' : i + 1}</span> {label}
            </button>
          </li>
        ))}
      </ol>
      <div className="m365-step-body">{body}</div>
      {state.step < STEPS.length - 1 || !register.isSuccess ? (
        <div className="m365-nav">
          <button type="button" disabled={state.step === 0 || register.isPending} onClick={() => go(state.step - 1)}>
            Back
          </button>
          {state.step < STEPS.length - 1 && (
            <button type="button" className="primary" disabled={!canNext} onClick={() => go(state.step + 1)}>
              Next
            </button>
          )}
        </div>
      ) : null}
    </section>
  )
}
