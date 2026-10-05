import { useCallback, useState } from 'react'

/** Small local preferences (never required: every read/write is guarded, private windows and blocked storage just fall back). */
export function readPref(key: string): string | null {
  try {
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

export function writePref(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value)
  } catch {
    /* ignore */
  }
}

export function usePref<T extends string>(key: string, allowed: readonly T[], fallback: T): [T, (v: T) => void] {
  const [value, setValue] = useState<T>(() => {
    const stored = readPref(key)
    return (allowed as readonly string[]).includes(stored ?? '') ? (stored as T) : fallback
  })
  const set = useCallback(
    (v: T) => {
      setValue(v)
      writePref(key, v)
    },
    [key],
  )
  return [value, set]
}
