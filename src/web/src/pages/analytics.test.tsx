import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { daysBetween, defaultGroup, formatValue, presetRange, toCsv } from '../lib/analytics'
import { agentMe, csrfHandler, mockFetch, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())
beforeEach(() => window.localStorage.clear())

const REPORT = {
  report: 'ticket-volume',
  period: { from: '2026-10-01', to: '2026-10-03', group: 'day', timeZone: 'Europe/London', generatedAt: '2026-10-03T10:00:00Z' },
  kpis: [
    { key: 'received', label: 'Received', value: 12, unit: 'count', previous: 9, drill: 'received', tone: 'neutral' },
    { key: 'resolved', label: 'Resolved', value: 7, unit: 'count', previous: 7, drill: 'resolved', tone: 'good' },
    { key: 'median', label: 'Median first response', value: 95, unit: 'minutes', tone: 'neutral' },
  ],
  series: [
    {
      key: 'volume', title: 'Tickets received and resolved', kind: 'bars', unit: 'count', bucketType: 'date', buckets: ['2026-10-01', '2026-10-02', '2026-10-03'],
      lines: [{ key: 'received', label: 'Received', values: [3, 5, 4], drill: 'received', tone: 'info' }, { key: 'resolved', label: 'Resolved', values: [2, 2, 3], drill: 'resolved', tone: 'good' }],
    },
  ],
  tables: [{ key: 'buckets', title: 'Volume by day', columns: [{ key: 'period', label: 'Day', type: 'date' }, { key: 'received', label: 'Received', type: 'number' }], rows: [['2026-10-01', 3], ['2026-10-02', 5], ['2026-10-03', 4]] }],
  notes: ['Received counts every ticket raised in the period.'],
  unavailable: ['Breakdown by Department: a report covers one Department at a time.'],
}

const routes = () => ({
  ...workspaceRoutes(agentMe()),
  'GET /api/departments/d1/reports/ticket-volume': () => REPORT,
  'GET /api/departments/d1/reports/tickets': () => ({
    metric: 'received', from: '2026-10-01', to: '2026-10-03', total: 1, truncated: false,
    items: [{ id: 't9', reference: 'IT-9', subject: 'Printer jam', status: 'Open', priority: 'High', assignee: null, createdAt: '2026-10-02T09:00:00Z', resolvedAt: null, ageDays: 1 }],
  }),
  ...csrfHandler,
})

test('the report library lists reports by category, searches them and remembers favourites', async () => {
  mockFetch(routes())
  renderApp('/analytics')
  expect(await screen.findByRole('heading', { name: 'Analytics', level: 1 })).toBeInTheDocument()
  for (const c of ['Ticketing', 'SLA', 'Productivity', 'Communication']) expect(screen.getByRole('heading', { name: c })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Agent workload/ })).toHaveAttribute('href', expect.stringContaining('/analytics/workload'))
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search reports' }), { target: { value: 'bounced' } })
  expect(screen.getByRole('link', { name: /Email delivery/ })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: /Backlog and ageing/ })).not.toBeInTheDocument()
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search reports' }), { target: { value: 'zzzz' } })
  expect(screen.getByText('No matching reports')).toBeInTheDocument()
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search reports' }), { target: { value: '' } })
  fireEvent.click(screen.getByRole('button', { name: /Add SLA performance to favourites/ }))
  expect(await screen.findByRole('heading', { name: 'Favourites' })).toBeInTheDocument()
})

test('a report shows its figures, chart, table and honest notes, and the filters change the request', async () => {
  const calls = mockFetch(routes())
  renderApp('/analytics/ticket-volume?dept=d1&from=2026-10-01&to=2026-10-03&group=day')
  expect(await screen.findByRole('heading', { name: 'Ticket volume trends', level: 1 })).toBeInTheDocument()
  const kpis = await screen.findByRole('list', { name: 'Key figures' })
  expect(within(kpis).getByText('12')).toBeInTheDocument()
  expect(within(kpis).getByText('1.6 h')).toBeInTheDocument() // 95 minutes
  expect(within(kpis).getByText(/\+3 \(\+33%\) vs previous period/)).toBeInTheDocument()
  expect(screen.getByRole('img', { name: /Tickets received and resolved/ })).toBeInTheDocument()
  expect(screen.getByRole('table')).toBeInTheDocument()
  expect(screen.getByText(/Not available:/)).toBeInTheDocument()
  expect(calls.some((c) => c.path.includes('/reports/ticket-volume') && c.path.includes('from=2026-10-01'))).toBe(true)

  fireEvent.change(screen.getByLabelText('Period'), { target: { value: 'today' } })
  await waitFor(() => expect(calls.filter((c) => c.path.includes('/reports/ticket-volume')).length).toBeGreaterThan(1))
  fireEvent.click(screen.getByText(/^Filters/))
  fireEvent.click(screen.getByLabelText('High'))
  await waitFor(() => expect(calls.some((c) => c.path.includes('/reports/ticket-volume') && c.path.includes('priority=High'))).toBe(true))
})

test('a figure opens the tickets behind it, for the same period and filters', async () => {
  const calls = mockFetch(routes())
  renderApp('/analytics/ticket-volume?dept=d1&from=2026-10-01&to=2026-10-03&group=day')
  fireEvent.click(await screen.findByRole('button', { name: /Received: 12\. Show the tickets/ }))
  expect(await screen.findByRole('link', { name: 'IT-9' })).toHaveAttribute('href', '/tickets/IT-9')
  expect(screen.getByText('Printer jam')).toBeInTheDocument()
  expect(calls.some((c) => c.path.includes('/reports/tickets') && c.path.includes('metric=received'))).toBe(true)
})

test('reporting helpers: presets, grouping, units and a CSV that cannot be turned into a formula', () => {
  expect(presetRange('7', '2026-10-09')).toEqual({ from: '2026-10-03', to: '2026-10-09' })
  expect(presetRange('lastmonth', '2026-10-09')).toEqual({ from: '2026-09-01', to: '2026-09-30' })
  expect(presetRange('month', '2026-10-09')).toEqual({ from: '2026-10-01', to: '2026-10-09' })
  expect(daysBetween('2026-10-01', '2026-10-03')).toBe(3)
  expect(defaultGroup('2026-10-01', '2026-10-09')).toBe('day')
  expect(defaultGroup('2026-06-01', '2026-10-09')).toBe('month')
  expect(formatValue(45, 'minutes')).toBe('45 min')
  expect(formatValue(120, 'minutes')).toBe('2 h')
  expect(formatValue(87.5, 'percent')).toBe('87.5%')
  expect(formatValue(null, 'count')).toBe('—')
  expect(toCsv([['a', 'b,c'], ['=SUM(A1)', 'say "hi"']])).toBe('a,"b,c"\r\n\'=SUM(A1),"say ""hi"""')
})

test('Analytics is offered only to people who can read tickets', async () => {
  mockFetch({ ...workspaceRoutes(agentMe()), ...csrfHandler })
  renderApp('/queue/my')
  expect(await screen.findByRole('link', { name: 'Analytics' })).toHaveAttribute('href', '/analytics')
})
