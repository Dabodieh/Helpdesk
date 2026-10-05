import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { fieldErrors } from '../api/errors'
import { createDepartment, listDepartments } from '../api/organisation'
import { canPlatform } from '../api/permissions'
import { DEPARTMENT_KEY } from '../api/schemas'
import { useMe } from '../auth'
import { ActiveBadge, ErrorNotice, Field, Loading } from '../components/common'

function CreateDepartment() {
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
      void navigate(`/departments/${dept.id}`)
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
    <section aria-labelledby="new-dept">
      <h2 id="new-dept">New department</h2>
      <form className="form" onSubmit={submit} noValidate>
        <Field label="Key (cannot be changed later)" error={(touched && keyError) || server.key}>
          {(p) => <input {...p} value={key} maxLength={10} onChange={(e) => setKey(e.target.value.toUpperCase())} />}
        </Field>
        <Field label="Name" error={(touched && nameError) || server.name}>
          {(p) => <input {...p} value={name} maxLength={200} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Description (optional)" error={server.description}>
          {(p) => <textarea {...p} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <ErrorNotice error={create.error} />
        <button type="submit" className="primary" disabled={create.isPending}>
          Create department
        </button>
      </form>
    </section>
  )
}

export default function DepartmentsPage() {
  const me = useMe()
  const list = useQuery({ queryKey: ['departments'], queryFn: listDepartments })
  const canCreate = canPlatform(me, 'platform.departments.manage')

  return (
    <>
      <h1>Departments</h1>
      {me.isPlatformAdmin && me.memberships.length === 0 && (
        <p className="muted">
          As a platform administrator you see all departments as administrative information only. Department content
          requires a department membership.
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
              <th scope="col">Key</th>
              <th scope="col">Name</th>
              <th scope="col">Status</th>
              <th scope="col">My role</th>
            </tr>
          </thead>
          <tbody>
            {list.data.map((d) => (
              <tr key={d.id}>
                <td>{d.key}</td>
                <td>
                  <Link to={`/departments/${d.id}`}>{d.name}</Link>
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
      {canCreate && <CreateDepartment />}
    </>
  )
}
