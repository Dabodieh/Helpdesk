// DEV / DEMO DATA ONLY. Seeds ONE self-contained demonstration department ("IT Support (Demo)", key DEMO) in a LOCAL Development API.
// Everything it creates is isolated to that department and to the demo-* sign-in users; the existing IT Support department is never touched.
// Uses dev sign-in only (the API must run in Development with Authentication:DevSignIn:Enabled, mail provider Fake: no real mail, no Graph calls).
//
//   node scripts/dev-demo-seed.mjs [apiBase=http://localhost:5080] [webBase=http://localhost:5173]
//
// Safe to rerun: the department, members, SLA setup, mailbox, forms and knowledge are looked up before they are created, and the tickets are only
// created when the department has none. To start over: docker exec -i helpdesk-postgres-1 psql -U helpdesk -d helpdesk < scripts/dev-demo-reset.sql
import { execFileSync } from 'node:child_process'

const base = process.argv[2] ?? 'http://localhost:5080'
const web = process.argv[3] ?? 'http://localhost:5173'
const MAILBOX = 'demo-desk@helpdesk.test'
const TAG = '[DEMO]'

class Session {
  constructor(subject, name) {
    this.subject = subject
    this.name = name
    this.cookies = new Map()
    this.csrf = null
  }
  async request(method, path, body) {
    const headers = { cookie: [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ') }
    if (body !== undefined) headers['content-type'] = 'application/json'
    if (method !== 'GET' && this.csrf) headers[this.csrf.headerName] = this.csrf.token
    const res = await fetch(base + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual' })
    for (const c of res.headers.getSetCookie?.() ?? []) {
      const [pair] = c.split(';')
      const i = pair.indexOf('=')
      this.cookies.set(pair.slice(0, i), pair.slice(i + 1))
    }
    const text = await res.text()
    return { status: res.status, json: text && (text.startsWith('{') || text.startsWith('[')) ? JSON.parse(text) : null, text }
  }
  async signIn() {
    const q = new URLSearchParams({ subject: this.subject, name: this.name, email: `${this.subject}@example.test`, returnUrl: '/' })
    await this.request('GET', `/api/auth/dev-login?${q}`)
    this.csrf = (await this.request('GET', '/api/csrf')).json
    this.me = (await this.request('GET', '/api/me')).json
    return this
  }
  async ok(method, path, body) {
    const r = await this.request(method, path, body)
    if (r.status >= 300) throw new Error(`${method} ${path} -> ${r.status} ${r.text}`)
    return r.json
  }
}

const psql = (sql) =>
  execFileSync('docker', ['exec', '-i', 'helpdesk-postgres-1', 'psql', '-U', 'helpdesk', '-d', 'helpdesk', '-v', 'ON_ERROR_STOP=1', '-tA', '-c', sql], { encoding: 'utf8' }).trim()
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ---- people (dev sign-in creates them on first use; demo-* only) ----
const platform = await new Session('dev-admin', 'Dev Admin').signIn()
const admin = await new Session('demo-admin', 'Demo Admin').signIn()
const lead = await new Session('demo-lead', 'Demo Lead').signIn()
const agent1 = await new Session('demo-agent1', 'Demo Agent One').signIn()
const agent2 = await new Session('demo-agent2', 'Demo Agent Two').signIn()
const req1 = await new Session('demo-requester1', 'Priya Shah').signIn()
const req2 = await new Session('demo-requester2', 'Marcus Webb').signIn()
const legacy = await new Session('demo-legacy', 'Dana Whitfield').signIn()

// ---- department, memberships ----
const all = await platform.ok('GET', '/api/departments')
const dept = all.find((d) => d.key === 'DEMO') ?? (await platform.ok('POST', '/api/departments', { key: 'DEMO', name: 'IT Support (Demo)', description: 'DEV/DEMO DATA: safe to delete (scripts/dev-demo-reset.sql).' }))
const D = `/api/departments/${dept.id}`
for (const [s, role] of [[admin, 'DepartmentAdmin'], [lead, 'TeamLead'], [agent1, 'Agent'], [agent2, 'Agent']]) {
  await platform.ok('PUT', `${D}/members/${s.me.id}`, { role })
}

// ---- service levels (must exist before tickets): Urgent is overdue a minute after seeding, High is due soon for ~3h, the rest are comfortable ----
if ((await admin.ok('GET', `${D}/sla-policies`)).length === 0) {
  const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map((day) => ({ day, intervals: [{ start: '00:00', end: '23:59' }] }))
  const hours = await admin.ok('POST', `${D}/business-hours`, { name: 'Demo: every day', timeZone: 'Europe/London', exceptions: [], weekly: week })
  const policy = (name, first, resolution, priority) =>
    admin.ok('POST', `${D}/sla-policies`, {
      name, calendarId: hours.id, firstResponseMinutes: first, resolutionMinutes: resolution, pauseWhenPending: false, approachingLeadMinutes: 30,
      conditions: priority ? [{ field: 'priority', operator: 'in', values: [priority] }] : [],
    })
  await policy('Demo Urgent (1 min)', null, 1, 'Urgent')
  await policy('Demo High (about 3h50)', null, 230, 'High')
  await policy('Demo Standard (8h)', null, 480, null)
}

// ---- mailbox (in-process simulator: nothing is sent anywhere) ----
if ((await admin.ok('GET', `${D}/mailboxes`)).length === 0) {
  await admin.ok('POST', `${D}/mailboxes`, { address: MAILBOX, displayName: 'IT Service Desk (Demo)', isDefault: true })
}

// ---- request forms: one for every employee, one restricted to a single person ----
const forms = await admin.ok('GET', `${D}/request-forms`)
const form = async (name, body) => forms.find((f) => f.name === name) ?? (await admin.ok('POST', `${D}/request-forms`, { name, ...body }))
const open = await form(`${TAG} Report an IT problem`, { description: 'Open to every signed-in employee.', audience: 'AllEmployees' })
await form(`${TAG} Request privileged access`, { description: 'Restricted: only Priya Shah (demo-requester1) can see and submit this.', audience: 'Selected', audienceUserIds: [req1.me.id] })

// ---- knowledge ----
const articles = await admin.ok('GET', `${D}/knowledge/articles?page=1`).catch(() => ({ items: [] }))
if (!(articles.items ?? []).some((a) => a.title?.startsWith(TAG))) {
  const article = await admin.ok('POST', `${D}/knowledge/articles`, {
    title: `${TAG} Reset your VPN token`, summary: 'Demo article.', body: 'If the VPN rejects your code: open the Company Portal, choose Devices, then Re-register this device and sign in again.',
  })
  await admin.ok('POST', `${D}/knowledge/articles/${article.id}/publish`, { version: article.version })
}

// ---- tickets (only once) ----
const any = await admin.ok('GET', `/api/tickets?queue=all&departmentId=${dept.id}&status=Open&status=Pending&status=Resolved&status=Closed&limit=1`)
if (any.items.length > 0) {
  console.log('Demo tickets already present; leaving them as they are.')
} else {
  const ticket = (s, subject, description, priority, requester, extra = {}) =>
    s.ok('POST', `${D}/tickets`, { subject: `${TAG} ${subject}`, description, priority, requester, ...extra })
  const post = (s, t, kind, body, extra = {}) => s.ok('POST', `/api/tickets/${t.id}/messages`, { kind, body, ...extra })
  const dispatch = () => platform.ok('POST', '/api/dev/mail/dispatch')

  // Requester history: Priya (linked, three requests) and Marcus (one), submitted through the portal like real employees.
  const portal = (s, subject, description) => s.ok('POST', '/api/portal/requests', { formId: open.id, subject: `${TAG} ${subject}`, description, submissionId: crypto.randomUUID() })
  await portal(req1, 'Laptop will not wake from sleep', 'It needs a hard reset every time I close the lid.')
  await portal(req1, 'Need a second monitor adapter', 'USB-C to HDMI please.')
  await portal(req1, 'Teams camera not detected', 'The built-in camera disappeared after the last update.')
  await portal(req2, 'Cannot open the shared finance drive', 'Access denied since this morning.')

  // Core scenarios.
  const normal = await ticket(lead, 'Outlook keeps asking for my password', 'Prompts every few minutes since the weekend. Webmail works.', 'Normal', { email: 'priya.shah@contoso.test', displayName: 'Priya Shah (contoso)' }, { assigneeUserId: agent1.me.id })
  const overdue = await ticket(lead, 'Server room temperature alert', 'Rack 4 reports 31C and the aircon shows an error light.', 'Urgent', { email: 'facilities@contoso.test', displayName: 'Facilities Desk' }, { assigneeUserId: agent2.me.id })
  const dueSoon = await ticket(lead, 'VPN drops every ten minutes', 'Home broadband, Windows 11. Started this morning.', 'High', { email: 'tom.hargreaves@contoso.test', displayName: 'Tom Hargreaves' }, { assigneeUserId: agent2.me.id })
  const unassigned = await ticket(lead, 'Request: install Visio on my laptop', 'Needed for the network diagrams project.', 'Low', { email: 'aisha.khan@contoso.test', displayName: 'Aisha Khan' })
  const duplicate = await ticket(lead, 'Duplicate: Outlook password prompts', 'Same issue as my colleague: Outlook keeps asking for a password.', 'Normal', { email: 'liam.oconnor@contoso.test', displayName: 'Liam O\'Connor' }, { teamId: desk.id })

  // Agent public reply, Internal Notes, an @mention, a follower case.
  await post(agent1, normal, 'Public', 'Hi Priya, we have reset your credential cache. Please sign out of Outlook and back in and tell us if the prompt returns.', { status: 'Pending' })
  await post(agent2, overdue, 'Internal', 'Engineer on site. Non-critical workloads moved off rack 4. Update the requester once the aircon is fixed.')
  await post(lead, normal, 'Internal', `@Demo Agent One can you check whether this is the stale token issue we saw last week?`, { mentionUserIds: [agent1.me.id] })
  await lead.ok('POST', `/api/tickets/${dueSoon.id}/followers/me`)
  await post(agent2, dueSoon, 'Public', 'Hi Tom, we are checking the VPN gateway logs for your device and will come back to you shortly.')

  // Related and merged tickets.
  await agent1.ok('POST', `/api/tickets/${normal.id}/links`, { ticketId: unassigned.id, type: 'Related' })
  const canonical = await agent1.ok('GET', `/api/tickets/${normal.id}`)
  const source = await agent1.ok('GET', `/api/tickets/${duplicate.id}`)
  await agent1.ok('POST', `/api/tickets/${normal.id}/merge`, { version: canonical.version, sources: [{ ticketId: duplicate.id, version: source.version }] })

  // Email-born tickets: a requester reply, a failed send and an uncertain send (the outbound rows are marked directly: nothing is ever sent for real).
  const inbound = (from, fromName, subject, bodyText) => platform.ok('POST', '/api/dev/mail/inbound', { mailbox: MAILBOX, from, fromName, subject: `${TAG} ${subject}`, bodyText })
  const find = async (subject) => (await admin.ok('GET', `/api/tickets?queue=all&departmentId=${dept.id}&status=Open&status=Pending&q=${encodeURIComponent(`${TAG} ${subject}`)}&limit=5`)).items[0]
  const assign = async (s, ref, who) => {
    const t = await s.ok('GET', `/api/tickets/${ref.id}`)
    await s.ok('PUT', `/api/tickets/${ref.id}/assignment`, { version: t.version, assigneeUserId: who.me.id })
  }
  await inbound('ravi.menon@contoso.test', 'Ravi Menon', 'Shared drive access request', 'Hello, please give me access to the Finance-Reports share. Thanks, Ravi')
  const reply = await find('Shared drive access request')
  await assign(lead, reply, agent1)
  await post(agent1, reply, 'Public', 'Hi Ravi, can you confirm which folder you need and who approved it?')
  await dispatch()
  await sleep(1500)
  const sent = await platform.ok('GET', `/api/dev/mail/sent?mailbox=${encodeURIComponent(MAILBOX)}`)
  const draft = sent.find((s) => s.subject.includes('Shared drive access request'))
  if (draft) {
    await platform.ok('POST', '/api/dev/mail/customer-reply', { mailbox: MAILBOX, draftId: draft.draftId, from: 'ravi.menon@contoso.test', fromName: 'Ravi Menon', bodyText: 'The Finance-Reports folder, approved by Dana in Finance. Thank you.' })
  }

  const broken = async (subject, from, fromName, state, error, category) => {
    await inbound(from, fromName, subject, 'Hello, could you help with this please?')
    const t = await find(subject)
    await assign(lead, t, agent2)
    await post(agent2, t, 'Public', 'Hi, we are looking into this for you.')
    for (let i = 0; i < 20 && psql(`select count(*) from mail.messages where ticket_id = '${t.id}' and direction = 'Outbound' and state = 'Sent'`) === '0'; i++) {
      await dispatch()
      await sleep(1000)
    }
    psql(`update mail.messages set state = '${state}', last_error = '${error}', delivery_reported_at = now(), updated_at = now() where ticket_id = '${t.id}' and direction = 'Outbound'`)
    psql(`update ticketing.messages set delivery_state = 'Failed', delivery_error = '${category}' where ticket_id = '${t.id}' and delivery_state in ('Pending','Sent')`)
  }
  await broken('Cannot print the quarterly report', 'dana.reports@contoso.test', 'Dana Reports', 'Failed', 'PermanentProviderException:graph_403', 'permission_denied')
  await broken('Projector in room 2 shows no signal', 'ops.team@contoso.test', 'Ops Team', 'Uncertain', 'outcome_uncertain', 'outcome_uncertain')

  // An email requester that already has history, has not been linked to the account of the same person, and so needs an administrator.
  await inbound('demo-legacy@example.test', 'Dana Whitfield', 'Old laptop battery replacement', 'My laptop battery barely lasts an hour.')
  await inbound('demo-legacy@example.test', 'Dana Whitfield', 'Badge reader not working at the side door', 'The side door reader rejects my badge.')
  console.log('Demo tickets created.')
}

// ---- how to sign in ----
const url = (s, returnUrl) =>
  `${web}/api/auth/dev-login?${new URLSearchParams({ subject: s.subject, name: s.name, email: `${s.subject}@example.test`, returnUrl })}`
console.log(`\nDEMO department: ${dept.name} (${dept.key})  id ${dept.id}\n`)
const list = [
  ['Department Admin', admin, `/admin/departments/${dept.id}`],
  ['Team Lead (also sees the dashboard)', lead, `/dashboard/${dept.id}`],
  ['Agent 1', agent1, '/queue/my'],
  ['Agent 2', agent2, '/queue/my'],
  ['Requester Priya Shah (linked, restricted form)', req1, '/portal'],
  ['Requester Marcus Webb', req2, '/portal'],
  ['Requester Dana Whitfield (unlinked legacy history: submitting is blocked until resolved)', legacy, '/portal'],
]
for (const [label, s, returnUrl] of list) console.log(`${label}\n  ${url(s, returnUrl)}`)
