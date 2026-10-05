import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, Outlet, useLocation } from 'react-router-dom'
import { fetchMe, logout } from '../api/organisation'
import { MeContext } from '../auth'
import { endSession } from '../queryClient'
import { ErrorNotice, Loading } from './common'
import SignIn from '../pages/SignIn'

/** Layout route: renders the shell and children only for a signed-in user, otherwise the sign-in screen. */
export default function RequireAuth() {
  const qc = useQueryClient()
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

  async function signOut() {
    try {
      await logout()
    } finally {
      endSession(qc)
    }
  }

  return (
    <MeContext.Provider value={me.data}>
      <header className="topbar">
        <Link to="/" className="brand">
          Helpdesk
        </Link>
        <nav aria-label="Main">
          <Link to="/">Departments</Link>
          <Link to="/status">Status</Link>
        </nav>
        <span className="who">
          {me.data.displayName}
          {me.data.isPlatformAdmin && <span className="badge"> Platform admin</span>}
        </span>
        <button type="button" onClick={() => void signOut()}>
          Sign out
        </button>
      </header>
      <main className="page">
        <Outlet />
      </main>
    </MeContext.Provider>
  )
}
