import { useQuery } from '@tanstack/react-query'
import { Link, NavLink, useSearchParams } from 'react-router-dom'
import { listDepartments } from '../api/organisation'
import { canInDepartment } from '../api/permissions'
import { useMe } from '../auth'
import { DepartmentSelect, SettingsCatalog, SettingsSearch } from '../components/AdminSettings'
import { ActiveBadge, ErrorNotice, Loading, PageHeader } from '../components/common'
import { Icon } from '../components/icons'
import { useAdminConfiguration } from '../hooks/useAdminConfiguration'
import { ADMIN_CATEGORIES, allowedSettings } from '../lib/adminSettings'

export default function AdminHubPage() {
  const me = useMe()
  const list = useQuery({ queryKey: ['departments'], queryFn: listDepartments })
  const [params, setParams] = useSearchParams()
  const departments = (list.data ?? []).filter(d => canInDepartment(me, 'department.read', d.id))
  // An invalid/foreign id never becomes a query target. The server-returned list is authoritative.
  const department = params.has('department') ? departments.find(d => d.id === params.get('department')) : departments[0]
  const settings = department ? allowedSettings(me, department.id) : []
  const states = useAdminConfiguration(department?.id ?? '')
  const search = params.get('q') ?? ''
  const category = params.get('category') ?? ''
  const change = (key: string, value: string) => setParams(previous => { const next = new URLSearchParams(previous); if (value) next.set(key, value); else next.delete(key); return next }, { replace: true })
  const categoryLink = (value: string) => { const next = new URLSearchParams(params); if (value) next.set('category', value); else next.delete('category'); return `/admin?${next}` }
  const globalMatches = (text: string) => (!category || category === 'platform') && search.toLowerCase().trim().split(/\s+/).every(word => text.toLowerCase().includes(word))
  return <div className="admin-layout admin-hub-layout">
    <aside className="admin-sidebar hub-sidebar">
      <h2>Administration</h2>
      <nav aria-label="Administration"><NavLink to="/admin" end><Icon name="views" /> Settings hub</NavLink><NavLink to="/admin/departments"><Icon name="organisation" /> Departments</NavLink></nav>
      <span className="rail-heading">Browse settings</span>
      <nav aria-label="Settings categories">
        <Link className={!category ? 'active' : undefined} to={categoryLink('')}>All settings</Link>
        {ADMIN_CATEGORIES.filter(c => settings.some(s => s.category === c.id)).map(c => <Link key={c.id} className={category === c.id ? 'active' : undefined} to={categoryLink(c.id)}><Icon name={c.icon} />{c.title}</Link>)}
        <Link className={category === 'platform' ? 'active' : undefined} to={categoryLink('platform')}><Icon name="server" />Platform &amp; operations</Link>
      </nav>
      <p className="sidebar-note"><Icon name="lock" /> Settings are scoped to the selected department. Ticket access follows department membership.</p>
    </aside>
    <div className="page admin administration-hub">
      <PageHeader title="Administration" subtitle="Everything you need to configure and run your service desk." />
      <div className="hub-search-row"><SettingsSearch value={search} onChange={value => change('q', value)} /><DepartmentSelect departments={departments} value={department?.id ?? ''} disabled={list.isPending || list.isError} onChange={id => change('department', id)} /></div>
      {list.isPending && <Loading label="Loading authorised departments" />}
      {list.isError && <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />}
      <div className="hub-platform-grid">
        {globalMatches('organisation departments management access create platform') && <Link className="hub-platform-card" to="/admin/departments"><span className="setting-icon"><Icon name="organisation" size={22} /></span><div><span className="settings-scope-label">Organisation</span><h2>Departments</h2><p>{list.isSuccess ? `${departments.length} available to you · Manage structure and membership` : 'Manage your department structure'}</p></div><span aria-hidden="true">↗</span></Link>}
        {globalMatches('platform operations system status health readiness') && <Link className="hub-platform-card" to="/admin/status"><span className="setting-icon"><Icon name="server" size={22} /></span><div><span className="settings-scope-label">Platform</span><h2>System status</h2><p>Check application readiness and availability</p></div><span aria-hidden="true">↗</span></Link>}
      </div>
      {department && category !== 'platform' && <>
        <div className="hub-department-context"><div><span className="scope-dot" aria-hidden="true" /><strong>{department.name}</strong><ActiveBadge active={department.isActive} /><span className="muted">Department settings</span></div><Link to={`/admin/departments/${department.id}`}>Open control centre →</Link></div>
        <SettingsCatalog settings={settings} departmentId={department.id} states={states} search={search} category={category} />
      </>}
      {!department && list.isSuccess && <div className="settings-empty"><Icon name="organisation" size={28} /><h2>{departments.length ? 'Choose a department' : 'No departments available'}</h2><p>{departments.length ? 'Select an authorised department to browse its settings.' : 'Department settings appear when you have access to a department.'}</p><Link to="/admin/departments">Manage departments →</Link></div>}
      {category === 'platform' && !globalMatches('organisation departments management access create platform') && !globalMatches('platform operations system status health readiness') && <p className="empty">No platform settings match this search.</p>}
    </div>
  </div>
}
