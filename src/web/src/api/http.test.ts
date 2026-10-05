import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { csrfHandler, json, mockFetch, problem } from '../test-utils'
import { describeError, isVersionConflict } from './errors'
import { api, ApiError, resetCsrf, setUnauthorizedHandler } from './http'

beforeEach(() => resetCsrf())
afterEach(() => {
  vi.unstubAllGlobals()
  setUnauthorizedHandler(null)
})

test('unsafe requests send the CSRF header; GET does not fetch a token', async () => {
  const calls = mockFetch({ ...csrfHandler, 'GET /api/x': () => ({}), 'POST /api/x': () => json({}, 200) })
  await api('GET', '/api/x')
  expect(calls.map((c) => c.path)).toEqual(['/api/x'])
  await api('POST', '/api/x', { body: { a: 1 } })
  const post = calls.find((c) => c.method === 'POST')!
  expect(post.headers['X-CSRF-TOKEN']).toBe('tok')
  expect(post.body).toEqual({ a: 1 })
})

test('a 400 CSRF failure refetches the token and retries once', async () => {
  let n = 0
  const calls = mockFetch({
    'GET /api/csrf': () => ({ headerName: 'X-CSRF-TOKEN', token: `t${++n}` }),
    'POST /api/x': [() => problem(400, 'Bad Request', { code: 'csrf_invalid' }), () => json({})],
  })
  await api('POST', '/api/x')
  const posts = calls.filter((c) => c.method === 'POST')
  expect(posts.map((p) => p.headers['X-CSRF-TOKEN'])).toEqual(['t1', 't2'])
})

test('a validation 400 is not retried and exposes the errors map', async () => {
  const calls = mockFetch({
    ...csrfHandler,
    'POST /api/x': () => problem(400, 'Validation failed', { errors: { name: ['Name is required.'] } }),
  })
  const err = await api('POST', '/api/x').catch((e: unknown) => e)
  expect(err).toBeInstanceOf(ApiError)
  expect((err as ApiError).errors).toEqual({ name: ['Name is required.'] })
  expect(describeError(err)).toContain('Name is required.')
  expect(calls.filter((c) => c.method === 'POST')).toHaveLength(1)
})

test('401 calls the unauthorized handler', async () => {
  mockFetch({ 'GET /api/x': () => problem(401, 'Unauthorized') })
  const handler = vi.fn()
  setUnauthorizedHandler(handler)
  await expect(api('GET', '/api/x')).rejects.toMatchObject({ status: 401 })
  expect(handler).toHaveBeenCalledOnce()
})

test('403, 404 and structured 409 codes map to distinct messages; 404 does not reveal existence', () => {
  const m403 = describeError(new ApiError(403, 'Forbidden'))
  const m404 = describeError(new ApiError(404, 'Not Found'))
  expect(m403).not.toBe(m404)
  expect(m404).toMatch(/not found, or you do not have access/i)
  const conflict = new ApiError(409, 'Conflict', 'any wording', undefined, 'version_conflict')
  expect(isVersionConflict(conflict)).toBe(true)
  expect(describeError(conflict)).toMatch(/changed by someone else/)
  // message text is irrelevant: only the code decides
  expect(isVersionConflict(new ApiError(409, 'Conflict', 'changed by someone else, reload'))).toBe(false)
  expect(describeError(new ApiError(409, 'Conflict', 'A team with this name already exists.', undefined, 'duplicate'))).toBe(
    'A team with this name already exists.',
  )
  expect(describeError(new ApiError(409, 'Conflict', undefined, undefined, 'last_admin'))).toMatch(/last administrator/)
  expect(describeError(new ApiError(409, 'Conflict', undefined, undefined, 'inactive'))).toMatch(/inactive/)
  expect(describeError(new ApiError(409, 'Conflict', 'Public replies are not allowed on a closed ticket.', undefined, 'invalid_state'))).toBe(
    'Public replies are not allowed on a closed ticket.',
  )
})

test('the problem code is parsed from the response body', async () => {
  mockFetch({ 'GET /api/x': () => problem(409, 'Conflict', { code: 'version_conflict' }) })
  await expect(api('GET', '/api/x')).rejects.toMatchObject({ status: 409, code: 'version_conflict' })
})

test('responses are validated with zod', async () => {
  const { meSchema } = await import('./schemas')
  mockFetch({ 'GET /api/me': () => ({ nope: true }) })
  await expect(api('GET', '/api/me', { schema: meSchema })).rejects.toMatchObject({ status: 0 })
})
