import { useId, useState, type ReactNode } from 'react'
import { describeError } from '../api/errors'

export function Loading({ label = 'Loading' }: { label?: string }) {
  return (
    <p role="status" className="muted">
      {label}…
    </p>
  )
}

/** Error banner; announced to assistive tech. Renders nothing without an error. */
export function ErrorNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <div role="alert" className={error ? 'notice error' : undefined}>
      {error ? (
        <>
          {describeError(error)}{' '}
          {onRetry && (
            <button type="button" onClick={onRetry}>
              Retry
            </button>
          )}
        </>
      ) : null}
    </div>
  )
}

/** Polite status message region (always mounted so announcements work). */
export function StatusMessage({ children }: { children?: ReactNode }) {
  return (
    <p role="status" className="ok">
      {children}
    </p>
  )
}

export function Field({
  label,
  error,
  hint,
  children,
}: {
  label: string
  error?: string
  hint?: string
  children: (props: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean }) => ReactNode
}) {
  const id = useId()
  const msgId = `${id}-msg`
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children({ id, 'aria-describedby': error || hint ? msgId : undefined, 'aria-invalid': error ? true : undefined })}
      {(error || hint) && (
        <span id={msgId} className={error ? 'field-error' : 'muted'}>
          {error ?? hint}
        </span>
      )}
    </div>
  )
}

export function ActiveBadge({ active }: { active: boolean }) {
  return <span className={active ? 'badge' : 'badge off'}>{active ? 'Active' : 'Inactive'}</span>
}

/** Page title block for administration screens: title, quiet subtitle and the primary actions on the right. */
export function PageHeader({ title, subtitle, actions, crumbs }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode; crumbs?: ReactNode }) {
  return (
    <header className="page-header">
      <div>
        {crumbs && <nav className="crumbs" aria-label="Breadcrumb">{crumbs}</nav>}
        <h1>{title}</h1>
        {subtitle && <p className="page-sub muted">{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </header>
  )
}

export type TabDef = { id: string; label: string; badge?: ReactNode; content: ReactNode; hidden?: boolean }

/** Accessible tabs (arrow keys move between tabs). Panels stay mounted only for the active tab. */
export function Tabs({ tabs, label }: { tabs: TabDef[]; label: string }) {
  const visible = tabs.filter((t) => !t.hidden)
  const [active, setActive] = useState(visible[0]?.id)
  const current = visible.find((t) => t.id === active) ?? visible[0]
  const uid = useId()
  return (
    <div className="tabs-block">
      <div
        className="tablist"
        role="tablist"
        aria-label={label}
        onKeyDown={(e) => {
          const i = visible.findIndex((t) => t.id === current?.id)
          const next = e.key === 'ArrowRight' ? i + 1 : e.key === 'ArrowLeft' ? i - 1 : e.key === 'Home' ? 0 : e.key === 'End' ? visible.length - 1 : null
          if (next === null) return
          e.preventDefault()
          const t = visible[(next + visible.length) % visible.length]!
          setActive(t.id)
          document.getElementById(`${uid}-tab-${t.id}`)?.focus()
        }}
      >
        {visible.map((t) => (
          <button
            key={t.id}
            id={`${uid}-tab-${t.id}`}
            type="button"
            role="tab"
            aria-selected={t.id === current?.id}
            aria-controls={`${uid}-panel-${t.id}`}
            tabIndex={t.id === current?.id ? 0 : -1}
            onClick={() => setActive(t.id)}
          >
            {t.label}
            {t.badge}
          </button>
        ))}
      </div>
      {current && (
        <div id={`${uid}-panel-${current.id}`} role="tabpanel" aria-labelledby={`${uid}-tab-${current.id}`} tabIndex={0} className="tabpanel">
          {current.content}
        </div>
      )}
    </div>
  )
}
