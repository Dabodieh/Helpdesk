import { useCallback, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { readPref, writePref } from './prefs'

export const SPLIT_KEY = 'helpdesk.queueWidth'
export const SPLIT_MIN = 360
export const SPLIT_DEFAULT = 520
const TICKET_MIN = 460

/** Clamp to sensible bounds: the queue never shrinks below 360px and never leaves the ticket less than 460px. */
export function clampWidth(width: number, container: number) {
  const max = Math.max(SPLIT_MIN, container > 0 ? container - TICKET_MIN : 900)
  return Math.round(Math.min(max, Math.max(SPLIT_MIN, width)))
}

/** Drag/keyboard-resizable queue width, remembered locally. */
export function useSplit(containerRef: React.RefObject<HTMLElement | null>) {
  const [width, setWidth] = useState(() => {
    const stored = Number(readPref(SPLIT_KEY))
    return Number.isFinite(stored) && stored > 0 ? stored : SPLIT_DEFAULT
  })
  const dragging = useRef(false)

  const bounds = () => containerRef.current?.getBoundingClientRect().width ?? 0
  const commit = useCallback((w: number) => {
    setWidth(w)
    writePref(SPLIT_KEY, String(w))
  }, [])

  const onPointerDown = (e: ReactPointerEvent<HTMLElement>) => {
    dragging.current = true
    e.currentTarget.setPointerCapture?.(e.pointerId)
    e.preventDefault()
  }
  const onPointerMove = (e: ReactPointerEvent<HTMLElement>) => {
    if (!dragging.current) return
    const left = containerRef.current?.getBoundingClientRect().left ?? 0
    setWidth(clampWidth(e.clientX - left, bounds()))
  }
  const onPointerUp = (e: ReactPointerEvent<HTMLElement>) => {
    if (!dragging.current) return
    dragging.current = false
    e.currentTarget.releasePointerCapture?.(e.pointerId)
    const left = containerRef.current?.getBoundingClientRect().left ?? 0
    commit(clampWidth(e.clientX - left, bounds()))
  }
  const onKeyDown = (e: ReactKeyboardEvent<HTMLElement>) => {
    const step = e.shiftKey ? 80 : 24
    if (e.key === 'ArrowLeft') commit(clampWidth(width - step, bounds()))
    else if (e.key === 'ArrowRight') commit(clampWidth(width + step, bounds()))
    else if (e.key === 'Home') commit(SPLIT_MIN)
    else if (e.key === 'End') commit(clampWidth(10_000, bounds()))
    else if (e.key === 'Enter' || e.key === ' ') commit(SPLIT_DEFAULT)
    else return
    e.preventDefault()
  }

  // Keep the stored width valid when the window shrinks.
  const [effective, setEffective] = useState(width)
  useEffect(() => {
    const update = () => setEffective(clampWidth(width, bounds()))
    update()
    window.addEventListener('resize', update)
    return () => window.removeEventListener('resize', update)
  }, [width])

  return { width: effective, handlers: { onPointerDown, onPointerMove, onPointerUp, onKeyDown } }
}
