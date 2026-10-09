import { useSearchParams } from 'react-router-dom'

const AUTH_ERRORS: Record<string, string> = {
  failed: 'Sign-in failed. Please try again.',
  inactive: 'Your account is not active. Contact an administrator.',
}

export default function SignIn({ returnUrl = '/' }: { returnUrl?: string }) {
  const [params] = useSearchParams()
  const authError = AUTH_ERRORS[params.get('authError') ?? '']
  // returnUrl must be a local path
  const safe = returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/'
  return (
    <main className="page narrow sign-in">
      <h1 className="wordmark">helpdesk<span>.</span></h1>
      {authError && (
        <p role="alert" className="notice error">
          {authError}
        </p>
      )}
      <p>Sign in with your organisation account to continue.</p>
      <p>
        <a className="button primary" href={`/api/auth/login?returnUrl=${encodeURIComponent(safe)}`}>
          Sign in
        </a>
      </p>
    </main>
  )
}
