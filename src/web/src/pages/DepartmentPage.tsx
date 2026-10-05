import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { getDepartment, updateDepartment } from '../api/organisation'
import { canInDepartment, canPlatform } from '../api/permissions'
import { useMe } from '../auth'
import { ActiveBadge, ErrorNotice, Loading } from '../components/common'
import EditForm from '../components/EditForm'
import MembersSection from '../components/MembersSection'
import TeamsSection from '../components/TeamsSection'

export default function DepartmentPage() {
  const { departmentId = '' } = useParams()
  const me = useMe()
  const qc = useQueryClient()
  const dept = useQuery({ queryKey: ['department', departmentId], queryFn: () => getDepartment(departmentId) })

  if (dept.isPending) return <Loading />
  if (dept.isError) {
    return (
      <>
        <ErrorNotice error={dept.error} onRetry={() => void dept.refetch()} />
        <p>
          <Link to="/">Back to departments</Link>
        </p>
      </>
    )
  }
  const d = dept.data
  const canManage = canInDepartment(me, 'department.manage', d.id)

  return (
    <>
      <p>
        <Link to="/">Departments</Link> / {d.key}
      </p>
      <h1>
        {d.name} <ActiveBadge active={d.isActive} />
      </h1>
      <p className="muted">
        Key {d.key}
        {d.myRole ? ` · Your role: ${d.myRole}` : ''}
      </p>

      <section aria-labelledby="details-h">
        <h2 id="details-h">Details</h2>
        <EditForm
          initial={{ name: d.name, description: d.description ?? '', isActive: d.isActive, version: d.version }}
          canEditDetails={canManage}
          canEditActive={canPlatform(me, 'platform.departments.manage')}
          save={async (body) => {
            await updateDepartment(d.id, body)
            await qc.invalidateQueries({ queryKey: ['department', d.id] })
            await qc.invalidateQueries({ queryKey: ['departments'] })
          }}
          reload={() => dept.refetch()}
        />
      </section>

      <TeamsSection department={d} />
      <MembersSection department={d} />
    </>
  )
}
