import Dialog from './Dialog'

/** Asks for the Composer to take focus (D24 "reply first"); the Composer of the open ticket listens. */
export const FOCUS_REPLY_EVENT = 'helpdesk:focus-reply'

/**
 * D24: resolving or closing without a public reply tells the requester nothing. The agent is warned and can write a reply first; resolving silently stays possible after
 * the warning. `count` is the number of tickets (bulk edits warn once).
 */
export default function SilentResolveDialog({
  status,
  count = 1,
  onReplyFirst,
  onConfirm,
  onClose,
}: {
  status: string
  count?: number
  onReplyFirst?: () => void
  onConfirm: () => void
  onClose: () => void
}) {
  const verb = status === 'Closed' ? 'close' : 'resolve'
  return (
    <Dialog title={`${verb[0]!.toUpperCase()}${verb.slice(1)} without a reply?`} onClose={onClose}>
      <div className="dialog-form">
        <p>
          {count === 1 ? 'The requester' : 'The requesters'} will not receive a message about this. Nothing is emailed when the status changes; only a public reply is sent.
        </p>
        <div className="dialog-actions">
          {onReplyFirst && (
            <button type="button" data-autofocus onClick={onReplyFirst}>
              Write a reply first
            </button>
          )}
          <button type="button" className="primary" data-autofocus={onReplyFirst ? undefined : true} onClick={onConfirm}>
            {count === 1 ? `${verb[0]!.toUpperCase()}${verb.slice(1)} without a message` : `${verb[0]!.toUpperCase()}${verb.slice(1)} ${count} tickets without a message`}
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </Dialog>
  )
}
