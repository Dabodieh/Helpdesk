import { useQuery } from '@tanstack/react-query'
import { Navigate } from 'react-router-dom'
import { canAdminister, hasAnyPermission } from '../api/permissions'
import { QUEUES } from '../api/ticketSchemas'
import { listViews } from '../api/views'
import { useMe } from '../auth'
import { Loading } from '../components/common'
import { useQueuePrefs } from '../hooks/useQueuePrefs'
import { viewLocation } from '../lib/filters'

/** Agents land on their chosen default queue or saved view, and fall back to My Queue when it is unavailable. */
function AgentLanding() {
  const { prefs, isPending } = useQueuePrefs()
  const viewId = prefs?.defaultViewId
  const views = useQuery({ queryKey: ['views'], queryFn: listViews, enabled: !!viewId, retry: false })
  if (isPending || (viewId && views.isPending)) return <Loading label="Opening your queue" />
  const view = viewId ? views.data?.find((v) => v.id === viewId) : undefined
  if (view) return <Navigate to={viewLocation(view)} replace />
  const queue = (QUEUES as readonly string[]).includes(prefs?.defaultQueue ?? '') ? prefs!.defaultQueue : 'my'
  return <Navigate to={`/queue/${queue}`} replace />
}

/** `/`: agents land on their default queue (My Queue unless chosen), administrators on Administration, everybody else (employees) on the requester portal. */
export default function Landing() {
  const me = useMe()
  if (hasAnyPermission(me, 'tickets.read')) return <AgentLanding />
  if (canAdminister(me)) return <Navigate to="/admin" replace />
  // Everyone else is an employee: the requester portal is their home.
  return <Navigate to="/portal" replace />
}
