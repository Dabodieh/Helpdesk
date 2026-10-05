import { useId, type ReactNode } from 'react'
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
