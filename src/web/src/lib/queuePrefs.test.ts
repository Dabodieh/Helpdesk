import { expect, test } from 'vitest'
import { ticketListPath } from '../api/tickets'
import { visibleColumns } from '../hooks/useQueuePrefs'
import { describeFilter, parseFilters, toViewFilter, viewLocation } from './filters'

const parse = (qs: string) => parseFilters('all', new URLSearchParams(qs), 'queue', 'me-id')

test('the extra queue filters parse from the URL and reach the list request', () => {
  const f = parse('send=failed&type=ty1&sla=awaitingresponse')
  expect(f).toMatchObject({ send: 'failed', typeId: 'ty1', sla: 'awaitingresponse' })
  const path = ticketListPath(f, null)
  expect(path).toContain('send=failed')
  expect(path).toContain('typeId=ty1')
  expect(parse('send=sideways').send).toBeUndefined() // unknown values are ignored
  expect(ticketListPath(parse('team=t1'), null)).not.toContain('team') // there is no team filter any more: the parameter is ignored
})

test('a saved view keeps the new filters and reopens as the same queue URL', () => {
  const view = { id: 'v1', filter: toViewFilter(parse('send=uncertain&type=ty1')) }
  expect(view.filter).toMatchObject({ send: 'uncertain', typeId: 'ty1' })
  const url = new URL(viewLocation(view), 'http://x')
  expect(url.searchParams.has('team')).toBe(false)
  expect(url.searchParams.get('send')).toBe('uncertain')
  expect(url.searchParams.get('type')).toBe('ty1')
  expect(describeFilter(view)).toContain('Send uncertain')
})

test('saved columns keep their order, unknown keys are ignored, and no choice means the standard set', () => {
  expect(visibleColumns(undefined)).toEqual(['requester', 'status', 'priority', 'sla', 'assignee', 'updated'])
  expect(visibleColumns({ columns: null })).toHaveLength(6)
  expect(visibleColumns({ columns: ['updated', 'ghost', 'status'] })).toEqual(['updated', 'status'])
  expect(visibleColumns({ columns: [] })).toEqual([])
})
