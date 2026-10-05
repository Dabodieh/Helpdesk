import type { Priority, Status } from '../api/ticketSchemas'

/** Status: dot + text (never colour alone). */
export function StatusPill({ status }: { status: Status }) {
  return (
    <span className={`pill status-${status.toLowerCase()}`}>
      <span className="dot" aria-hidden="true" />
      {status}
    </span>
  )
}

const BARS: Record<Priority, number> = { Low: 1, Normal: 2, High: 3, Urgent: 4 }

/** Priority: signal-bar glyph + text. */
export function PriorityMark({ priority }: { priority: Priority }) {
  return (
    <span className={`prio prio-${priority.toLowerCase()}`}>
      <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true" focusable="false">
        {[0, 1, 2, 3].map((i) => (
          <rect
            key={i}
            x={i * 3}
            y={9 - i * 3}
            width="2"
            height={3 + i * 3}
            rx="0.5"
            className={i < BARS[priority] ? 'on' : 'off'}
          />
        ))}
      </svg>
      {priority}
    </span>
  )
}
