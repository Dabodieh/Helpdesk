import { useQueryClient } from '@tanstack/react-query'
import { useMemo, useState, type ReactNode } from 'react'
import { Link, NavLink, useNavigate } from 'react-router-dom'
import { canAdminister, hasAnyPermission } from '../api/permissions'
import { logout } from '../api/organisation'
import { useMe } from '../auth'
import { clearDrafts } from '../lib/drafts'
import { useShortcuts } from '../lib/shortcuts'
import { useQueueCounts } from '../queries'
import { endSession } from '../queryClient'
import { Icon } from './icons'
import NewTicketDialog from './NewTicketDialog'
import ShortcutHelp from './ShortcutHelp'
import { ShellContext } from './ShellContext'
import UserMenu from './UserMenu'

const QUEUE_NAV = [
  { to: '/queue/my', label: 'My Queue', icon: 'inbox', count: 'my' },
  { to: '/queue/team', label: 'Team Queue', icon: 'team', count: 'team' },
  { to: '/queue/unassigned', label: 'Unassigned', icon: 'unassigned', count: 'unassigned' },
  { to: '/queue/all', label: 'All Tickets', icon: 'all', count: 'all' },
] as const

/** Narrow left navigation + content. Only modules that exist are listed. */
export default function AppShell({ children }: { children: ReactNode }) {
  const me = useMe()
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [dialog, setDialog] = useState<'ticket' | 'help' | null>(null)
  const hasTickets = hasAnyPermission(me, 'tickets.read')
  const canCreate = hasAnyPermission(me, 'tickets.create')
  const counts = useQueueCounts(hasTickets)

  const api = useMemo(
    () => ({
      openNewTicket: () => {
        if (canCreate) setDialog('ticket')
      },
      openHelp: () => setDialog('help'),
    }),
    [canCreate],
  )

  useShortcuts({ '?': api.openHelp, c: api.openNewTicket }, dialog === null)

  async function signOut() {
    try {
      await logout()
    } finally {
      clearDrafts()
      endSession(qc)
    }
  }

  return (
    <ShellContext.Provider value={api}>
      <div className="shell">
        <aside className="rail">
          <Link to="/" className="brand">
            Helpdesk
          </Link>
          {canCreate && (
            <button type="button" className="primary new-ticket" onClick={() => setDialog('ticket')}>
              <Icon name="plus" /> New ticket
            </button>
          )}
          <nav aria-label="Main">
            {hasTickets && (
              <ul>
                {QUEUE_NAV.map((n) => (
                  <li key={n.to}>
                    <NavLink to={n.to}>
                      <Icon name={n.icon} />
                      <span className="nav-label">{n.label}</span>
                      {counts.data && (
                        <span className="count" aria-label={`${counts.data[n.count]} open`}>
                          {counts.data[n.count]}
                        </span>
                      )}
                    </NavLink>
                  </li>
                ))}
                <li>
                  <NavLink to="/search">
                    <Icon name="search" />
                    <span className="nav-label">Search</span>
                  </NavLink>
                </li>
              </ul>
            )}
            <ul className="rail-bottom">
              {canAdminister(me) && (
                <li>
                  <NavLink to="/admin">
                    <Icon name="admin" />
                    <span className="nav-label">Administration</span>
                  </NavLink>
                </li>
              )}
            </ul>
          </nav>
          <UserMenu onSignOut={() => void signOut()} onHelp={() => setDialog('help')} />
        </aside>
        <main className="main">{children}</main>
        {dialog === 'help' && <ShortcutHelp onClose={() => setDialog(null)} />}
        {dialog === 'ticket' && (
          <NewTicketDialog
            onClose={() => setDialog(null)}
            onCreated={(t) => {
              setDialog(null)
              void navigate(`/tickets/${encodeURIComponent(t.reference)}`)
            }}
          />
        )}
      </div>
    </ShellContext.Provider>
  )
}
