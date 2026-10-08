import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import {
  addTeamMember,
  getDepartment,
  getTeam,
  listMembers,
  listTeamMembers,
  removeTeamMember,
  updateTeam,
} from '../api/organisation'
import { canInDepartment } from '../api/permissions'
import { useMe } from '../auth'
import { ActiveBadge, ErrorNotice, Loading, PageHeader } from '../components/common'
import EditForm from '../components/EditForm'

function TeamMembers({ departmentId, teamId, active, canManage }: { departmentId: string; teamId: string; active: boolean; canManage: boolean }) {
  const qc = useQueryClient()
  const [selected, setSelected] = useState('')
  const members = useQuery({ queryKey: ['teamMembers', departmentId, teamId], queryFn: () => listTeamMembers(departmentId, teamId) })
  const deptMembers = useQuery({ queryKey: ['members', departmentId], queryFn: () => listMembers(departmentId), enabled: canManage })
  const refresh = async () => {
    await qc.invalidateQueries({ queryKey: ['teamMembers', departmentId, teamId] })
    await qc.invalidateQueries({ queryKey: ['teams', departmentId] })
  }
  const add = useMutation({
    mutationFn: () => addTeamMember(departmentId, teamId, selected),
    onSuccess: async () => {
      setSelected('')
      await refresh()
    },
  })
  const remove = useMutation({ mutationFn: (userId: string) => removeTeamMember(departmentId, teamId, userId), onSuccess: refresh })

  const inTeam = new Set(members.data?.map((m) => m.userId))
  const candidates = (deptMembers.data ?? []).filter((m) => !inTeam.has(m.userId))

  return (
    <section aria-labelledby="tm-h">
      <h2 id="tm-h">Team members</h2>
      {members.isPending && <Loading />}
      {members.isError && <ErrorNotice error={members.error} onRetry={() => void members.refetch()} />}
      {members.data && members.data.length === 0 && <p>No members in this team.</p>}
      {members.data && members.data.length > 0 && (
        <table>
          <caption className="sr-only">Team members</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Email</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {members.data.map((m) => (
              <tr key={m.userId}>
                <td>{m.displayName}</td>
                <td>{m.email}</td>
                <td>
                  {canManage && (
                    <button type="button" disabled={remove.isPending} onClick={() => remove.mutate(m.userId)}>
                      Remove<span className="sr-only"> {m.displayName}</span>
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ErrorNotice error={add.error ?? remove.error ?? deptMembers.error} />
      {canManage && (
        <form
          className="form inline"
          aria-label="Add team member"
          onSubmit={(e) => {
            e.preventDefault()
            if (selected) add.mutate()
          }}
        >
          <div className="field">
            <label htmlFor="team-add">Add department member</label>
            <select id="team-add" value={selected} disabled={!active} onChange={(e) => setSelected(e.target.value)}>
              <option value="">Select a member…</option>
              {candidates.map((m) => (
                <option key={m.userId} value={m.userId}>
                  {m.displayName}
                </option>
              ))}
            </select>
            {!active && <span className="muted">Members cannot be added to an inactive team.</span>}
          </div>
          <button type="submit" className="primary" disabled={!selected || add.isPending || !active}>
            Add to team
          </button>
        </form>
      )}
    </section>
  )
}

export default function TeamPage() {
  const { departmentId = '', teamId = '' } = useParams()
  const me = useMe()
  const qc = useQueryClient()
  const dept = useQuery({ queryKey: ['department', departmentId], queryFn: () => getDepartment(departmentId) })
  const team = useQuery({ queryKey: ['team', departmentId, teamId], queryFn: () => getTeam(departmentId, teamId) })

  if (team.isPending || dept.isPending) return <Loading />
  const failure = team.error ?? dept.error
  if (failure || !team.data || !dept.data) {
    return (
      <>
        <ErrorNotice error={failure} onRetry={() => void Promise.all([team.refetch(), dept.refetch()])} />
        <p>
          <Link to={`/admin/departments/${departmentId}`}>Back to department</Link>
        </p>
      </>
    )
  }
  const t = team.data
  const canManage = canInDepartment(me, 'teams.manage', departmentId)

  return (
    <>
      <PageHeader title={<>{t.name} <ActiveBadge active={t.isActive} /></>} subtitle="Team details and department members."
        crumbs={<><Link to="/admin/departments">Departments</Link> / <Link to={`/admin/departments/${departmentId}?section=teams`}>{dept.data.name}</Link> / Team</>} />
      <section aria-labelledby="details-h">
        <h2 id="details-h">Details</h2>
        <EditForm
          initial={{ name: t.name, description: t.description ?? '', isActive: t.isActive, version: t.version }}
          canEditDetails={canManage}
          canEditActive={canManage}
          save={async (body) => {
            await updateTeam(departmentId, teamId, body)
            await qc.invalidateQueries({ queryKey: ['team', departmentId, teamId] })
            await qc.invalidateQueries({ queryKey: ['teams', departmentId] })
          }}
          reload={() => team.refetch()}
        />
      </section>
      <TeamMembers departmentId={departmentId} teamId={teamId} active={t.isActive} canManage={canManage} />
    </>
  )
}
