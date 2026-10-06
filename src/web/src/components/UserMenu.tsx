import { useEffect, useId, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useMe } from '../auth'

export default function UserMenu({ onSignOut, onHelp }: { onSignOut: () => void; onHelp: () => void }) {
  const me = useMe()
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panelId = useId()

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false)
        trigger.current?.focus()
      }
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const initials = me.displayName
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p[0]!.toUpperCase())
    .join('')

  return (
    <div className="usermenu" ref={root}>
      <button
        ref={trigger}
        type="button"
        className="usermenu-trigger"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-label="Account menu"
        onClick={() => setOpen(!open)}
      >
        <span className="avatar" aria-hidden="true">
          {initials || '?'}
        </span>
        <span className="usermenu-name">{me.displayName}</span>
      </button>
      {open && (
        <div id={panelId} className="popover" role="group" aria-label="Account">
          <p className="muted small">{me.email ?? me.displayName}</p>
          {me.isPlatformAdmin && <p className="small">Platform administrator</p>}
          <Link to="/portal" onClick={() => setOpen(false)}>
            Requester portal
          </Link>
          <Link to="/responses" onClick={() => setOpen(false)}>
            My canned responses
          </Link>
          <button type="button" onClick={onHelp}>
            Keyboard shortcuts
          </button>
          <button type="button" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      )}
    </div>
  )
}
