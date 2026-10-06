import { Navigate } from 'react-router-dom'
import { canAdminister, hasAnyPermission } from '../api/permissions'
import { useMe } from '../auth'

/** `/`: agents land on My Queue, administrators on Administration, everybody else (employees) on the requester portal. */
export default function Landing() {
  const me = useMe()
  if (hasAnyPermission(me, 'tickets.read')) return <Navigate to="/queue/my" replace />
  if (canAdminister(me)) return <Navigate to="/admin" replace />
  // Everyone else is an employee: the requester portal is their home.
  return <Navigate to="/portal" replace />
}
