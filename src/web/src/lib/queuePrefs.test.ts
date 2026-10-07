import { expect, test } from 'vitest'
import { ticketListPath } from '../api/tickets'
import { visibleColumns } from '../hooks/useQueuePrefs'
import { describeFilter, parseFilters, toViewFilter, viewLocation } from './filters'

const parse = (qs: string) => parseFilters('all', new URLSearchParams(qs), 'queue', 'me-id')

test('the extra queue filters parse from the URL and reach the list request', () => {
  const f = parse('team=none&send=failed&type=ty1&sla=awaitingresponse')
  expect(f).toMatchObject({ noTeam: true, teamId: undefined, send: 'failed', typeId: 'ty1', sla: 'awaitingresponse' })
  const path = ticketListPath(f, null)
  expect(path).toContain('noTeam=true')
  expect(path).toContain('send=failed')
  expect(path).toContain('typeId=ty1')
  expect(path).not.toContain('teamId=')
  expect(parse('send=sideways').send).toBeUndefined() // unknown values are ignored
  expect(parse('team=t1')).toMatchObject({ teamId: 't1', noTeam: undefined })
})

test('a saved view keeps the new filters and reopens as the same queue URL', () => {
  const view = { id: 'v1', filter: toViewFilter(parse('team=none&send=uncertain&type=ty1')) }
  expect(view.filter).toMatchObject({ noTeam: true, send: 'uncertain', typeId: 'ty1' })
  const url = new URL(viewLocation(view), 'http://x')
  expect(url.searchParams.get('team')).toBe('none')
  expect(url.searchParams.get('send')).toBe('uncertain')
  expect(url.searchParams.get('type')).toBe('ty1')
  expect(describeFilter(view)).toContain('No team')
})

test('saved columns keep their order, unknown keys are ignored, and no choice means the standard set', () => {
  expect(visibleColumns(undefined)).toEqual(['requester', 'status', 'priority', 'sla', 'team', 'assignee', 'updated'])
  expect(visibleColumns({ columns: null })).toHaveLength(7)
  expect(visibleColumns({ columns: ['updated', 'ghost', 'status'] })).toEqual(['updated', 'status'])
  expect(visibleColumns({ columns: [] })).toEqual([])
})
