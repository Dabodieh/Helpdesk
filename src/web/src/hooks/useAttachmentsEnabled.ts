import { useQuery } from '@tanstack/react-query'
import { getPortalCapabilities } from '../api/portal'

/** Hide file inputs until the server confirms uploads are enabled, including on errors. */
export function useAttachmentsEnabled() {
  const q = useQuery({ queryKey: ['portal', 'capabilities'], queryFn: getPortalCapabilities, staleTime: 30_000, refetchInterval: 30_000 })
  return q.data?.attachmentsEnabled === true && !q.isError
}
