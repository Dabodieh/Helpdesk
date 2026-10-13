import { z } from 'zod'
import { csrfSchema } from './schemas'

export class ApiError extends Error {
  status: number
  title: string
  detail?: string
  errors?: Record<string, string[]>
  /** Structured problem code (e.g. version_conflict, csrf_invalid); see docs/api/PHASE2_API.md. */
  code?: string
  constructor(status: number, title: string, detail?: string, errors?: Record<string, string[]>, code?: string) {
    super(detail ?? title)
    this.status = status
    this.title = title
    this.detail = detail
    this.errors = errors
    this.code = code
  }
}

type Csrf = { headerName: string; token: string }
let csrf: Csrf | null = null
/** The fetch in flight: every caller that needs a token meanwhile shares it (a second GET would replace the antiforgery cookie and invalidate the first token). */
let csrfPending: Promise<Csrf> | null = null
let unauthorized: (() => void) | null = null

/** Forget the cached CSRF token (the token is bound to the signed-in user). */
export function resetCsrf() {
  csrf = null
  csrfPending = null
}

/** Register a callback invoked when a request (other than the /api/me probe) returns 401. */
export function setUnauthorizedHandler(fn: (() => void) | null) {
  unauthorized = fn
}

async function parse<T>(res: Response, schema?: z.ZodType<T>): Promise<T> {
  if (res.status === 204 || !schema) return undefined as T
  try {
    return schema.parse(await res.json())
  } catch {
    throw new ApiError(0, 'Unexpected response', 'The server returned data in an unexpected format.')
  }
}

async function toError(res: Response): Promise<ApiError> {
  let body: { title?: unknown; detail?: unknown; errors?: unknown; code?: unknown } = {}
  try {
    body = await res.json()
  } catch {
    /* no problem-details body */
  }
  const errors = z.record(z.string(), z.array(z.string())).safeParse(body.errors)
  return new ApiError(
    res.status,
    typeof body.title === 'string' ? body.title : res.statusText || 'Request failed',
    typeof body.detail === 'string' ? body.detail : undefined,
    errors.success ? errors.data : undefined,
    typeof body.code === 'string' ? body.code : undefined,
  )
}

function isCsrfFailure(e: ApiError) {
  return e.code === 'csrf_invalid'
}

async function getCsrf(): Promise<Csrf> {
  if (csrf) return csrf
  csrfPending ??= (async () => {
    const res = await fetch('/api/csrf', { credentials: 'same-origin' })
    if (!res.ok) throw await toError(res)
    return await parse(res, csrfSchema)
  })()
  const pending = csrfPending
  try {
    csrf = await pending
    return csrf
  } finally {
    if (csrfPending === pending) csrfPending = null
  }
}

/** Headers the realtime connection needs for its (unsafe) negotiate request. */
export async function csrfHeaders(): Promise<Record<string, string>> {
  const { headerName, token } = await getCsrf()
  return { [headerName]: token }
}

const UNSAFE = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

export async function api<T = void>(
  method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE',
  path: string,
  opts: { body?: unknown; schema?: z.ZodType<T>; raw?: Blob } = {},
): Promise<T> {
  const unsafe = UNSAFE.has(method)
  for (let attempt = 0; ; attempt++) {
    const headers: Record<string, string> = { Accept: 'application/json' }
    if (opts.raw) headers['Content-Type'] = opts.raw.type || 'application/octet-stream'
    else if (opts.body !== undefined) headers['Content-Type'] = 'application/json'
    if (unsafe) {
      const token = await getCsrf()
      headers[token.headerName] = token.token
    }
    let res: Response
    try {
      res = await fetch(path, {
        method,
        headers,
        credentials: 'same-origin',
        body: opts.raw ?? (opts.body === undefined ? undefined : JSON.stringify(opts.body)),
      })
    } catch {
      throw new ApiError(0, 'Network error', 'The server could not be reached.')
    }
    if (res.ok) return parse(res, opts.schema)
    const error = await toError(res)
    if (unsafe && attempt === 0 && isCsrfFailure(error)) {
      resetCsrf()
      continue
    }
    if (error.status === 401 && path !== '/api/me') unauthorized?.()
    throw error
  }
}
