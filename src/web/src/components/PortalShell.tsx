import { useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Link, NavLink } from 'react-router-dom'
import { logout } from '../api/organisation'
import { canAdminister, hasAnyPermission } from '../api/permissions'
import { useMe } from '../auth'
import { useRealtime } from '../lib/realtime'
import { endSession } from '../queryClient'

/**
 * The requester portal frame: its own navigation and layout, deliberately not the agent workspace. It works on a phone (the navigation wraps,
 * targets are large) and exposes nothing of the agent or administration areas except a clear way back for people who also work tickets.
 */
export default function PortalShell({ children }: { children: ReactNode }) {
  const me = useMe()
  const qc = useQueryClient()
  const [open, setOpen] = useState(false)
  const isAgent = hasAnyPermission(me, 'tickets.read') || canAdminister(me)
  useRealtime(true, me.id)

  async function signOut() {
    try {
      await logout()
    } finally {
      endSession(qc)
    }
  }

  return (
    <div className="portal">
      <a href="#portal-main" className="skip-link">
        Skip to content
      </a>
      <header className="portal-header">
        <div className="portal-bar">
          <Link to="/portal" className="portal-brand">
            <span className="brand-mark" aria-hidden="true">
              H
            </span>
            Help &amp; support
          </Link>
          <div className="portal-account">
            <button type="button" className="ghost" aria-expanded={open} aria-controls="portal-account-menu" onClick={() => setOpen(!open)}>
              {me.displayName}
            </button>
            {open && (
              <div id="portal-account-menu" className="popover" role="group" aria-label="Account">
                <p className="muted small">{me.email ?? me.displayName}</p>
                {isAgent && (
                  <Link to="/" onClick={() => setOpen(false)}>
                    Switch to the agent workspace
                  </Link>
                )}
                <button type="button" onClick={() => void signOut()}>
                  Sign out
                </button>
              </div>
            )}
          </div>
        </div>
        <nav className="portal-nav" aria-label="Portal">
          <NavLink to="/portal" end>
            Home
          </NavLink>
          <NavLink to="/portal/submit">Submit a request</NavLink>
          <NavLink to="/portal/requests">My requests</NavLink>
          <NavLink to="/portal/knowledge">Knowledge</NavLink>
        </nav>
      </header>
      <main id="portal-main" className="portal-main" tabIndex={-1}>
        {children}
      </main>
    </div>
  )
}
