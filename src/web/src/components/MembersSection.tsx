import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { listMembers, lookupUsers, removeMember, setMemberRole } from '../api/organisation'
import { canInDepartment } from '../api/permissions'
import { ROLE_LABEL, ROLES, type Department, type Member, type Role } from '../api/schemas'
import { useMe } from '../auth'
import { ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

const MIN_QUERY = 3

function MemberRow({ departmentId, member, canManage }: { departmentId: string; member: Member; canManage: boolean }) {
  const qc = useQueryClient()
  const refresh = () => qc.invalidateQueries({ queryKey: ['members', departmentId] })
  const change = useMutation({
    mutationFn: (role: Role) => setMemberRole(departmentId, member.userId, role),
    onSettled: refresh,
  })
  const remove = useMutation({
    mutationFn: () => removeMember(departmentId, member.userId),
    onSuccess: async () => {
      await refresh()
    },
  })
  const error = change.error ?? remove.error
  return (
    <>
      <tr>
        <td>{member.displayName}</td>
        <td>{member.email}</td>
        <td>
          {canManage ? (
            <select
              aria-label={`Role of ${member.displayName}`}
              value={member.role}
              disabled={change.isPending || remove.isPending}
              onChange={(e) => change.mutate(e.target.value as Role)}
            >
              {!ROLES.includes(member.role as Role) && <option value={member.role}>{ROLE_LABEL[member.role] ?? member.role}</option>}
              {ROLES.map((r) => (
                <option key={r} value={r}>
                  {ROLE_LABEL[r]}
                </option>
              ))}
            </select>
          ) : (
            (ROLE_LABEL[member.role] ?? member.role)
          )}
        </td>
        <td>
          {canManage && (
            <button
              type="button"
              disabled={remove.isPending}
              onClick={() => {
                if (window.confirm(`Remove ${member.displayName} from this department? They lose access to its tickets and settings straight away. Their past work and the audit trail stay; you can add them again later.`)) remove.mutate()
              }}
            >
              Remove<span className="sr-only"> {member.displayName}</span>
            </button>
          )}
        </td>
      </tr>
      {error && (
        <tr>
          <td colSpan={4}>
            <ErrorNotice error={error} />
          </td>
        </tr>
      )}
    </>
  )
}

function AddMember({ departmentId, existing, onClose }: { departmentId: string; existing: Set<string>; onClose: () => void }) {
  const qc = useQueryClient()
  const [text, setText] = useState('')
  const [q, setQ] = useState('')
  const [role, setRole] = useState<Role>('Agent')
  const [added, setAdded] = useState('')

  const results = useQuery({
    queryKey: ['userLookup', q],
    queryFn: () => lookupUsers(q),
    enabled: q.length >= MIN_QUERY,
  })
  const add = useMutation({
    mutationFn: (userId: string) => setMemberRole(departmentId, userId, role),
    onSuccess: async (m) => {
      setAdded(`${m.displayName} added as ${m.role}.`)
      await qc.invalidateQueries({ queryKey: ['members', departmentId] })
    },
  })

  function search(e: FormEvent) {
    e.preventDefault()
    setAdded('')
    setQ(text.trim().length >= MIN_QUERY ? text.trim() : '')
  }

  const tooShort = text.trim().length > 0 && text.trim().length < MIN_QUERY
  return (
    <Dialog title="Add member" onClose={onClose}>
      <form className="form inline" onSubmit={search} role="search" aria-label="Find user">
        <Field label="Find user by name or email" hint={tooShort ? `Enter at least ${MIN_QUERY} characters.` : undefined}>
          {(p) => <input {...p} data-autofocus type="search" value={text} onChange={(e) => setText(e.target.value)} />}
        </Field>
        <button type="submit" disabled={text.trim().length < MIN_QUERY}>
          Search
        </button>
        <Field label="Role">
          {(p) => (
            <select {...p} value={role} onChange={(e) => setRole(e.target.value as Role)}>
              {ROLES.map((r) => (
                <option key={r}>{r}</option>
              ))}
            </select>
          )}
        </Field>
      </form>
      {results.isFetching && <Loading label="Searching" />}
      <ErrorNotice error={results.error ?? add.error} />
      <p role="status" className="ok">
        {added}
      </p>
      {results.data && results.data.length === 0 && <p>No matching users.</p>}
      {results.data && results.data.length > 0 && (
        <ul className="plain" aria-label="Search results">
          {results.data.map((u) => (
            <li key={u.id}>
              {u.displayName} <span className="muted">{u.email}</span>{' '}
              {existing.has(u.id) ? (
                <span className="muted">Already a member</span>
              ) : (
                <button type="button" disabled={add.isPending} onClick={() => add.mutate(u.id)}>
                  Add<span className="sr-only"> {u.displayName}</span>
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      <div className="dialog-actions">
        <button type="button" onClick={onClose}>
          Done
        </button>
      </div>
    </Dialog>
  )
}

export default function MembersSection({ department }: { department: Department }) {
  const me = useMe()
  const members = useQuery({ queryKey: ['members', department.id], queryFn: () => listMembers(department.id) })
  const canManage = canInDepartment(me, 'department.members.manage', department.id)
  const [adding, setAdding] = useState(false)

  return (
    <section aria-labelledby="members-h">
      <div className="section-head">
        <h2 id="members-h">Members</h2>
        {canManage && department.isActive && (
          <button type="button" className="primary" onClick={() => setAdding(true)}>
            Add member
          </button>
        )}
      </div>
      {members.isPending && <Loading />}
      {members.isError && <ErrorNotice error={members.error} onRetry={() => void members.refetch()} />}
      {members.data && members.data.length === 0 && <p>No members yet.</p>}
      {members.data && members.data.length > 0 && (
        <table>
          <caption className="sr-only">Department members</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Email</th>
              <th scope="col">Role</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {members.data.map((m) => (
              <MemberRow key={m.userId} departmentId={department.id} member={m} canManage={canManage} />
            ))}
          </tbody>
        </table>
      )}
      {canManage && !department.isActive && <p className="muted">Members cannot be added to an inactive department.</p>}
      {adding && <AddMember departmentId={department.id} existing={new Set(members.data?.map((m) => m.userId))} onClose={() => setAdding(false)} />}
    </section>
  )
}
