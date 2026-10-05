import { Navigate } from 'react-router-dom'
import { canAdminister, hasAnyPermission } from '../api/permissions'
import { useMe } from '../auth'

/** `/`: agents land on My Queue; people without ticket access are sent to Administration (or told they have no access). */
export default function Landing() {
  const me = useMe()
  if (hasAnyPermission(me, 'tickets.read')) return <Navigate to="/queue/my" replace />
  if (canAdminister(me)) return <Navigate to="/admin" replace />
  return (
    <div className="page narrow">
      <h1>No access yet</h1>
      <p>
        Your account is signed in, but you are not a member of any department. Ask an administrator to add you to a
        department.
      </p>
    </div>
  )
}
