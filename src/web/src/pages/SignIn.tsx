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
    <main className="page narrow">
      <h1>Helpdesk</h1>
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
      {import.meta.env.DEV && (
        <p className="muted">
          Development: when dev sign-in is enabled on the API, open{' '}
          <code>/api/auth/dev-login?subject=dev-admin&amp;name=Dev%20Admin&amp;email=dev@example.test&amp;returnUrl=/</code>{' '}
          to get a session (see docs/DEVELOPMENT.md).
        </p>
      )}
    </main>
  )
}
