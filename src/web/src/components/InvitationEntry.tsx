import { useEffect, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { saveInvitationToken } from '../lib/invitationToken'
import RequireAuth from './RequireAuth'

/** Keep the secret in this tab through external sign-in, never in its return URL. */
export default function InvitationEntry() {
  const location = useLocation()
  const { token: legacyToken } = useParams()
  const navigate = useNavigate()
  const incoming = location.hash.slice(1) || legacyToken
  const [storageError, setStorageError] = useState(false)

  useEffect(() => {
    try {
      if (incoming) saveInvitationToken(incoming)
      if (incoming || location.search) navigate('/activate', { replace: true })
    } catch {
      // oxlint-disable-next-line react/set-state-in-effect -- Report a failed external storage write before allowing sign-in.
      setStorageError(true)
    }
  }, [incoming, location.search, navigate])

  if (storageError) return <main className="page"><p role="alert">Allow this site's session storage, then reopen your invitation link to sign in securely.</p></main>
  // Do not mount sign-in until history replacement has removed the secret from the return path.
  if (incoming || location.search) return null
  return <RequireAuth layout="portal" />
}
