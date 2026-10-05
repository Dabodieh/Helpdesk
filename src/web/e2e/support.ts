import { request, type APIRequestContext } from '@playwright/test'

/** The Vite dev server proxies /api to the host, so cookies and CSRF behave as in production. */
export const BASE_URL = process.env.E2E_BASE_URL ?? 'http://localhost:5173'

export type TestUser = { subject: string; name: string; email: string }

export const devLoginPath = (u: TestUser, returnUrl = '/') =>
  `/api/auth/dev-login?subject=${encodeURIComponent(u.subject)}&name=${encodeURIComponent(u.name)}&email=${encodeURIComponent(u.email)}&returnUrl=${encodeURIComponent(returnUrl)}`

/** A signed-in API client (cookie session + CSRF) for seeding. */
export class Api {
  private constructor(
    private readonly ctx: APIRequestContext,
    private csrf: { headerName: string; token: string },
  ) {}

  static async signIn(user: TestUser): Promise<Api> {
    const ctx = await request.newContext({ baseURL: BASE_URL })
    const login = await ctx.get(devLoginPath(user))
    if (!login.ok()) throw new Error(`dev-login failed for ${user.subject}: ${login.status()} (is Authentication:DevSignIn enabled?)`)
    // The CSRF token is bound to the signed-in user: fetch it after sign-in.
    const csrf = (await (await ctx.get('/api/csrf')).json()) as { headerName: string; token: string }
    return new Api(ctx, csrf)
  }

  async get<T>(path: string): Promise<T> {
    const res = await this.ctx.get(path)
    if (!res.ok()) throw new Error(`GET ${path} -> ${res.status()} ${await res.text()}`)
    return (await res.json()) as T
  }

  async send<T = unknown>(method: 'POST' | 'PUT' | 'PATCH', path: string, data: unknown): Promise<T> {
    const res = await this.ctx.fetch(path, { method, data, headers: { [this.csrf.headerName]: this.csrf.token } })
    if (!res.ok()) throw new Error(`${method} ${path} -> ${res.status()} ${await res.text()}`)
    const text = await res.text()
    return (text ? JSON.parse(text) : undefined) as T
  }

  dispose() {
    return this.ctx.dispose()
  }
}

export type Seed = {
  actor: TestUser
  colleague: TestUser
  departmentId: string
  teamName: string
  first: { reference: string; subject: string; description: string }
  second: { reference: string; subject: string }
}

/**
 * Creates a fresh department (unique key), a team, two members (actor = TeamLead so assignment is allowed,
 * colleague = Agent) and two tickets assigned to the actor, all through the real API.
 * Needs the bootstrap platform admin `dev-admin` (see docs/DEVELOPMENT.md).
 */
export async function seed(): Promise<Seed> {
  const run = Date.now().toString(36)
  const key = `E${run.toUpperCase().slice(-6)}`
  const actor: TestUser = { subject: `e2e-lead-${run}`, name: `E2E Lead ${run}`, email: `lead-${run}@example.test` }
  const colleague: TestUser = { subject: `e2e-agent-${run}`, name: `E2E Colleague ${run}`, email: `agent-${run}@example.test` }
  const teamName = `E2E Team ${run}`

  // Users are provisioned on first sign-in; read their ids from /api/me.
  const [leadApi, agentApi] = [await Api.signIn(actor), await Api.signIn(colleague)]
  const leadId = (await leadApi.get<{ id: string }>('/api/me')).id
  const agentId = (await agentApi.get<{ id: string }>('/api/me')).id
  await agentApi.dispose()

  const admin = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  const dept = await admin.send<{ id: string }>('POST', '/api/departments', { key, name: `E2E ${run}` })
  await admin.send('POST', `/api/departments/${dept.id}/teams`, { name: teamName })
  await admin.send('PUT', `/api/departments/${dept.id}/members/${leadId}`, { role: 'TeamLead' })
  await admin.send('PUT', `/api/departments/${dept.id}/members/${agentId}`, { role: 'Agent' })
  await admin.dispose()

  // Re-read the session so /api/me lists the new membership, then create tickets as the lead.
  const lead = await Api.signIn(actor)
  const mk = async (subject: string, description: string) =>
    lead.send<{ reference: string }>('POST', `/api/departments/${dept.id}/tickets`, {
      subject,
      description,
      requester: { email: `requester-${run}@example.test`, displayName: 'E2E Requester' },
      priority: 'Normal',
      assigneeUserId: leadId,
    })
  const firstDescription = `Printer on floor 3 is jammed (${run})`
  const first = { subject: `E2E printer jam ${run}`, description: firstDescription }
  const second = { subject: `E2E VPN drops ${run}` }
  const t1 = await mk(first.subject, firstDescription)
  const t2 = await mk(second.subject, `VPN disconnects every hour (${run})`)
  await lead.dispose()

  return {
    actor,
    colleague,
    departmentId: dept.id,
    teamName,
    first: { ...first, reference: t1.reference },
    second: { ...second, reference: t2.reference },
  }
}
