import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { fieldErrors } from '../api/errors'
import { useQueuePrefs } from '../hooks/useQueuePrefs'
import type { TicketFilters } from '../api/tickets'
import { createView, deleteView, listViews, updateView, type SavedView } from '../api/views'
import { describeFilter, toViewFilter, viewLocation } from '../lib/filters'
import { ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'
import { Icon } from './icons'

/** Saved views: open, pin, delete, and save the filters currently showing. Views are private and re-checked by the server on every run. */
export default function ViewsMenu({ filters, onClose }: { filters: TicketFilters; onClose: () => void }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const activeView = params.get('view')
  const { prefs, save: savePrefs, update: updatePrefs } = useQueuePrefs()
  const views = useQuery({ queryKey: ['views'], queryFn: listViews })
  const [name, setName] = useState('')
  const [pin, setPin] = useState(true)
  const refresh = () => qc.invalidateQueries({ queryKey: ['views'] })

  const create = useMutation({
    mutationFn: () => createView({ name: name.trim(), filter: toViewFilter(filters), isPinned: pin }),
    onSuccess: async (view) => {
      setName('')
      await refresh()
      void navigate(viewLocation(view))
      onClose()
    },
  })
  const togglePin = useMutation({
    mutationFn: (v: SavedView) => updateView(v.id, { version: v.version, isPinned: !v.isPinned }),
    onSettled: refresh,
  })
  const overwrite = useMutation({
    mutationFn: (v: SavedView) => updateView(v.id, { version: v.version, filter: toViewFilter(filters) }),
    onSuccess: async (view) => {
      await refresh()
      void navigate(viewLocation(view))
      onClose()
    },
  })
  const remove = useMutation({ mutationFn: (v: SavedView) => deleteView(v.id), onSettled: refresh })
  const server = fieldErrors(create.error)

  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim()) create.mutate()
  }

  return (
    <Dialog title="Saved views" onClose={onClose}>
      <div className="views-list">
        {views.isPending && <Loading label="Loading views" />}
        {views.isError && <ErrorNotice error={views.error} onRetry={() => void views.refetch()} />}
        {views.isSuccess && views.data.length === 0 && <p className="empty small">No saved views yet. Set up the filters you want, then save them below.</p>}
        {views.isSuccess && views.data.length > 0 && (
          <ul>
            {views.data.map((v) => (
              <li key={v.id} className={v.id === activeView ? 'active' : undefined}>
                <button
                  type="button"
                  className="view-open"
                  onClick={() => {
                    void navigate(viewLocation(v))
                    onClose()
                  }}
                >
                  <strong>{v.name}</strong>
                  <span className="muted small">{describeFilter(v)}</span>
                </button>
                <button
                  type="button"
                  className="ghost icon-btn"
                  aria-pressed={v.isPinned}
                  aria-label={v.isPinned ? `Unpin ${v.name}` : `Pin ${v.name} to the sidebar`}
                  title={v.isPinned ? 'Pinned to the sidebar' : 'Pin to the sidebar'}
                  disabled={togglePin.isPending}
                  onClick={() => togglePin.mutate(v)}
                >
                  <Icon name="star" />
                </button>
                <button
                  type="button"
                  className="ghost"
                  aria-pressed={prefs?.defaultViewId === v.id}
                  disabled={savePrefs.isPending}
                  onClick={() => updatePrefs(prefs?.defaultViewId === v.id ? { defaultViewId: null } : { defaultViewId: v.id, defaultQueue: null })}
                  title="Open this view when you sign in"
                >
                  {prefs?.defaultViewId === v.id ? 'Default view' : `Make ${v.name} my default`}
                </button>
                {v.id === activeView && (
                  <button type="button" className="ghost" disabled={overwrite.isPending} onClick={() => overwrite.mutate(v)} title="Replace this view’s filters with the ones showing now">
                    Update to current filters
                  </button>
                )}
                <button type="button" className="ghost danger-text" aria-label={`Delete ${v.name}`} disabled={remove.isPending} onClick={() => remove.mutate(v)}>
                  Delete
                </button>
              </li>
            ))}
          </ul>
        )}
        <ErrorNotice error={togglePin.error ?? remove.error ?? overwrite.error ?? savePrefs.error} />
      </div>
      <form className="dialog-form" onSubmit={submit} aria-label="Save current filters as a view">
        <h3>Save the current filters</h3>
        <Field label="View name" error={server.name} hint="For example “Urgent tickets” or “Network team pending”.">
          {(p) => <input {...p} data-autofocus value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <label className="check">
          <input type="checkbox" checked={pin} onChange={(e) => setPin(e.target.checked)} /> Pin to the sidebar
        </label>
        <ErrorNotice error={create.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Close
          </button>
          <button type="submit" className="primary" disabled={!name.trim() || create.isPending}>
            Save view
          </button>
        </div>
      </form>
    </Dialog>
  )
}
