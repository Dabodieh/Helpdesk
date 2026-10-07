import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import { canTicket } from '../api/permissions'
import { listOutboundMailboxes } from '../api/emailIdentity'
import { createTicket, lookupRequesters } from '../api/tickets'
import { PRIORITIES, type Priority, type RequesterLookup, type TicketDetail } from '../api/ticketSchemas'
import { useMe } from '../auth'
import { useDirectory } from '../hooks/useDirectory'
import { useDebounced } from '../lib/useDebounced'
import { ErrorNotice, Field } from './common'
import Dialog from './Dialog'

export default function NewTicketDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (t: TicketDetail) => void }) {
  const me = useMe()
  const qc = useQueryClient()
  const departments = me.memberships.filter((m) => m.permissions.includes('tickets.create'))
  const [departmentId, setDepartmentId] = useState(departments[0]?.departmentId ?? '')
  const [email, setEmail] = useState('')
  const [chosen, setChosen] = useState<RequesterLookup | null>(null)
  const [name, setName] = useState('')
  const [subject, setSubject] = useState('')
  const [description, setDescription] = useState('')
  const [priority, setPriority] = useState<Priority>('Normal')
  const [teamId, setTeamId] = useState('')
  const [assignee, setAssignee] = useState('')
  const [touched, setTouched] = useState(false)
  const [mailboxId, setMailboxId] = useState('')

  const canAssignAny = canTicket(me, 'tickets.assign.any', departmentId)
  const canAssignSelf = canTicket(me, 'tickets.assign.self', departmentId)
  const dir = useDirectory(canAssignAny ? departmentId : undefined)

  const term = useDebounced(email.trim(), 250)
  const lookup = useQuery({
    queryKey: ['requesters', departmentId, term],
    queryFn: () => lookupRequesters(departmentId, term),
    enabled: !!departmentId && !chosen && term.length >= 2,
  })

  // The mailbox is the ticket's sender identity for good. With one choice (or none) nothing is asked; the server picks the default.
  const mailboxes = useQuery({ queryKey: ['outbound-mailboxes', departmentId], queryFn: () => listOutboundMailboxes(departmentId), enabled: !!departmentId })
  const create = useMutation({
    mutationFn: () =>
      createTicket(departmentId, {
        subject: subject.trim(),
        description: description.trim(),
        requester: chosen ? { requesterId: chosen.id } : { email: email.trim(), displayName: name.trim() || undefined },
        priority,
        teamId: teamId || undefined,
        assigneeUserId: assignee || undefined,
        mailboxId: mailboxId || undefined,
      }),
    onSuccess: async (t) => {
      await qc.invalidateQueries({ queryKey: ['tickets'] })
      onCreated(t)
    },
  })

  const server = fieldErrors(create.error)
  const problems = {
    requester: chosen || /\S+@\S+\.\S+/.test(email.trim()) ? undefined : 'Enter the requester email address.',
    subject: subject.trim() ? undefined : 'Subject is required.',
    description: description.trim() ? undefined : 'Description is required.',
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    setTouched(true)
    if (problems.requester || problems.subject || problems.description || !departmentId) return
    create.mutate()
  }

  return (
    <Dialog title="New ticket" onClose={onClose}>
      <form className="form dialog-form" onSubmit={submit} noValidate>
        {departments.length > 1 && (
          <Field label="Department">
            {(p) => (
              <select
                {...p}
                value={departmentId}
                onChange={(e) => {
                  setDepartmentId(e.target.value)
                  setChosen(null)
                  setTeamId('')
                  setAssignee('')
                  setMailboxId('')
                }}
              >
                {departments.map((d) => (
                  <option key={d.departmentId} value={d.departmentId}>
                    {d.departmentName}
                  </option>
                ))}
              </select>
            )}
          </Field>
        )}
        {(mailboxes.data?.length ?? 0) > 1 && (
          <Field label="Send replies from" hint="Fixed for this ticket once it is created.">
            {(p) => (
              <select {...p} value={mailboxId} onChange={(e) => setMailboxId(e.target.value)}>
                <option value="">Default ({mailboxes.data!.find((m) => m.isDefault)?.name ?? mailboxes.data![0].name})</option>
                {mailboxes.data!.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name} &lt;{m.address}&gt;
                  </option>
                ))}
              </select>
            )}
          </Field>
        )}
        {chosen ? (
          <div className="field">
            <span className="label">Requester</span>
            <div className="chosen">
              <span>
                {chosen.displayName} <span className="muted">&lt;{chosen.email}&gt;</span>
              </span>
              <button type="button" onClick={() => setChosen(null)}>
                Change requester
              </button>
            </div>
          </div>
        ) : (
          <>
            <Field label="Requester email" error={(touched && problems.requester) || server['requester.email'] || server.requester}>
              {(p) => (
                <input
                  {...p}
                  type="email"
                  data-autofocus
                  autoComplete="off"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              )}
            </Field>
            {lookup.data && lookup.data.length > 0 && (
              <ul className="plain suggestions" aria-label="Matching requesters">
                {lookup.data.map((r) => (
                  <li key={r.id}>
                    <button type="button" className="ghost" onClick={() => setChosen(r)}>
                      {r.displayName} <span className="muted">&lt;{r.email}&gt;</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
            <Field label="Requester name (optional, for a new requester)">
              {(p) => <input {...p} value={name} onChange={(e) => setName(e.target.value)} />}
            </Field>
          </>
        )}
        <Field label="Subject" error={(touched && problems.subject) || server.subject}>
          {(p) => <input {...p} value={subject} maxLength={300} onChange={(e) => setSubject(e.target.value)} />}
        </Field>
        <Field label="Description" error={(touched && problems.description) || server.description}>
          {(p) => <textarea {...p} rows={5} value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <div className="row">
          <Field label="Priority">
            {(p) => (
              <select {...p} value={priority} onChange={(e) => setPriority(e.target.value as Priority)}>
                {PRIORITIES.map((x) => (
                  <option key={x}>{x}</option>
                ))}
              </select>
            )}
          </Field>
          {canAssignAny && (
            <Field label="Team">
              {(p) => (
                <select {...p} value={teamId} onChange={(e) => setTeamId(e.target.value)}>
                  <option value="">No team</option>
                  {dir.teams
                    .filter((t) => t.isActive)
                    .map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.name}
                      </option>
                    ))}
                </select>
              )}
            </Field>
          )}
          {(canAssignAny || canAssignSelf) && (
            <Field label="Assignee">
              {(p) => (
                <select {...p} value={assignee} onChange={(e) => setAssignee(e.target.value)}>
                  <option value="">Unassigned</option>
                  <option value={me.id}>Me</option>
                  {canAssignAny &&
                    dir.members
                      .filter((m) => m.userId !== me.id && m.role !== 'Viewer')
                      .map((m) => (
                        <option key={m.userId} value={m.userId}>
                          {m.displayName}
                        </option>
                      ))}
                </select>
              )}
            </Field>
          )}
        </div>
        <ErrorNotice error={create.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'Create ticket'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}
