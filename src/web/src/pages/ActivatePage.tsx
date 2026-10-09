import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { acceptInvitation, previewInvitation } from '../api/invitations'
import { ROLE_LABEL } from '../api/schemas'
import { ApiError } from '../api/http'
import { ErrorNotice, Loading } from '../components/common'
import { useMe } from '../auth'

/** The page behind the emailed activation link. Reached after signing in with Microsoft; nothing is granted until the person presses the button. */
export default function ActivatePage() {
  const { token = '' } = useParams()
  const me = useMe()
  const qc = useQueryClient()
  const preview = useQuery({ queryKey: ['invitation', token], queryFn: () => previewInvitation(token), retry: false })
  const accept = useMutation({
    mutationFn: () => acceptInvitation(token),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['me'] }),
  })

  if (preview.isPending) return <Loading label="Checking your invitation" />
  if (preview.isError) {
    const gone = preview.error instanceof ApiError && preview.error.status === 404
    return (
      <div className="page narrow">
        <h1>Activate your account</h1>
        {gone ? <p className="notice warn">This link is not valid. It may have been used already, replaced by a newer one or expired. Ask your administrator to send a new invitation.</p> : <ErrorNotice error={preview.error} onRetry={() => void preview.refetch()} />}
      </div>
    )
  }

  if (accept.isSuccess) {
    return (
      <div className="page narrow">
        <h1>Your account is active</h1>
        <p role="status">
          {accept.data.alreadyMember ? `You already belong to ${accept.data.departmentName}.` : `You have been added to ${accept.data.departmentName} as ${ROLE_LABEL[accept.data.role] ?? accept.data.role}.`}
        </p>
        <p>
          <Link className="button primary" to="/">
            Open Helpdesk
          </Link>
        </p>
      </div>
    )
  }

  return (
    <div className="page narrow">
      <h1>Activate your account</h1>
      <p>
        Hello {preview.data.displayName}. You have been invited to <strong>{preview.data.departmentName}</strong> as <strong>{ROLE_LABEL[preview.data.role] ?? preview.data.role}</strong>.
      </p>
      <p className="muted small">Signed in as {me.email ?? me.displayName}.</p>
      <ErrorNotice error={accept.error} />
      <button type="button" className="primary" disabled={accept.isPending} onClick={() => accept.mutate()}>
        {accept.isPending ? 'Activating…' : 'Activate my account'}
      </button>
    </div>
  )
}
