import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { fieldErrors } from '../api/errors'
import { createDepartment, listDepartments } from '../api/organisation'
import { canPlatform, hasAnyPermission } from '../api/permissions'
import { DEPARTMENT_KEY } from '../api/schemas'
import { useMe } from '../auth'
import { ActiveBadge, ErrorNotice, Field, Loading, PageHeader } from '../components/common'
import Dialog from '../components/Dialog'

function CreateDepartment({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [touched, setTouched] = useState(false)

  const create = useMutation({
    mutationFn: () => createDepartment({ key, name: name.trim(), description: description.trim() || undefined }),
    onSuccess: async (dept) => {
      await qc.invalidateQueries({ queryKey: ['departments'] })
      await qc.invalidateQueries({ queryKey: ['me'] })
      void navigate(`/admin/departments/${dept.id}`)
    },
  })

  const keyError = !DEPARTMENT_KEY.test(key)
    ? 'Key must be 2 to 10 characters: capital letters and digits, starting with a letter.'
    : undefined
  const nameError = name.trim() ? undefined : 'Name is required.'
  const server = fieldErrors(create.error)

  function submit(e: FormEvent) {
    e.preventDefault()
    setTouched(true)
    if (keyError || nameError) return
    create.mutate()
  }

  return (
    <Dialog title="New department" onClose={onClose}>
      <form className="dialog-form" onSubmit={submit} noValidate aria-label="New department">
        <Field label="Name" error={(touched && nameError) || server.name}>
          {(p) => <input {...p} data-autofocus value={name} maxLength={200} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Key (cannot be changed later)" error={(touched && keyError) || server.key} hint="Prefix of ticket references, e.g. IT-1042.">
          {(p) => <input {...p} value={key} maxLength={10} onChange={(e) => setKey(e.target.value.toUpperCase())} />}
        </Field>
        <Field label="Description (optional)" error={server.description}>
          {(p) => <textarea {...p} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <ErrorNotice error={create.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={create.isPending}>
            Create department
          </button>
        </div>
      </form>
    </Dialog>
  )
}

export default function DepartmentsPage() {
  const me = useMe()
  const list = useQuery({ queryKey: ['departments'], queryFn: listDepartments })
  const canCreate = canPlatform(me, 'platform.departments.manage')
  const [creating, setCreating] = useState(false)

  return (
    <>
      <PageHeader
        title="Departments"
        subtitle="Teams, members and email for each department."
        actions={
          canCreate && (
            <button type="button" className="primary" onClick={() => setCreating(true)}>
              New department
            </button>
          )
        }
      />
      {!hasAnyPermission(me, 'tickets.read') && (
        <p className="notice info" role="note">
          You have no access to tickets: ticket queues need a department membership with ticket permissions.
          {me.isPlatformAdmin
            ? ' As a platform administrator you manage departments, teams and members here, but department content stays private.'
            : ''}
        </p>
      )}
      {list.isPending && <Loading />}
      {list.isError && <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />}
      {list.data && list.data.length === 0 && <p>No departments to show yet.</p>}
      {list.data && list.data.length > 0 && (
        <table>
          <caption className="sr-only">Departments</caption>
          <thead>
            <tr>
              <th scope="col">Department</th>
              <th scope="col">Status</th>
              <th scope="col">My role</th>
            </tr>
          </thead>
          <tbody>
            {list.data.map((d) => (
              <tr key={d.id}>
                <td>
                  <Link to={`/admin/departments/${d.id}`} className="dept-link">
                    {d.name}
                  </Link>{' '}
                  <code className="key-chip">{d.key}</code>
                </td>
                <td>
                  <ActiveBadge active={d.isActive} />
                </td>
                <td>{d.myRole ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {creating && <CreateDepartment onClose={() => setCreating(false)} />}
    </>
  )
}
