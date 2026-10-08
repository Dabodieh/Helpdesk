import { useEffect, useId, useRef, type ReactNode } from 'react'

/** Minimal modal: Esc closes, focus moves in and returns to the opener, Tab is kept inside. */
export default function Dialog({ title, onClose, children, variant, dirty = false }: { title: string; onClose: () => void; children: ReactNode; variant?: 'drawer'; dirty?: boolean }) {
  const ref = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const closeRef = useRef(onClose)
  // Esc and Close ask before throwing away typed work (UX-008); programmatic closes after a save go through onClose directly and are never asked.
  const requestClose = () => {
    if (dirty && !window.confirm('Discard what you have typed?')) return
    onClose()
  }
  const requestRef = useRef(requestClose)
  useEffect(() => {
    closeRef.current = onClose
    requestRef.current = requestClose
  })

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    const root = ref.current!
    const focusables = () =>
      Array.from(
        root.querySelectorAll<HTMLElement>('a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled])'),
      )
    const first = root.querySelector<HTMLElement>('[data-autofocus]') ?? focusables()[0]
    first?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        e.stopPropagation()
        requestRef.current()
      } else if (e.key === 'Tab') {
        const f = focusables()
        if (f.length === 0) return
        const a = document.activeElement
        if (e.shiftKey && a === f[0]) {
          e.preventDefault()
          f[f.length - 1].focus()
        } else if (!e.shiftKey && a === f[f.length - 1]) {
          e.preventDefault()
          f[0].focus()
        }
      }
    }
    document.addEventListener('keydown', onKey, true)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      opener?.focus?.()
    }
  }, [])

  return (
    <div className={variant === 'drawer' ? 'dialog-backdrop drawer-backdrop' : 'dialog-backdrop'}>
      <div ref={ref} className={variant === 'drawer' ? 'dialog drawer' : 'dialog'} role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="dialog-head">
          <h2 id={titleId}>{title}</h2>
          <button type="button" className="ghost" onClick={requestClose} aria-label="Close dialog">
            Close
          </button>
        </div>
        {children}
      </div>
    </div>
  )
}
