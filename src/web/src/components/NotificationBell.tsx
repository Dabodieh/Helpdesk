import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { listNotifications, markAllNotificationsRead, markNotificationRead, type Notification } from '../api/notifications'
import { formatDateTime } from '../lib/format'
import { livePolling } from '../queries'
import { Icon } from './icons'

const TEXT: Record<Notification['type'], string> = {
  assigned: 'Assigned to you',
  requester_reply: 'Requester replied',
  followed_update: 'Update on a ticket you follow',
  mentioned: 'Mentioned you in a note on',
  sla_approaching: 'SLA target approaching on',
  sla_breached: 'SLA target breached on',
  unassigned: 'Ticket unassigned, its assignee was removed:',
}

/**
 * The in-app notification centre: a count and a short list. Notifications are persisted by the server; the socket only prompts a refetch (and polling is the
 * fallback), so nothing depends on the live connection. A click marks it read and opens the ticket through the normal authorised route.
 */
export default function NotificationBell() {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panelId = useId()
  const q = useQuery({ queryKey: ['notifications'], queryFn: listNotifications, ...livePolling })
  const refresh = () => qc.invalidateQueries({ queryKey: ['notifications'] })
  const read = useMutation({ mutationFn: markNotificationRead, onSettled: refresh })
  const readAll = useMutation({ mutationFn: markAllNotificationsRead, onSettled: refresh })

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

  const unread = q.data?.unreadCount ?? 0
  const items = q.data?.items ?? []
  return (
    <div className="notify" ref={root}>
      <button
        ref={trigger}
        type="button"
        className="notify-trigger"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'}
        onClick={() => setOpen(!open)}
      >
        <Icon name="bell" />
        <span className="nav-label">Notifications</span>
        {unread > 0 && (
          <span className="count notify-count" aria-hidden="true">
            {unread > 99 ? '99+' : unread}
          </span>
        )}
      </button>
      {open && (
        <div className="notify-panel" id={panelId} role="region" aria-label="Notifications">
          <div className="notify-head">
            <strong>Notifications</strong>
            <button type="button" className="ghost" disabled={unread === 0 || readAll.isPending} onClick={() => readAll.mutate()}>
              Mark all read
            </button>
          </div>
          {q.isPending && <p className="muted small" role="status">Loading notifications…</p>}
          {q.isError && <p className="field-error small">Notifications could not be loaded.</p>}
          {q.isSuccess && items.length === 0 && <p className="empty small">Nothing needs your attention. You are told when a ticket is assigned to you, a requester replies, or a ticket you follow changes.</p>}
          <ul className="notify-list">
            {items.map((n) => (
              <li key={n.id} className={n.read ? 'notify-item' : 'notify-item unread'}>
                <button
                  type="button"
                  className="notify-open"
                  onClick={() => {
                    if (!n.read) read.mutate(n.id)
                    setOpen(false)
                    void navigate(`/tickets/${encodeURIComponent(n.reference)}${n.messageId ? `#msg-${n.messageId}` : ''}`)
                  }}
                >
                  <span className="notify-title">
                    {!n.read && <span className="sr-only">Unread: </span>}
                    {TEXT[n.type]} <span className="ref">{n.reference}</span>
                  </span>
                  <span className="muted small notify-sub">
                    {n.subject} · {formatDateTime(n.createdAt)}
                  </span>
                </button>
                {!n.read && (
                  <button type="button" className="ghost small" aria-label={`Mark ${n.reference} read`} onClick={() => read.mutate(n.id)}>
                    Mark read
                  </button>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
