import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { BrowserRouter, Link, Route, Routes } from 'react-router-dom'
import { fetchHealth } from './api/health'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } },
})

function StatusPage() {
  const { data, isPending, isError } = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => fetchHealth('/health/ready'),
  })
  return (
    <section aria-labelledby="status-heading">
      <h1 id="status-heading">System status</h1>
      <p role="status">
        {isPending ? 'Checking…' : isError ? 'API unreachable' : `API: ${data.status}`}
      </p>
    </section>
  )
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <nav aria-label="Main">
          <Link to="/">Status</Link>
        </nav>
        <main>
          <Routes>
            <Route path="/" element={<StatusPage />} />
          </Routes>
        </main>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
