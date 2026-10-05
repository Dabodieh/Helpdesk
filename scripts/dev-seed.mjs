// Seeds a LOCAL development API with a realistic helpdesk: departments, teams, agents, requesters, tickets and conversations.
// Uses dev sign-in only (the API must run in Development/Testing with Authentication:DevSignIn:Enabled).
// Usage: node scripts/dev-seed.mjs [apiBase=http://localhost:5080]
const base = process.argv[2] ?? 'http://localhost:5080'

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
    return { status: res.status, json: text && text.startsWith('{') || text.startsWith('[') ? JSON.parse(text) : null, text }
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

const admin = await new Session('dev-admin', 'Dev Admin').signIn()
const people = {
  alice: await new Session('alice', 'Alice Nguyen').signIn(),
  bob: await new Session('bob', 'Bob Okafor').signIn(),
  carol: await new Session('carol', 'Carol Reyes').signIn(),
}

async function department(key, name) {
  const all = await admin.ok('GET', '/api/departments')
  return all.find((d) => d.key === key) ?? (await admin.ok('POST', '/api/departments', { key, name }))
}
const it = await department('IT', 'IT Support')
await department('HR', 'Human Resources')

async function team(name) {
  const teams = await admin.ok('GET', `/api/departments/${it.id}/teams`)
  return teams.find((t) => t.name === name) ?? (await admin.ok('POST', `/api/departments/${it.id}/teams`, { name }))
}
const serviceDesk = await team('Service Desk')
const network = await team('Network & Infrastructure')

const roles = { 'dev-admin': 'DepartmentAdmin', alice: 'Agent', bob: 'Agent', carol: 'TeamLead' }
const users = { 'dev-admin': admin, alice: people.alice, bob: people.bob, carol: people.carol }
for (const [subject, role] of Object.entries(roles)) {
  await admin.ok('PUT', `/api/departments/${it.id}/members/${users[subject].me.id}`, { role })
}
for (const [s, t] of [['alice', serviceDesk], ['bob', serviceDesk], ['carol', network], ['alice', network]]) {
  await admin.ok('PUT', `/api/departments/${it.id}/teams/${t.id}/members/${users[s].me.id}`)
}

const existing = await admin.ok('GET', `/api/tickets?departmentId=${it.id}&status=Open&status=Pending&status=Resolved&status=Closed&limit=1`)
if (existing.items.length > 0) {
  console.log('Tickets already present; leaving data as is.')
  process.exit(0)
}

const requesters = [
  ['Priya Shah', 'priya.shah@contoso.test'], ['Marcus Webb', 'marcus.webb@contoso.test'], ['Elena Petrova', 'elena.petrova@contoso.test'],
  ['Tom Hargreaves', 'tom.hargreaves@contoso.test'], ['Aisha Khan', 'aisha.khan@contoso.test'], ['Liam O\'Connor', 'liam.oconnor@contoso.test'],
]
const catalogue = [
  ['Cannot connect to VPN from home', 'Since this morning the VPN client says "connection timed out" after I enter my MFA code. Worked fine yesterday. Windows 11 laptop, home broadband.', 'High'],
  ['Printer on floor 3 jamming repeatedly', 'The Canon on floor 3 jams on every second job. Paper tray was refilled twice. Several of us are affected.', 'Normal'],
  ['New starter: laptop and accounts for Monday', 'Jordan Ellis starts Monday in Finance. Needs a laptop, M365 licence, shared drive access to Finance-Reports and the ERP login.', 'Normal'],
  ['Outlook keeps asking for password', 'Outlook prompts for my password every few minutes since the weekend. Webmail works. I already restarted twice.', 'Normal'],
  ['Server room temperature alert', 'Monitoring flagged rack 4 at 31C. Aircon unit B shows an error light. Needs urgent attention before the afternoon.', 'Urgent'],
  ['Request: second monitor for desk 14', 'Working with large spreadsheets all day; a second monitor would help a lot. Approved by my manager (see attached email).', 'Low'],
  ['Teams meeting audio echo in Boardroom', 'Every Teams call in the boardroom has a loud echo. Tried muting the laptop but the room speaker still loops.', 'High'],
  ['Shared mailbox access for accounts@', 'Please add me to the accounts@ shared mailbox with send-as rights. I cover for Dana while she is on leave.', 'Normal'],
  ['Laptop battery swelling', 'My laptop trackpad is lifting and the case is bulging at the bottom. I have switched it off and unplugged it.', 'Urgent'],
  ['WiFi dropping in meeting room B', 'WiFi drops every 10 minutes in meeting room B. Other rooms are fine. Happens on phones too.', 'High'],
  ['Password reset for contractor account', 'Our contractor Sam cannot log in; the account may have expired. Please extend until the end of the month.', 'Normal'],
  ['Excel crashes opening the budget workbook', 'Excel closes immediately when I open Budget-FY27.xlsx. Other files open fine. Repair did not help.', 'Normal'],
]
const agentsRotation = [people.alice, people.bob, people.carol]
const created = []
for (let i = 0; i < catalogue.length; i++) {
  const [subject, description, priority] = catalogue[i]
  const [displayName, email] = requesters[i % requesters.length]
  const t = await admin.ok('POST', `/api/departments/${it.id}/tickets`, {
    subject, description, priority, requester: { email, displayName },
    ...(i % 3 === 0 ? { teamId: network.id } : i % 3 === 1 ? { teamId: serviceDesk.id } : {}),
    ...(i % 4 !== 3 ? { assigneeUserId: agentsRotation[i % 3].me.id } : {}),
  })
  created.push(t)
}
async function reply(session, ticket, kind, body, status) {
  await session.ok('POST', `/api/tickets/${ticket.id}/messages`, { kind, body, ...(status ? { status } : {}) })
}
await reply(people.alice, created[0], 'Internal', 'Checked the VPN gateway logs: the user\'s token is being rejected, looks like a stale device certificate. Will re-issue.')
await reply(people.alice, created[0], 'Public', 'Hi Priya, thanks for the details. We think your device certificate has expired and are re-issuing it now. Please reboot once you see the Company Portal notification.', 'Pending')
await reply(people.bob, created[1], 'Internal', 'Parts ordered for the fuser unit, ETA Thursday.')
await reply(people.carol, created[4], 'Internal', 'Facilities engineer is on site. Moved non-critical workloads off rack 4 as a precaution.')
await reply(people.carol, created[4], 'Public', 'We are aware of the alert and an engineer is on site. We will update you within the hour.')
await reply(people.bob, created[8], 'Public', 'Please keep the laptop switched off and away from anything flammable. We are sending a replacement to your desk today.', 'Resolved')
await people.alice.ok('PATCH', `/api/tickets/${created[2].id}`, { version: created[2].version, priority: 'High' })
console.log(`Seeded ${created.length} IT tickets. Sign in as dev-admin, alice, bob or carol (api/auth/dev-login?subject=alice&name=Alice%20Nguyen).`)
