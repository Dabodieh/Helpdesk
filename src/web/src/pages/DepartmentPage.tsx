import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { getDepartment, listDepartments, updateDepartment } from '../api/organisation'
import { canInDepartment, canPlatform } from '../api/permissions'
import { useMe } from '../auth'
import { ROLE_LABEL } from '../api/schemas'
import { DepartmentSelect, SettingsSearch } from '../components/AdminSettings'
import AutoReplySection from '../components/AutoReplySection'
import AutomationsSection from '../components/AutomationsSection'
import BusinessHoursSection from '../components/BusinessHoursSection'
import CannedManager from '../components/CannedManager'
import { ActiveBadge, ErrorNotice, Loading, PageHeader, Tabs } from '../components/common'
import DepartmentOverview from '../components/DepartmentOverview'
import Dialog from '../components/Dialog'
import EditForm from '../components/EditForm'
import TicketingSection from '../components/TicketingSection'
import KnowledgeSection from '../components/KnowledgeSection'
import MacrosSection from '../components/MacrosSection'
import MailboxesSection from '../components/MailboxesSection'
import MembersSection from '../components/MembersSection'
import RequesterIdentitiesSection from '../components/RequesterIdentitiesSection'
import SlaPoliciesSection from '../components/SlaPoliciesSection'

/** Section selection lives in the URL (?section=...), so a section can be bookmarked; the route itself is unchanged. */
const SECTION = 'section'

export default function DepartmentPage() {
  const { departmentId = '' } = useParams()
  const me = useMe()
  const qc = useQueryClient()
  const [searchParams, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const departments = useQuery({ queryKey: ['departments'], queryFn: listDepartments })
  const filter = searchParams.get('find') ?? ''
  const [editing, setEditing] = useState(false)
  const dept = useQuery({ queryKey: ['department', departmentId], queryFn: () => getDepartment(departmentId) })

  if (dept.isPending) return <Loading />
  if (dept.isError) {
    return (
      <>
        <ErrorNotice error={dept.error} onRetry={() => void dept.refetch()} />
        <p>
          <Link to="/admin/departments">Back to departments</Link>
        </p>
      </>
    )
  }
  const d = dept.data
  const canManage = canInDepartment(me, 'department.manage', d.id)
  const canActivate = canPlatform(me, 'platform.departments.manage')
  const choices = (departments.data ?? [d]).filter(item => canInDepartment(me, 'department.read', item.id))
  const find = (value: string) => setSearch(s => { const next = new URLSearchParams(s); next.set(SECTION, 'overview'); if (value) next.set('find', value); else next.delete('find'); return next }, { replace: true })

  return (
    <div className="department-page">
      <PageHeader
        crumbs={
          <>
            Administration / <Link to="/admin/departments">Departments</Link> / {d.name}
          </>
        }
        title={
          <>
            {d.name} <ActiveBadge active={d.isActive} />
          </>
        }
        subtitle={
          <>
            Ticket prefix: <code className="key-chip">{d.key}</code>
            {d.myRole ? ` · ${ROLE_LABEL[d.myRole] ?? d.myRole}` : ' · Administrative access'}
            {d.description && <span className="department-description">{d.description}</span>}
          </>
        }
        actions={
          (canManage || canActivate) && (
            <button type="button" onClick={() => setEditing(true)}>
              Edit department
            </button>
          )
        }
      />
      {editing && (
        <Dialog title="Edit department" onClose={() => setEditing(false)}>
          <EditForm
            initial={{ name: d.name, description: d.description ?? '', isActive: d.isActive, version: d.version }}
            canEditDetails={canManage}
            canEditActive={canActivate}
            save={async (body) => {
              await updateDepartment(d.id, body)
              await qc.invalidateQueries({ queryKey: ['department', d.id] })
              await qc.invalidateQueries({ queryKey: ['departments'] })
              setEditing(false)
            }}
            reload={() => dept.refetch()}
          />
          <p className="muted small">The department code ({d.key}) prefixes ticket references and cannot be changed.</p>
        </Dialog>
      )}
      <Tabs
        label="Department administration"
        vertical
        sidebarHeader={<div className="department-context">
          <Link to="/admin/departments">← All departments</Link>
          <DepartmentSelect departments={choices} value={d.id} disabled={departments.isPending || departments.isError} onChange={id => { const next = new URLSearchParams(searchParams); next.delete('find'); setEditing(false); void navigate(`/admin/departments/${id}?${next}`) }} />
          {departments.isError && <button type="button" className="ghost" onClick={() => void departments.refetch()}>Retry department list</button>}
          <SettingsSearch value={filter} onChange={find} label="Search department settings" />
        </div>}
        param={SECTION}
        tabs={[
          { id: 'overview', label: 'Overview', group: 'Department', content: <DepartmentOverview department={d} search={filter} /> },
          { id: 'members', label: 'Members', group: 'People & access', content: <MembersSection department={d} /> },
          {
            id: 'requesters',
            label: 'Requester identities',
            group: 'People & access',
            hidden: !canInDepartment(me, 'department.members.manage', d.id),
            content: <RequesterIdentitiesSection department={d} />,
          },
          {
            id: 'mailboxes',
            label: 'Mailboxes',
            group: 'Communication',
            hidden: !canInDepartment(me, 'department.mailboxes.manage', d.id),
            content: <MailboxesSection department={d} />,
          },
          { id: 'acknowledgements', label: 'Acknowledgements', group: 'Communication', hidden: !canInDepartment(me, 'department.mailboxes.manage', d.id), content: <AutoReplySection department={d} /> },
          {
            id: 'responses',
            label: 'Responses',
            group: 'Communication',
            hidden: !canInDepartment(me, 'department.canned.manage', d.id),
            content: (
              <section aria-labelledby="responses-h">
                <h2 id="responses-h">Shared canned responses</h2>
                <p className="muted">Available to every member of this department when replying. They are inserted into the reply box, never sent automatically.</p>
                <CannedManager scope={{ scope: 'Department', departmentId: d.id }} />
              </section>
            ),
          },
          {
            id: 'ticketing',
            label: 'Ticket settings',
            group: 'Ticket setup',
            hidden: !canInDepartment(me, 'department.forms.manage', d.id),
            content: <TicketingSection department={d} />,
          },
          {
            id: 'hours',
            label: 'Business hours',
            group: 'Service management',
            hidden: !canInDepartment(me, 'department.sla.manage', d.id),
            content: <BusinessHoursSection department={d} />,
          },
          {
            id: 'sla',
            label: 'SLA policies',
            group: 'Service management',
            hidden: !canInDepartment(me, 'department.sla.manage', d.id),
            content: <SlaPoliciesSection department={d} />,
          },
          {
            id: 'automations',
            label: 'Automations',
            group: 'Service management',
            hidden: !canInDepartment(me, 'department.automation.manage', d.id),
            content: <AutomationsSection department={d} />,
          },
          {
            id: 'knowledge',
            label: 'Knowledge',
            group: 'Self service',
            hidden: !canInDepartment(me, 'department.knowledge.manage', d.id),
            content: <KnowledgeSection department={d} />,
          },
          {
            id: 'macros',
            label: 'Macros',
            group: 'Productivity',
            hidden: !canInDepartment(me, 'department.canned.manage', d.id),
            content: <MacrosSection department={d} />,
          },
        ]}
      />
    </div>
  )
}
