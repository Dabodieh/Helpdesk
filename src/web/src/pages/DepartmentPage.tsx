import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { getDepartment, updateDepartment } from '../api/organisation'
import { canInDepartment, canPlatform } from '../api/permissions'
import { useMe } from '../auth'
import CannedManager from '../components/CannedManager'
import { ActiveBadge, ErrorNotice, Loading, PageHeader, Tabs } from '../components/common'
import EditForm from '../components/EditForm'
import MailboxesSection from '../components/MailboxesSection'
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
          <Link to="/admin">Back to departments</Link>
        </p>
      </>
    )
  }
  const d = dept.data
  const canManage = canInDepartment(me, 'department.manage', d.id)

  return (
    <>
      <PageHeader
        crumbs={
          <>
            <Link to="/admin">Departments</Link> / {d.key}
          </>
        }
        title={
          <>
            {d.name} <ActiveBadge active={d.isActive} />
          </>
        }
        subtitle={`Key ${d.key}${d.myRole ? ` · Your role: ${d.myRole}` : ''}`}
      />
      <Tabs
        label="Department administration"
        tabs={[
          {
            id: 'overview',
            label: 'Overview',
            content: (
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
            ),
          },
          { id: 'teams', label: 'Teams', content: <TeamsSection department={d} /> },
          { id: 'members', label: 'Members', content: <MembersSection department={d} /> },
          {
            id: 'mailboxes',
            label: 'Mailboxes',
            hidden: !canInDepartment(me, 'department.mailboxes.manage', d.id),
            content: <MailboxesSection department={d} />,
          },
          {
            id: 'responses',
            label: 'Responses',
            hidden: !canInDepartment(me, 'department.canned.manage', d.id),
            content: (
              <section aria-labelledby="responses-h">
                <h2 id="responses-h">Shared canned responses</h2>
                <p className="muted">Available to every member of this department when replying. They are inserted into the reply box, never sent automatically.</p>
                <CannedManager scope={{ scope: 'Department', departmentId: d.id }} />
              </section>
            ),
          },
        ]}
      />
    </>
  )
}
