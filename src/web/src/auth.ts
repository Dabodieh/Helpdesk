import { createContext, useContext } from 'react'
import type { Me } from './api/schemas'

export const MeContext = createContext<Me | null>(null)

/** The signed-in user. Only valid below RequireAuth. */
export function useMe(): Me {
  const me = useContext(MeContext)
  if (!me) throw new Error('useMe must be used inside RequireAuth')
  return me
}
