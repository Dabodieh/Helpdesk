import { useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'
import type { TicketDetail } from '../api/ticketSchemas'

/** Put a fresh server ticket into the cache (every mutation returns the new state) and refresh dependent views. */
export function useApplyTicket(ref: string) {
  const qc = useQueryClient()
  return useCallback(
    (t: TicketDetail) => {
      qc.setQueryData(['ticket', ref], t)
      qc.setQueryData(['ticket', t.id], t)
      void qc.invalidateQueries({ queryKey: ['tickets'] })
      void qc.invalidateQueries({ queryKey: ['ticket', t.id, 'activity'] })
      void qc.invalidateQueries({ queryKey: ['ticket', t.id, 'messages'] })
    },
    [qc, ref],
  )
}
