import { useQuery } from '@tanstack/react-query'
import { fetchHealth } from '../api/health'

export default function StatusPage() {
  const { data, isPending, isError } = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => fetchHealth('/health/ready'),
  })
  return (
    <section aria-labelledby="status-heading">
      <h1 id="status-heading">System status</h1>
      <p role="status">{isPending ? 'Checking…' : isError ? 'API unreachable' : `API: ${data.status}`}</p>
    </section>
  )
}
