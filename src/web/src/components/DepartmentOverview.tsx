import { useQuery } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { listRules } from '../api/automation'
import { getAutoReply } from '../api/emailIdentity'
import { listMailboxes } from '../api/mail'
import { listMembers, listTeams } from '../api/organisation'
import { canInDepartment } from '../api/permissions'
import type { Department } from '../api/schemas'
import { listBusinessHours, listSlaPolicies } from '../api/sla'
import { listForms } from '../api/ticketing'
import { useMe } from '../auth'
import { StatusBadge, type Tone } from './common'

type Row = { id: string; label: string; tone: Tone; state: string; detail?: string; section: string }

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/**
 * "What is this department and is it set up?" Built only from lists the caller may already read; a row for an area the caller cannot manage is simply
 * not shown. Nothing here is mandatory: "Not set up" is information, not an error.
 */
export default function DepartmentOverview({ department: d, onOpen }: { department: Department; onOpen: (section: string) => void }) {
  const me = useMe()
  const can = (p: string) => canInDepartment(me, p, d.id)
  const members = useQuery({ queryKey: ['members', d.id], queryFn: () => listMembers(d.id) })
  const teams = useQuery({ queryKey: ['teams', d.id], queryFn: () => listTeams(d.id) })
  const mailboxes = useQuery({ queryKey: ['mailboxes', d.id], queryFn: () => listMailboxes(d.id), enabled: can('department.mailboxes.manage') })
  const hours = useQuery({ queryKey: ['business-hours', d.id], queryFn: () => listBusinessHours(d.id), enabled: can('department.sla.manage') })
  const sla = useQuery({ queryKey: ['sla-policies', d.id], queryFn: () => listSlaPolicies(d.id), enabled: can('department.sla.manage') })
  const rules = useQuery({ queryKey: ['automations', d.id], queryFn: () => listRules(d.id), enabled: can('department.automation.manage') })
  const forms = useQuery({ queryKey: ['request-forms', d.id], queryFn: () => listForms(d.id), enabled: can('department.forms.manage') })
  const ack = useQuery({ queryKey: ['auto-reply', d.id], queryFn: () => getAutoReply(d.id), enabled: can('department.mailboxes.manage') })

  const rows: Row[] = []
  if (members.data) {
    const n = members.data.length
    rows.push({ id: 'members', label: 'Members', tone: n > 0 ? 'ok' : 'warn', state: n > 0 ? plural(n, 'member') : 'No members', section: 'members' })
  }
  if (teams.data) {
    const n = teams.data.filter((t) => t.isActive).length
    rows.push({ id: 'teams', label: 'Teams', tone: n > 0 ? 'ok' : 'off', state: n > 0 ? plural(n, 'active team') : 'No teams', detail: 'Used for routing and assignment', section: 'teams' })
  }
  if (mailboxes.data) {
    const active = mailboxes.data.filter((m) => m.isActive)
    const def = active.find((m) => m.isDefault)
    rows.push({
      id: 'mailboxes',
      label: 'Mailboxes',
      tone: def ? 'ok' : 'warn',
      state: def ? `Default: ${def.address}` : active.length > 0 ? 'No default mailbox' : 'No mailbox',
      detail: active.length > 0 ? plural(active.length, 'active mailbox', 'active mailboxes') : 'Replies cannot be sent by email',
      section: 'mailboxes',
    })
  }
  if (hours.data) {
    rows.push({ id: 'hours', label: 'Business hours', tone: hours.data.length > 0 ? 'ok' : 'off', state: hours.data.length > 0 ? plural(hours.data.length, 'calendar') : 'Not configured', section: 'hours' })
  }
  if (sla.data) {
    const n = sla.data.filter((p) => p.isActive).length
    rows.push({ id: 'sla', label: 'SLA policies', tone: n > 0 ? 'ok' : 'off', state: n > 0 ? plural(n, 'active policy', 'active policies') : 'Not configured', section: 'sla' })
  }
  if (rules.data) {
    const n = rules.data.filter((r) => r.isActive).length
    rows.push({ id: 'automations', label: 'Automations', tone: n > 0 ? 'ok' : 'off', state: n > 0 ? plural(n, 'active rule') : 'None', section: 'automations' })
  }
  if (forms.data) {
    // A form counts only when at least one requester can use it: active, offered to requesters, and an audience that is not empty (ITSM-006).
    const usable = forms.data.filter((f) => f.isActive && f.visibleToRequesters && (f.audience === 'AllEmployees' || f.audienceUsers.length > 0))
    const active = forms.data.filter((f) => f.isActive).length
    const hasSender = mailboxes.data ? mailboxes.data.some((m) => m.isActive && m.isDefault) : true
    const n = usable.length
    rows.push({
      id: 'forms',
      label: 'Request forms',
      tone: n > 0 && hasSender ? 'ok' : n > 0 ? 'warn' : 'off',
      state: n > 0 ? plural(n, 'form') + ' available to requesters' : active > 0 ? `${plural(active, 'active form')}, none available to anyone` : 'None',
      detail:
        n === 0
          ? active > 0
            ? 'Open a form and choose who may use it (new forms start restricted to selected people)'
            : 'Requesters cannot raise tickets through the portal'
          : hasSender
            ? undefined
            : 'Requesters cannot submit until the department has a usable default mailbox (set up the mailbox first)',
      section: 'ticketing',
    })
  }
  if (ack.data) {
    const on = ack.data.enabled && (ack.data.forEmail || ack.data.forPortal)
    rows.push({
      id: 'acknowledgements',
      label: 'Acknowledgements',
      tone: on ? 'ok' : 'off',
      state: on ? 'On' : 'Off',
      detail: on ? [ack.data.forEmail && 'for email', ack.data.forPortal && 'for the portal'].filter(Boolean).join(' and ') : 'Requesters get no automatic confirmation',
      section: 'mailboxes',
    })
  }

  return (
    <section aria-labelledby="overview-h" className="overview">
      <h2 id="overview-h">Department setup</h2>
      <p className="muted">{d.description || 'No description yet.'}</p>
      <div className="overview-grid">
        <div>
          {[members, teams, mailboxes, hours, sla, rules, forms, ack].some(q => q.isError) && <p className="notice error" role="alert">Some setup details could not be loaded. Open the relevant section to retry.</p>}
          {rows.length === 0 && <p className="empty small">Setup details appear here as the lists load.</p>}
          {[
            { title: 'People & access', ids: ['members', 'teams'] },
            { title: 'Communication', ids: ['mailboxes', 'acknowledgements'] },
            { title: 'Service management', ids: ['hours', 'sla', 'automations'] },
            { title: 'Self service', ids: ['forms'] },
          ].map(group => {
            const items = rows.filter(r => group.ids.includes(r.id))
            return items.length > 0 && <section className="setup-group" key={group.title} aria-label={group.title}>
              <h3>{group.title}</h3>
              <ul className="setup-list" aria-label={`${group.title} setup status`}>
                {items.map(r => <li key={r.id}>
                  <span className="setup-main"><strong>{r.label}</strong>{r.detail && <span className="muted small">{r.detail}</span>}</span>
                  <StatusBadge tone={r.tone}>{r.state}</StatusBadge>
                  <SectionLink section={r.section} onOpen={onOpen}>{r.label}</SectionLink>
                </li>)}
              </ul>
            </section>
          })}
        </div>
        <aside className="overview-context" aria-label="Department context">
          <h3>At a glance</h3>
          <dl><div><dt>Ticket prefix</dt><dd>{d.key}</dd></div><div><dt>Status</dt><dd><StatusBadge tone={d.isActive ? 'ok' : 'off'}>{d.isActive ? 'Active' : 'Inactive'}</StatusBadge></dd></div><div><dt>Your access</dt><dd>{d.myRole ?? 'Administrative metadata'}</dd></div></dl>
          <p className="muted small">Department membership controls access to tickets and service data. Teams route and assign work within this department.</p>
        </aside>
      </div>
    </section>
  )
}

function SectionLink({ section, onOpen, children }: { section: string; onOpen: (s: string) => void; children: ReactNode }) {
  return (
    <button type="button" className="ghost" onClick={() => onOpen(section)} aria-label={`Open ${children}`}>
      Open →
    </button>
  )
}
