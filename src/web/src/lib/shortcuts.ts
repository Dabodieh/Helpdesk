import { useEffect, useRef } from 'react'

/** True when the key event comes from somewhere the user is typing, or carries a modifier that would collide with browser/OS shortcuts. */
export function shouldIgnoreShortcut(e: KeyboardEvent): boolean {
  if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || e.isComposing) return true
  const t = e.target
  if (!(t instanceof HTMLElement)) return false
  if (t.isContentEditable) return true
  const tag = t.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
}

/** True when the event target is a control that handles Enter/Space itself (so Enter must not also "open"). */
export function isActivatable(e: KeyboardEvent): boolean {
  const t = e.target
  return t instanceof HTMLElement && (t.tagName === 'A' || t.tagName === 'BUTTON' || t.getAttribute('role') === 'button')
}

export type ShortcutMap = Record<string, (e: KeyboardEvent) => void>

/** Document-level shortcuts; ignored while typing and for modified keys. Handlers may be replaced on each render. */
export function useShortcuts(map: ShortcutMap, enabled = true) {
  const ref = useRef(map)
  useEffect(() => {
    ref.current = map
  })
  useEffect(() => {
    if (!enabled) return
    const onKey = (e: KeyboardEvent) => {
      if (shouldIgnoreShortcut(e)) return
      const fn = ref.current[e.key]
      if (fn) fn(e)
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [enabled])
}

export const SHORTCUT_HELP: { keys: string; action: string }[] = [
  { keys: 'j / Down', action: 'Next ticket in the list' },
  { keys: 'k / Up', action: 'Previous ticket in the list' },
  { keys: 'Enter', action: 'Open the highlighted ticket' },
  { keys: 'r', action: 'Write a public reply' },
  { keys: 'n', action: 'Write an internal note' },
  { keys: 'Ctrl+Enter', action: 'Post from the reply box' },
  { keys: '/', action: 'Search the queue' },
  { keys: 'c', action: 'New ticket' },
  { keys: 'Esc', action: 'Leave the reply box / close a dialog' },
  { keys: '?', action: 'Show this help' },
]
