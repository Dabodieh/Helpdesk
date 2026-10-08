import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState, type ReactNode } from 'react'
import { Link, NavLink, useLocation, useNavigate } from 'react-router-dom'
import { canAdminister, hasAnyPermission } from '../api/permissions'
import { logout } from '../api/organisation'
import { listViews } from '../api/views'
import { useMe } from '../auth'
import { clearDrafts } from '../lib/drafts'
import { viewLocation } from '../lib/filters'
import { useRealtime, useRealtimeStatus } from '../lib/realtime'
import { useShortcuts } from '../lib/shortcuts'
import { useQueueCounts } from '../queries'
import { endSession } from '../queryClient'
import { Icon } from './icons'
import NewTicketDialog from './NewTicketDialog'
import NotificationBell from './NotificationBell'
import ShortcutHelp from './ShortcutHelp'
import { ShellContext } from './ShellContext'
import UserMenu from './UserMenu'
import ThemeToggle from './ThemeToggle'

const QUEUE_NAV = [
  { to: '/queue/my', label: 'My Queue', icon: 'inbox', count: 'my' },
  { to: '/queue/team', label: 'Team Queue', icon: 'team', count: 'team' },
  { to: '/queue/unassigned', label: 'Unassigned', icon: 'unassigned', count: 'unassigned' },
  { to: '/queue/all', label: 'All Tickets', icon: 'all', count: 'all' },
  { to: '/queue/following', label: 'Following', icon: 'star', count: 'following' },
] as const

const LIVE_TEXT = {
  off: '',
  connecting: 'Connecting…',
  live: 'Live updates on',
  reconnecting: 'Reconnecting…',
  offline: 'Live updates off · refreshing every 30 s',
} as const

/** Narrow left navigation + content. Only modules that exist are listed. */
/** The top-bar search: type a reference, subject or requester and press Enter; the results open in the search workspace. */
function GlobalSearch() {
  const navigate = useNavigate()
  const [text, setText] = useState('')
  return (
    <form
      className="global-search"
      role="search"
      onSubmit={(e) => {
        e.preventDefault()
        const q = text.trim()
        void navigate(q ? `/search?q=${encodeURIComponent(q)}` : '/search')
        setText('')
      }}
    >
      <Icon name="search" />
      <input type="search" aria-label="Search all tickets" placeholder="Search tickets" value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
    </form>
  )
}

const NAV_COLLAPSED_KEY = 'helpdesk.nav.collapsed'
const RAIL_HIDDEN_KEY = 'helpdesk.rail.hidden'

/** A yes/no choice remembered in this browser (a private window simply does not remember it). */
function usePersistentFlag(key: string): [boolean, () => void] {
  const [on, setOn] = useState(() => {
    try {
      return window.localStorage.getItem(key) === '1'
    } catch {
      return false
    }
  })
  const toggle = () =>
    setOn((v) => {
      try {
        window.localStorage.setItem(key, v ? '0' : '1')
      } catch {
        /* not remembered */
      }
      return !v
    })
  return [on, toggle]
}

export default function AppShell({ children }: { children: ReactNode }) {
  const me = useMe()
  const qc = useQueryClient()
  const navigate = useNavigate()
  const location = useLocation()
  const [dialog, setDialog] = useState<'ticket' | 'help' | null>(null)
  const [collapsed, toggleNav] = usePersistentFlag(NAV_COLLAPSED_KEY)
  const [railHidden, toggleRail] = usePersistentFlag(RAIL_HIDDEN_KEY)
  const hasTickets = hasAnyPermission(me, 'tickets.read')
  const canCreate = hasAnyPermission(me, 'tickets.create')
  const counts = useQueueCounts(hasTickets)
  const views = useQuery({ queryKey: ['views'], queryFn: listViews, enabled: hasTickets, staleTime: 60_000 })
  const pinned = (views.data ?? []).filter((v) => v.isPinned)
  const inWorkspace = /^\/(queue|tickets|search)(\/|$)/.test(location.pathname)
  const activeView = new URLSearchParams(location.search).get('view')
  useRealtime(hasTickets, me.id)
  const live = useRealtimeStatus()

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
      <div className={`shell${inWorkspace ? ' shell-workspace' : ''}${inWorkspace && collapsed ? ' nav-collapsed' : ''}${railHidden ? ' rail-hidden' : ''}`}>
        <a className="skip-link" href="#main-content">Skip to content</a>
        <aside className="app-rail" aria-label="Application">
          <Link to="/" className="graphite-brand" aria-label="Helpdesk home"><span /><span /><span /></Link>
          <nav aria-label="Main" className="app-rail-nav">
            {hasTickets && <>
              <NavLink to="/dashboard" className="rail-link" aria-label="Dashboard" title="Dashboard"><Icon name="views" /></NavLink>
              <NavLink to="/queue/my" className={() => `rail-link${inWorkspace ? ' active' : ''}`} aria-label="Workspace" title="Workspace"><Icon name="inbox" /></NavLink>
            </>}
            <NavLink to="/portal/knowledge" className="rail-link" aria-label="Knowledge" title="Knowledge"><Icon name="file" /></NavLink>
          </nav>
          <div className="app-rail-foot">
            {canAdminister(me) && <NavLink to="/admin" className="rail-link" aria-label="Administration" title="Administration"><Icon name="admin" /></NavLink>}
            <ThemeToggle />
            <button type="button" className="rail-link" aria-label="Keyboard shortcuts" title="Keyboard shortcuts" onClick={() => setDialog('help')}><Icon name="help" /></button>
            <UserMenu onSignOut={() => void signOut()} onHelp={() => setDialog('help')} />
          </div>
        </aside>
        <header className="app-topbar">
          <button type="button" className="topbar-menu" onClick={toggleRail} aria-expanded={!railHidden} aria-label={railHidden ? 'Show navigation' : 'Hide navigation'} title={railHidden ? 'Show navigation' : 'Hide navigation'}><Icon name="density" /></button>
          <Link to="/" className="wordmark">helpdesk<span>.</span></Link>
          <span className="topbar-context">{location.pathname.startsWith('/admin') ? 'Administration' : 'Agent workspace'}</span>
          {hasTickets && <GlobalSearch />}
          {live !== 'off' && <span className={`live live-${live}`} role="status"><span className="live-dot" aria-hidden="true" />{LIVE_TEXT[live]}</span>}
          {canCreate && <button type="button" className="primary" onClick={() => setDialog('ticket')}><Icon name="plus" /> New ticket</button>}
          {hasTickets && <NotificationBell />}
        </header>
        {inWorkspace && <aside className="rail workspace-nav">
          <button type="button" className="nav-collapse" onClick={toggleNav} aria-expanded={!collapsed} aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
            <span aria-hidden="true">{collapsed ? '»' : '«'}</span>
            <span className="nav-label">Collapse</span>
          </button>
          <nav aria-label="Queues">
            {hasTickets && (
              <>
                <h2 className="rail-heading">Workspace</h2>
                <ul>
                  <li>
                    <NavLink to="/dashboard" title="Dashboard">
                      <Icon name="views" />
                      <span className="nav-label">Dashboard</span>
                    </NavLink>
                  </li>
                  {QUEUE_NAV.map((n) => (
                    <li key={n.to}>
                      <NavLink to={n.to} title={n.label} className={() => (location.pathname.startsWith(n.to) && !activeView ? 'active' : undefined)}>
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
                  {counts.data && counts.data.slaBreached > 0 && (
                    <li>
                      <NavLink to="/queue/all?sla=breached" className={() => (location.search.includes('sla=breached') ? 'active' : undefined)}>
                        <span aria-hidden="true" className="nav-mark">
                          ▲
                        </span>
                        <span className="nav-label">SLA breached</span>
                        <span className="count" aria-label={`${counts.data.slaBreached} breached`}>
                          {counts.data.slaBreached}
                        </span>
                      </NavLink>
                    </li>
                  )}
                </ul>
                <h2 className="rail-heading">Tools</h2>
                <ul>
                  <li>
                    <NavLink to="/portal/knowledge" title="Knowledge">
                      <Icon name="file" />
                      <span className="nav-label">Knowledge</span>
                    </NavLink>
                  </li>
                  <li>
                    <NavLink to="/search" title="Search">
                      <Icon name="search" />
                      <span className="nav-label">Search</span>
                    </NavLink>
                  </li>
                </ul>
                {pinned.length > 0 && (
                  <>
                    <h2 className="rail-heading">Views</h2>
                    <ul aria-label="Pinned views">
                      {pinned.map((v) => (
                        <li key={v.id}>
                          <Link to={viewLocation(v)} className={v.id === activeView ? 'active' : undefined} aria-current={v.id === activeView ? 'page' : undefined}>
                            <Icon name="star" />
                            <span className="nav-label">{v.name}</span>
                          </Link>
                        </li>
                      ))}
                    </ul>
                  </>
                )}
              </>
            )}
          </nav>
        </aside>}
        <main id="main-content" tabIndex={-1} className="main">{children}</main>
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
