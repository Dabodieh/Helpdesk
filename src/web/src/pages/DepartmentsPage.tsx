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

/** What each management area is and where it lives. Everything below Departments stays inside its department (its security boundary). */
const AREAS = [
  { name: 'Organisation', text: 'Departments, teams and members.', where: 'Departments' },
  { name: 'Communication', text: 'Mailboxes, acknowledgements and shared responses.', where: 'Each department' },
  { name: 'Ticketing and self service', text: 'Ticket types, fields, request forms and knowledge.', where: 'Each department' },
  { name: 'Service management', text: 'Business hours, SLA policies and automations.', where: 'Each department' },
  { name: 'Agent productivity', text: 'Macros and canned responses.', where: 'Each department' },
] as const

export default function DepartmentsPage() {
  const me = useMe()
  const list = useQuery({ queryKey: ['departments'], queryFn: listDepartments })
  const canCreate = canPlatform(me, 'platform.departments.manage')
  const [creating, setCreating] = useState(false)

  return (
    <>
      <PageHeader
        title="Administration"
        subtitle="Set up departments and how they run. Configuration is kept inside each department."
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
          <ul className="rows" aria-label="Departments">
            {list.data.map((d) => (
              <li key={d.id} className="row">
                <div className="row-main">
                  <div className="row-title">
                    <Link to={`/admin/departments/${d.id}`} className="dept-link">
                      {d.name}
                    </Link>
                    <code className="key-chip">{d.key}</code>
                    <ActiveBadge active={d.isActive} />
                  </div>
                  {d.description && <p className="muted small row-sub">{d.description}</p>}
                  {d.myRole && <p className="muted small row-sub">Your role: {d.myRole}</p>}
                </div>
                <Link to={`/admin/departments/${d.id}`} className="row-action" aria-label={`Manage ${d.name}`}>
                  Manage →
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section aria-labelledby="areas-h">
        <h2 id="areas-h" className="section-title">
          Management areas
        </h2>
        <dl className="areas">
          {AREAS.map((a) => (
            <div key={a.name}>
              <dt>{a.name}</dt>
              <dd>
                {a.text} <span className="muted small">{a.where}</span>
              </dd>
            </div>
          ))}
          <div>
            <dt>System</dt>
            <dd>
              Health of mail, storage and background work. <Link to="/admin/status">System status</Link>
            </dd>
          </div>
        </dl>
      </section>
      {creating && <CreateDepartment onClose={() => setCreating(false)} />}
    </>
  )
}
