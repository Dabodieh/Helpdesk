import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { fieldErrors } from '../api/errors'
import { createTeam, listTeams, updateTeam } from '../api/organisation'
import { canInDepartment } from '../api/permissions'
import type { Department, Team } from '../api/schemas'
import { useMe } from '../auth'
import { ActiveBadge, ErrorNotice, Field, Loading } from './common'

function CreateTeam({ departmentId }: { departmentId: string }) {
  const qc = useQueryClient()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const create = useMutation({
    mutationFn: () => createTeam(departmentId, { name: name.trim(), description: description.trim() || undefined }),
    onSuccess: async () => {
      setName('')
      setDescription('')
      await qc.invalidateQueries({ queryKey: ['teams', departmentId] })
    },
  })
  const server = fieldErrors(create.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (name.trim()) create.mutate()
  }
  return (
    <form className="form inline" onSubmit={submit} aria-label="New team">
      <Field label="Team name" error={server.name}>
        {(p) => <input {...p} value={name} maxLength={200} required onChange={(e) => setName(e.target.value)} />}
      </Field>
      <Field label="Description (optional)" error={server.description}>
        {(p) => <input {...p} value={description} onChange={(e) => setDescription(e.target.value)} />}
      </Field>
      <button type="submit" className="primary" disabled={create.isPending}>
        Add team
      </button>
      <ErrorNotice error={create.error} />
    </form>
  )
}

function TeamRow({ departmentId, team, canManage }: { departmentId: string; team: Team; canManage: boolean }) {
  const qc = useQueryClient()
  const toggle = useMutation({
    mutationFn: () => updateTeam(departmentId, team.id, { version: team.version, isActive: !team.isActive }),
    onSettled: () => qc.invalidateQueries({ queryKey: ['teams', departmentId] }),
  })
  return (
    <>
      <tr>
        <td>
          <Link to={`/admin/departments/${departmentId}/teams/${team.id}`}>{team.name}</Link>
        </td>
        <td>{team.description}</td>
        <td>{team.memberCount ?? ''}</td>
        <td>
          <ActiveBadge active={team.isActive} />
        </td>
        <td>
          {canManage && (
            <button type="button" disabled={toggle.isPending} onClick={() => toggle.mutate()}>
              {team.isActive ? 'Deactivate' : 'Activate'}
              <span className="sr-only"> {team.name}</span>
            </button>
          )}
        </td>
      </tr>
      {toggle.isError && (
        <tr>
          <td colSpan={5}>
            <ErrorNotice error={toggle.error} />
          </td>
        </tr>
      )}
    </>
  )
}

export default function TeamsSection({ department }: { department: Department }) {
  const me = useMe()
  const teams = useQuery({ queryKey: ['teams', department.id], queryFn: () => listTeams(department.id) })
  const canManage = canInDepartment(me, 'teams.manage', department.id)

  return (
    <section aria-labelledby="teams-h">
      <h2 id="teams-h">Teams</h2>
      {teams.isPending && <Loading />}
      {teams.isError && <ErrorNotice error={teams.error} onRetry={() => void teams.refetch()} />}
      {teams.data && teams.data.length === 0 && <p>No teams yet.</p>}
      {teams.data && teams.data.length > 0 && (
        <table>
          <caption className="sr-only">Teams</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Description</th>
              <th scope="col">Members</th>
              <th scope="col">Status</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {teams.data.map((t) => (
              <TeamRow key={t.id} departmentId={department.id} team={t} canManage={canManage} />
            ))}
          </tbody>
        </table>
      )}
      {canManage && (department.isActive ? <CreateTeam departmentId={department.id} /> : <p className="muted">Teams cannot be added to an inactive department.</p>)}
    </section>
  )
}
