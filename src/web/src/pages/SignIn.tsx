import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { getAuthProviders } from '../api/entra'

const AUTH_ERRORS: Record<string, string> = {
  failed: 'Sign-in failed. Please try again.',
  inactive: 'Your account is not active. Contact an administrator.',
  unavailable: 'Microsoft sign-in is temporarily unavailable. Try again in a few minutes; if it continues, contact an administrator.',
}

export default function SignIn({ returnUrl = '/' }: { returnUrl?: string }) {
  const [params] = useSearchParams()
  const authError = AUTH_ERRORS[params.get('authError') ?? '']
  const reference = (params.get('ref') ?? '').match(/^[A-Za-z0-9:_-]{1,64}$/)?.[0]
  // returnUrl must be a local path
  const safe = returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/'
  // Only an explicit "not set up" hides the button: if the answer is unknown the button stays.
  const providers = useQuery({ queryKey: ['auth-providers'], queryFn: getAuthProviders, retry: false, staleTime: 30_000 })
  const notSetUp = providers.data?.entra === false
  return (
    <main className="page narrow sign-in">
      <h1 className="wordmark">helpdesk<span>.</span></h1>
      {authError && (
        <p role="alert" className="notice error">
          {authError}
          {reference && <span className="muted"> Reference: {reference}</span>}
        </p>
      )}
      {notSetUp ? (
        <p role="status" className="notice warn">
          Microsoft sign-in has not been set up yet. An administrator needs to connect it.
        </p>
      ) : (
        <>
          <p>Sign in with your organisation account to continue.</p>
          <p>
            <a className="button primary" href={`/api/auth/login?returnUrl=${encodeURIComponent(safe)}`}>
              Sign in
            </a>
          </p>
        </>
      )}
    </main>
  )
}
