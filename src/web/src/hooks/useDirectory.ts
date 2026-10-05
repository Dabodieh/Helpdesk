import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { listMembers, listTeams } from '../api/organisation'
import type { NameLookup } from '../lib/activity'

/**
 * Teams and members of one department, for filter/assignment choices and for naming ids in the history.
 * Failures (e.g. a role without department.read) simply leave the lists empty: the UI degrades, the server decides.
 */
export function useDirectory(departmentId: string | undefined) {
  const teams = useQuery({
    queryKey: ['teams', departmentId],
    queryFn: () => listTeams(departmentId!),
    enabled: !!departmentId,
    staleTime: 60_000,
  })
  const members = useQuery({
    queryKey: ['members', departmentId],
    queryFn: () => listMembers(departmentId!),
    enabled: !!departmentId,
    staleTime: 60_000,
  })
  const names = useMemo<NameLookup>(
    () => ({
      users: new Map((members.data ?? []).map((m) => [m.userId, m.displayName])),
      teams: new Map((teams.data ?? []).map((t) => [t.id, t.name])),
    }),
    [members.data, teams.data],
  )
  return { teams: teams.data ?? [], members: members.data ?? [], names }
}
