import { useQuery } from '@tanstack/react-query'
import { fetchHealth } from '../api/health'
import { PageHeader, StatusBadge } from '../components/common'
import { Icon } from '../components/icons'

export default function StatusPage() {
  const { data, isPending, isError, isFetching, refetch } = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => fetchHealth('/health/ready'),
  })
  return (
    <section aria-label="System status">
      <PageHeader title="System status" subtitle="Current application readiness." actions={<button type="button" disabled={isFetching} onClick={() => void refetch()}>{isFetching ? 'Checking…' : 'Refresh status'}</button>} />
      <div className="system-health">
        <span className="department-icon" aria-hidden="true"><Icon name="views" /></span>
        <div>
          <h2>Application API</h2>
          <p role="status">{isPending ? 'Checking…' : isError ? 'API unreachable' : `API: ${data.status}`}</p>
          {!isPending && <StatusBadge tone={isError || !data?.ok ? 'bad' : data.status === 'Healthy' ? 'ok' : 'warn'}>{isError ? 'Unavailable' : data?.status}</StatusBadge>}
        </div>
      </div>
    </section>
  )
}
