const storageKey = 'helpdesk.invitation'

export const saveInvitationToken = (token: string) => sessionStorage.setItem(storageKey, token)

export function readInvitationToken(): string {
  try {
    return sessionStorage.getItem(storageKey) ?? ''
  } catch {
    return ''
  }
}

export function clearInvitationToken(): void {
  try {
    sessionStorage.removeItem(storageKey)
  } catch {
    // Storage may have been disabled since opening the link. Do not turn a successful activation into an error.
  }
}
