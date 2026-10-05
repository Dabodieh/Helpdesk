import { useQuery } from '@tanstack/react-query'
import { Outlet, useLocation } from 'react-router-dom'
import { fetchMe } from '../api/organisation'
import { MeContext } from '../auth'
import { ErrorNotice, Loading } from './common'
import SignIn from '../pages/SignIn'
import AppShell from './AppShell'

/** Layout route: renders the shell and children only for a signed-in user, otherwise the sign-in screen. */
export default function RequireAuth() {
  const location = useLocation()
  const me = useQuery({ queryKey: ['me'], queryFn: fetchMe, staleTime: 60_000 })

  if (me.isPending) return <Loading label="Checking your session" />
  if (me.isError) {
    return (
      <main className="page">
        <ErrorNotice error={me.error} onRetry={() => void me.refetch()} />
      </main>
    )
  }
  if (me.data === null) return <SignIn returnUrl={location.pathname + location.search} />

  return (
    <MeContext.Provider value={me.data}>
      <AppShell>
        <Outlet />
      </AppShell>
    </MeContext.Provider>
  )
}
