import { describeError, isVersionConflict } from '../api/errors'

/** Mutation failure banner. A version conflict offers a reload that keeps the user's draft text. */
export default function ProblemBanner({
  error,
  onReload,
  onDismiss,
}: {
  error: unknown
  onReload?: () => void
  onDismiss?: () => void
}) {
  if (!error) return <div role="alert" />
  const conflict = isVersionConflict(error)
  return (
    <div role="alert" className="notice error banner">
      <span>
        {conflict
          ? 'Someone else changed this ticket. Reload to see the latest version; anything you typed is kept.'
          : describeError(error)}
      </span>
      <span className="banner-actions">
        {conflict && onReload && (
          <button type="button" onClick={onReload}>
            Reload latest
          </button>
        )}
        {onDismiss && (
          <button type="button" className="ghost" onClick={onDismiss}>
            Dismiss
          </button>
        )}
      </span>
    </div>
  )
}
