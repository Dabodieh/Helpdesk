import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { createRule, listHistory, listRules, reorderRules, testRule, TRIGGER_LABELS, TRIGGERS, updateRule, type Execution, type Rule, type TestResult } from '../api/automation'
import { fieldErrors } from '../api/errors'
import type { Department } from '../api/schemas'
import type { Condition } from '../api/sla'
import { useDirectory } from '../hooks/useDirectory'
import { formatDateTime } from '../lib/format'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'
import { RULE_FIELDS, summariseRule } from '../lib/rules'
import { ActionsEditor, ConditionsEditor } from './RuleEditors'

function RuleEditor({ department, existing, onClose }: { department: Department; existing?: Rule; onClose: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(existing?.name ?? '')
  const [trigger, setTrigger] = useState(existing?.trigger ?? 'TicketCreated')
  const [active, setActive] = useState(existing?.isActive ?? true)
  const [stop, setStop] = useState(existing?.stopProcessing ?? false)
  const [conditions, setConditions] = useState<Condition[]>(existing?.conditions ?? [])
  const [actions, setActions] = useState(existing?.actions ?? [{ type: 'setPriority', value: 'High', text: null }])
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), trigger, isActive: active, stopProcessing: stop, conditions, actions }
      return existing ? updateRule(department.id, existing.id, { ...body, version: existing.version }) : createRule(department.id, body)
    },
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['automations', department.id] })
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim()) save.mutate()
  }
  return (
    <Dialog title={existing ? `Edit ${existing.name}` : 'New automation rule'} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} aria-label="Automation rule">
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={100} required onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="When" error={server.trigger}>
          {(p) => (
            <select {...p} value={trigger} onChange={(e) => setTrigger(e.target.value)}>
              {TRIGGERS.map((t) => (
                <option key={t} value={t}>
                  {TRIGGER_LABELS[t]}
                </option>
              ))}
            </select>
          )}
        </Field>
        <ConditionsEditor departmentId={department.id} fields={RULE_FIELDS} value={conditions} onChange={setConditions} errors={server} label="If" />
        <ActionsEditor departmentId={department.id} value={actions} onChange={setActions} errors={server} />
        <label className="check">
          <input type="checkbox" checked={stop} onChange={(e) => setStop(e.target.checked)} /> Stop processing further rules for this event when this rule matches
        </label>
        <label className="check">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Active
        </label>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending || !name.trim()}>
            Save rule
          </button>
        </div>
      </form>
    </Dialog>
  )
}

const RESULT_TEXT: Record<string, string> = { WouldChange: 'Would change', WouldNotChange: 'No change needed', WouldFail: 'Would fail' }

function TestDialog({ department, rule, onClose }: { department: Department; rule?: Rule; onClose: () => void }) {
  const [ticket, setTicket] = useState('')
  const [trigger, setTrigger] = useState(rule?.trigger ?? 'TicketCreated')
  const run = useMutation({ mutationFn: () => testRule(department.id, { ticket: ticket.trim(), trigger, ruleId: rule?.id }) })
  return (
    <Dialog title={rule ? `Test “${rule.name}” against a ticket` : 'Test rules against a ticket'} onClose={onClose}>
      <form
        className="form inline"
        aria-label="Test against ticket"
        onSubmit={(e) => {
          e.preventDefault()
          if (ticket.trim()) run.mutate()
        }}
      >
        <Field label="Ticket reference" hint="For example IT-1042. Nothing is changed: this only explains what would happen.">
          {(p) => <input {...p} data-autofocus value={ticket} onChange={(e) => setTicket(e.target.value)} />}
        </Field>
        {!rule && (
          <Field label="As if this happened">
            {(p) => (
              <select {...p} value={trigger} onChange={(e) => setTrigger(e.target.value)}>
                {TRIGGERS.map((t) => (
                  <option key={t} value={t}>
                    {TRIGGER_LABELS[t]}
                  </option>
                ))}
              </select>
            )}
          </Field>
        )}
        <button type="submit" className="primary" disabled={!ticket.trim() || run.isPending}>
          {run.isPending ? 'Testing…' : 'Test'}
        </button>
      </form>
      <ErrorNotice error={run.error} />
      {run.data && <TestOutcome result={run.data} />}
    </Dialog>
  )
}

function TestOutcome({ result }: { result: TestResult }) {
  return (
    <div role="region" aria-label="Test result">
      <p>
        <strong>{result.ticket}</strong>, trigger “{TRIGGER_LABELS[result.trigger as keyof typeof TRIGGER_LABELS] ?? result.trigger}”:{' '}
        {result.wouldChangeAnything ? 'something would change.' : 'nothing would change.'}
      </p>
      {result.rules.length === 0 && <p className="empty small">No active rules for this trigger.</p>}
      {result.rules.map((r) => (
        <div key={r.ruleId} className="test-rule">
          <h3>
            {r.name} <span className="badge">{!r.evaluated ? 'Not evaluated' : r.matched ? 'Matched' : 'Did not match'}</span>
          </h3>
          {r.note && <p className="muted small">{r.note}</p>}
          {r.conditions.length > 0 && (
            <ul className="trace" aria-label="Conditions">
              {r.conditions.map((c, i) => (
                <li key={i}>
                  <span aria-hidden="true">{c.result ? '✓' : '✗'}</span> {c.field} {c.operator} {c.values.join(', ')} <span className="muted small">{c.result ? 'true' : 'false'}</span>
                </li>
              ))}
            </ul>
          )}
          {r.actions.length > 0 && (
            <ul className="trace" aria-label="Actions">
              {r.actions.map((a) => (
                <li key={a.index}>
                  {a.type}
                  {a.value ? ` ${a.value}` : ''}: <strong>{RESULT_TEXT[a.result] ?? a.result}</strong> {a.detail && <span className="muted small">({a.detail})</span>}
                </li>
              ))}
            </ul>
          )}
        </div>
      ))}
      <p className="muted small">{result.notice}</p>
    </div>
  )
}

function History({ department }: { department: Department }) {
  const [ticket, setTicket] = useState('')
  const q = useInfiniteQuery({
    queryKey: ['automation-history', department.id, ticket],
    queryFn: ({ pageParam }) => listHistory(department.id, { ticket: ticket.trim() || undefined, cursor: pageParam }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
  })
  const rows: Execution[] = q.data?.pages.flatMap((p) => p.items) ?? []
  return (
    <section aria-labelledby="auto-hist">
      <h3 id="auto-hist">Execution history</h3>
      <p className="muted small">Why each rule ran, or did not. Diagnostic detail stays here and is never shown in a ticket conversation.</p>
      <Field label="Filter by ticket reference">{(p) => <input {...p} value={ticket} onChange={(e) => setTicket(e.target.value)} placeholder="IT-1042" />}</Field>
      {q.isPending && <Loading label="Loading history" />}
      {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
      {q.isSuccess && rows.length === 0 && <p className="empty small">No executions yet.</p>}
      {rows.length > 0 && (
        <table className="grid">
          <caption className="sr-only">Automation executions</caption>
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Rule</th>
              <th scope="col">Ticket</th>
              <th scope="col">Trigger</th>
              <th scope="col">Result</th>
              <th scope="col">Details</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((e) => (
              <tr key={e.id}>
                <td>{formatDateTime(e.startedAt)}</td>
                <td>{e.ruleName}</td>
                <td>{e.ticketReference}</td>
                <td className="small">
                  {e.trigger} <span className="muted">· from {e.provenance}</span>
                </td>
                <td>
                  {e.state === 'Skipped' ? `Skipped (${e.skipReason})` : e.state === 'Failed' ? 'Failed' : e.matched ? 'Matched' : 'Did not match'}
                </td>
                <td className="small">
                  <details>
                    <summary>Show</summary>
                    <ul className="trace">
                      {e.conditions.map((c, i) => (
                        <li key={i}>
                          <span aria-hidden="true">{c.result ? '✓' : '✗'}</span> {c.field} {c.operator} {c.values.join(', ')}
                        </li>
                      ))}
                      {e.actions.map((a) => (
                        <li key={a.index}>
                          {a.type}
                          {a.value ? ` ${a.value}` : ''}: {a.outcome} {a.detail && `(${a.detail})`}
                        </li>
                      ))}
                      {e.error && <li className="field-error">{e.error}</li>}
                      <li className="muted">Chain depth {e.depth}</li>
                    </ul>
                  </details>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {q.hasNextPage && (
        <button type="button" disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
          Load more
        </button>
      )}
    </section>
  )
}

/** Department automation rules (permission department.automation.manage). Structured editors only; no scripts. */
export default function AutomationsSection({ department }: { department: Department }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['automations', department.id], queryFn: () => listRules(department.id) })
  const dir = useDirectory(department.id)
  const [editing, setEditing] = useState<Rule | 'new' | null>(null)
  const [testing, setTesting] = useState<Rule | 'all' | null>(null)
  const [view, setView] = useState<'rules' | 'history'>('rules')
  const reorder = useMutation({
    mutationFn: ({ trigger, ids }: { trigger: string; ids: string[] }) => reorderRules(department.id, trigger, ids),
    onSuccess: (data) => qc.setQueryData(['automations', department.id], data),
  })
  const toggle = useMutation({
    mutationFn: (r: Rule) => updateRule(department.id, r.id, { isActive: !r.isActive, version: r.version }),
    onSettled: () => qc.invalidateQueries({ queryKey: ['automations', department.id] }),
  })
  const nameOf = (kind: string, value: string) =>
    value === 'none' ? 'none' : kind === 'team' ? (dir.names.teams.get(value) ?? value) : kind === 'assignee' ? (dir.names.users.get(value) ?? value) : value

  const rules = q.data ?? []
  const move = (trigger: string, index: number, delta: -1 | 1) => {
    const ids = rules.filter((r) => r.trigger === trigger).map((r) => r.id)
    const j = index + delta
    if (j < 0 || j >= ids.length) return
    ;[ids[index], ids[j]] = [ids[j]!, ids[index]!]
    reorder.mutate({ trigger, ids })
  }

  return (
    <section aria-labelledby="auto-h">
      <div className="section-head">
        <h2 id="auto-h">Automations</h2>
        <span className="page-actions">
          <button type="button" onClick={() => setTesting('all')}>
            Test against ticket
          </button>
          <button type="button" className="primary" onClick={() => setEditing('new')}>
            New rule
          </button>
        </span>
      </div>
      <p className="muted">Declarative rules: when something happens and the conditions are true, the actions run, in the order shown. Rules only ever touch tickets of this department.</p>
      <div className="seg" role="radiogroup" aria-label="Automations view">
        <label className={view === 'rules' ? 'on' : undefined}>
          <input type="radio" name="auto-view" checked={view === 'rules'} onChange={() => setView('rules')} /> Rules
        </label>
        <label className={view === 'history' ? 'on' : undefined}>
          <input type="radio" name="auto-view" checked={view === 'history'} onChange={() => setView('history')} /> History
        </label>
      </div>
      {view === 'history' ? (
        <History department={department} />
      ) : (
        <>
          {q.isPending && <Loading label="Loading automation rules" />}
          {q.isError && <ErrorNotice error={q.error} onRetry={() => void q.refetch()} />}
          {q.isSuccess && rules.length === 0 && <p className="empty">No automation rules yet.</p>}
          {TRIGGERS.filter((t) => rules.some((r) => r.trigger === t)).map((t) => {
            const group = rules.filter((r) => r.trigger === t)
            return (
              <div key={t} className="rule-group">
                <h3>{TRIGGER_LABELS[t]}</h3>
                <table className="grid">
                  <caption className="sr-only">Rules for “{TRIGGER_LABELS[t]}”, in running order</caption>
                  <thead>
                    <tr>
                      <th scope="col">Order</th>
                      <th scope="col">Rule</th>
                      <th scope="col">What it does</th>
                      <th scope="col">Status</th>
                      <th scope="col" />
                    </tr>
                  </thead>
                  <tbody>
                    {group.map((r, i) => (
                      <tr key={r.id}>
                        <td className="row-actions">
                          <span className="muted">{i + 1}</span>
                          <button type="button" className="ghost" aria-label={`Move ${r.name} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(t, i, -1)}>
                            ↑
                          </button>
                          <button type="button" className="ghost" aria-label={`Move ${r.name} down`} disabled={i === group.length - 1 || reorder.isPending} onClick={() => move(t, i, 1)}>
                            ↓
                          </button>
                        </td>
                        <td>
                          <strong>{r.name}</strong> {r.stopProcessing && <span className="badge">Stops further rules</span>}
                        </td>
                        <td className="small">{summariseRule(r.conditions, r.actions, nameOf)}</td>
                        <td>
                          <ActiveBadge active={r.isActive} />
                        </td>
                        <td className="row-actions">
                          <button type="button" onClick={() => setEditing(r)}>
                            Edit<span className="sr-only"> {r.name}</span>
                          </button>
                          <button type="button" onClick={() => setTesting(r)}>
                            Test
                          </button>
                          <button type="button" disabled={toggle.isPending} onClick={() => toggle.mutate(r)}>
                            {r.isActive ? 'Deactivate' : 'Activate'}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )
          })}
          <ErrorNotice error={reorder.error ?? toggle.error} />
        </>
      )}
      {editing && <RuleEditor department={department} existing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
      {testing && <TestDialog department={department} rule={testing === 'all' ? undefined : testing} onClose={() => setTesting(null)} />}
    </section>
  )
}
