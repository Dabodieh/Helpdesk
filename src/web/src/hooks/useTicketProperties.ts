import { useQuery } from '@tanstack/react-query'
import { getTicketProperties } from '../api/ticketing'
import { livePolling } from '../queries'

export const propertiesKey = (reference: string) => ['ticket', reference, 'properties']

/** Type and configured field values of one ticket (cached; shared by the properties panel and the edit dialog). */
export function useTicketProperties(reference: string) {
  return useQuery({ queryKey: propertiesKey(reference), queryFn: () => getTicketProperties(reference), ...livePolling })
}
