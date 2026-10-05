import { useState, type FormEvent } from 'react'
import { fieldErrors, isVersionConflict } from '../api/errors'
import { ErrorNotice, Field, StatusMessage } from './common'

export type EditValues = { name: string; description: string; isActive: boolean; version: string }
export type EditBody = { version: string; name?: string; description?: string; isActive?: boolean }

/**
 * Edit form for a department or team. Sends only changed fields plus the `version` it was loaded with.
 * A 409 version conflict asks the user to reload the latest data (which also resets the form).
 */
export default function EditForm({
  initial,
  canEditDetails,
  canEditActive,
  save,
  reload,
}: {
  initial: EditValues
  canEditDetails: boolean
  canEditActive: boolean
  save: (body: EditBody) => Promise<unknown>
  reload: () => Promise<unknown>
}) {
  const [values, setValues] = useState(initial)
  const [syncedVersion, setSyncedVersion] = useState(initial.version)
  const [error, setError] = useState<unknown>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  // New server data (after save or reload) replaces the form values.
  if (initial.version !== syncedVersion) {
    setSyncedVersion(initial.version)
    setValues(initial)
  }

  const conflict = isVersionConflict(error)
  const fields = fieldErrors(error)

  async function submit(e: FormEvent) {
    e.preventDefault()
    const body: EditBody = { version: initial.version }
    if (canEditDetails && values.name.trim() !== initial.name) body.name = values.name.trim()
    if (canEditDetails && values.description !== initial.description) body.description = values.description
    if (canEditActive && values.isActive !== initial.isActive) body.isActive = values.isActive
    if (Object.keys(body).length === 1) {
      setError(null)
      setSaved(false)
      return
    }
    setBusy(true)
    setSaved(false)
    try {
      await save(body)
      setError(null)
      setSaved(true)
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  async function doReload() {
    setError(null)
    await reload()
  }

  const readOnly = !canEditDetails
  return (
    <form onSubmit={(e) => void submit(e)} className="form" aria-label="Edit details">
      <Field label="Name" error={fields.name}>
        {(p) => (
          <input
            {...p}
            value={values.name}
            maxLength={200}
            required
            readOnly={readOnly}
            onChange={(e) => setValues({ ...values, name: e.target.value })}
          />
        )}
      </Field>
      <Field label="Description" error={fields.description}>
        {(p) => (
          <textarea
            {...p}
            rows={3}
            value={values.description}
            readOnly={readOnly}
            onChange={(e) => setValues({ ...values, description: e.target.value })}
          />
        )}
      </Field>
      <label className="check">
        <input
          type="checkbox"
          checked={values.isActive}
          disabled={!canEditActive}
          onChange={(e) => setValues({ ...values, isActive: e.target.checked })}
        />{' '}
        Active
      </label>
      <ErrorNotice error={error} />
      {conflict && (
        <p>
          <button type="button" onClick={() => void doReload()}>
            Reload latest version
          </button>
        </p>
      )}
      <StatusMessage>{saved ? 'Saved.' : ''}</StatusMessage>
      {(canEditDetails || canEditActive) && (
        <button type="submit" className="primary" disabled={busy}>
          Save changes
        </button>
      )}
    </form>
  )
}
