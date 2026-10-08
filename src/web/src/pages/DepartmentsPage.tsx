import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { fieldErrors } from '../api/errors'
import { createDepartment, listDepartments } from '../api/organisation'
import { canPlatform, hasAnyPermission } from '../api/permissions'
import { DEPARTMENT_KEY, ROLE_LABEL } from '../api/schemas'
import { useMe } from '../auth'
import { ActiveBadge, ErrorNotice, Field, Loading, PageHeader } from '../components/common'
import Dialog from '../components/Dialog'
import { Icon } from '../components/icons'

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
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('all')
  const visible = (list.data ?? []).filter(d => `${d.name} ${d.key}`.toLowerCase().includes(search.toLowerCase()) && (status === 'all' || d.isActive === (status === 'active')))

  return (
    <>
      <PageHeader
        crumbs={<><Link to="/admin">Administration</Link> / Departments</>}
        title="Departments"
        subtitle="Manage department structure, access and service configuration."
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
      <section aria-labelledby="depts-h">
        <h2 id="depts-h" className="section-title">
          Departments
        </h2>
        {list.isPending && <Loading />}
        {list.isError && <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />}
        {list.data && list.data.length === 0 && (
          <p className="empty">
            No departments to show. A department is the boundary for tickets, people and configuration.
            {canCreate ? ' Create the first one to get started.' : ' Ask a platform administrator to add one or give you a membership.'}
          </p>
        )}
        {list.data && list.data.length > 0 && (
          <>
            <div className="admin-filters">
              <label className="search-control"><Icon name="search" /><input aria-label="Find a department" placeholder="Find a department…" value={search} onChange={e => setSearch(e.target.value)} /></label>
              <select aria-label="Department status" value={status} onChange={e => setStatus(e.target.value)}><option value="all">All statuses</option><option value="active">Active</option><option value="inactive">Inactive</option></select>
              <span className="muted small">{visible.length} of {list.data.length} departments</span>
            </div>
            <div className="table-scroll">
              <table className="department-table" aria-label="Departments">
                <thead><tr><th>Department</th><th>Prefix</th><th>Status</th><th>Your access</th><th><span className="sr-only">Manage</span></th></tr></thead>
                <tbody>{visible.map(d => <tr key={d.id}>
                  <td><div className="department-cell"><span className="department-icon"><Icon name="team" /></span><div><Link to={`/admin/departments/${d.id}`} className="dept-link">{d.name}</Link>{d.description && <p className="muted small">{d.description}</p>}</div></div></td>
                  <td><code className="key-chip">{d.key}</code></td><td><ActiveBadge active={d.isActive} /></td>
                  <td className="muted small">{d.myRole ? ROLE_LABEL[d.myRole] ?? d.myRole : 'Metadata only'}</td>
                  <td><Link to={`/admin/departments/${d.id}`} className="row-action" aria-label={`Manage ${d.name}`}>Manage →</Link></td>
                </tr>)}</tbody>
              </table>
              {visible.length === 0 && <p className="empty">No departments match these filters.</p>}
            </div>
          </>
        )}
      </section>
      {creating && <CreateDepartment onClose={() => setCreating(false)} />}
    </>
  )
}
