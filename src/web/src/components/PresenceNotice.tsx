import type { PresenceUser } from '../lib/realtime'

const TEXT: Record<PresenceUser['state'], (name: string) => string> = {
  viewing: (n) => `${n} is viewing this ticket`,
  reply: (n) => `${n} is writing a reply`,
  note: (n) => `${n} is writing an internal note`,
}

/**
 * Who else is on this ticket right now. Advisory only (nothing here blocks or locks anything), a state and a name and nothing about what is being written.
 * The server already left out the caller and anything the caller may not know (note composing is only reported to people who may read notes).
 */
export default function PresenceNotice({ users }: { users: PresenceUser[] }) {
  if (users.length === 0) return null
  return (
    <p className="presence small muted" role="status" aria-live="polite">
      <span className="presence-dot" aria-hidden="true" />
      {users.map((u) => TEXT[u.state](u.displayName)).join(' · ')}
    </p>
  )
}
