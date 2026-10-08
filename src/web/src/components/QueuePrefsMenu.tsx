import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { COLUMN_LABELS, QUEUE_COLUMNS, type QueueColumn } from '../api/queuePrefs'
import type { Queue } from '../api/ticketSchemas'
import { useQueuePrefs, visibleColumns } from '../hooks/useQueuePrefs'
import { QUEUE_TITLES } from '../lib/filters'
import { ErrorNotice } from './common'
import Dialog from './Dialog'

type Row = { key: QueueColumn; shown: boolean }

/** Personal queue preferences: which columns show and in what order, and this queue as the landing page. Stored on the account; nothing here grants access. */
export default function QueuePrefsMenu({ queue, onClose }: { queue: Queue; onClose: () => void }) {
  const { prefs, save, update } = useQueuePrefs()
  const [params] = useSearchParams()
  const shown = visibleColumns(prefs)
  const [rows, setRows] = useState<Row[]>(() => [
    ...shown.map((key) => ({ key, shown: true })),
    ...QUEUE_COLUMNS.filter((c) => !shown.includes(c)).map((key) => ({ key, shown: false })),
  ])
  const move = (i: number, d: -1 | 1) => {
    const j = i + d
    if (j < 0 || j >= rows.length) return
    const next = [...rows]
    ;[next[i], next[j]] = [next[j]!, next[i]!]
    setRows(next)
  }
  const isDefault = prefs?.defaultQueue === queue
  const onView = params.has('view')
  const landing = prefs?.defaultViewId
    ? 'You land on a saved view (set from Views).'
    : prefs?.defaultQueue
      ? `You land on ${QUEUE_TITLES[prefs.defaultQueue as Queue] ?? 'My Queue'}.`
      : 'You land on My Queue.'

  return (
    <Dialog title="Queue preferences" onClose={onClose}>
      <p className="muted small">Saved to your account, so they follow you to other browsers. Ref and Subject are always shown.</p>
      <h3>Columns</h3>
      <ul className="column-prefs" aria-label="Columns">
        {rows.map((r, i) => (
          <li key={r.key}>
            <label className="check">
              <input type="checkbox" checked={r.shown} onChange={() => setRows(rows.map((x) => (x.key === r.key ? { ...x, shown: !x.shown } : x)))} /> {COLUMN_LABELS[r.key]}
            </label>
            <span className="row-actions">
              <button type="button" className="ghost" aria-label={`Move ${COLUMN_LABELS[r.key]} up`} aria-disabled={i === 0} onClick={() => move(i, -1)}>
                ↑
              </button>
              <button type="button" className="ghost" aria-label={`Move ${COLUMN_LABELS[r.key]} down`} aria-disabled={i === rows.length - 1} onClick={() => move(i, 1)}>
                ↓
              </button>
            </span>
          </li>
        ))}
      </ul>
      <h3>Landing page</h3>
      <p className="muted small">{landing}</p>
      <div className="dialog-actions start">
        <button
          type="button"
          disabled={save.isPending || isDefault || onView}
          title={onView ? 'Use Views to make a saved view your default' : undefined}
          onClick={() => update({ defaultQueue: queue, defaultViewId: null })}
        >
          {isDefault ? `${QUEUE_TITLES[queue]} is your default` : `Make ${QUEUE_TITLES[queue]} my default`}
        </button>
        <button type="button" className="ghost" disabled={save.isPending || (!prefs?.defaultQueue && !prefs?.defaultViewId)} onClick={() => update({ defaultQueue: null, defaultViewId: null })}>
          Clear default
        </button>
      </div>
      <ErrorNotice error={save.error} />
      <div className="dialog-actions">
        <button type="button" className="ghost" disabled={save.isPending} onClick={() => update({ columns: null }, onClose)}>
          Standard columns
        </button>
        <button type="button" onClick={onClose}>
          Close
        </button>
        <button type="button" className="primary" disabled={save.isPending} onClick={() => update({ columns: rows.filter((r) => r.shown).map((r) => r.key) }, onClose)}>
          Save columns
        </button>
      </div>
    </Dialog>
  )
}
