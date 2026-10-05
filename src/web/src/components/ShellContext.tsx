import { createContext, useContext } from 'react'

export type ShellApi = { openNewTicket: () => void; openHelp: () => void }

export const ShellContext = createContext<ShellApi>({ openNewTicket: () => {}, openHelp: () => {} })
export const useShell = () => useContext(ShellContext)
