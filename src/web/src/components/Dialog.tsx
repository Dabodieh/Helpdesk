import { useEffect, useId, useRef, type ReactNode } from 'react'

/** Minimal modal: Esc closes, focus moves in and returns to the opener, Tab is kept inside. */
export default function Dialog({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const closeRef = useRef(onClose)
  useEffect(() => {
    closeRef.current = onClose
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
        closeRef.current()
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
    <div className="dialog-backdrop">
      <div ref={ref} className="dialog" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="dialog-head">
          <h2 id={titleId}>{title}</h2>
          <button type="button" className="ghost" onClick={onClose} aria-label="Close dialog">
            Close
          </button>
        </div>
        {children}
      </div>
    </div>
  )
}
