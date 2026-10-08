import { useEffect } from 'react'
import { usePref } from '../lib/prefs'

export default function ThemeToggle() {
  const [theme, setTheme] = usePref('helpdesk.theme', ['dark', 'light'] as const, 'dark')
  useEffect(() => { document.documentElement.dataset.theme = theme }, [theme])
  const next = theme === 'dark' ? 'light' : 'dark'
  return <button type="button" className="rail-link theme-toggle" aria-label={`Switch to ${next} theme`} title={`Switch to ${next} theme`} onClick={() => setTheme(next)}>
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true">
      {theme === 'dark' ? <><circle cx="12" cy="12" r="4" /><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5" /></> : <path d="M20 15.3A9 9 0 0 1 8.7 4a9 9 0 1 0 11.3 11.3Z" />}
    </svg>
  </button>
}
