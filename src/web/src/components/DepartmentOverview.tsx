import { Link } from 'react-router-dom'
import type { Department } from '../api/schemas'
import { useMe } from '../auth'
import { useAdminConfiguration } from '../hooks/useAdminConfiguration'
import { allowedSettings, settingHref } from '../lib/adminSettings'
import { SettingsCatalog } from './AdminSettings'
import { Icon } from './icons'

export default function DepartmentOverview({ department: d, search = '' }: { department: Department; search?: string }) {
  const me = useMe()
  const settings = allowedSettings(me, d.id)
  const states = useAdminConfiguration(d.id)
  const warnings = settings.filter(s => states[s.id]?.warning)
  const failed = settings.filter(s => states[s.id]?.error)
  return <section className="department-control-centre" aria-label="Department control centre">
    {!search && <>
      <div className="department-snapshot" aria-label="Configuration snapshot">
        {[{ id: 'members', label: 'People', icon: 'person' }, { id: 'mailboxes', label: 'Email channel', icon: 'mail' }].filter(item => settings.some(s => s.id === item.id)).map(item => <Link key={item.id} to={settingHref(settings.find(s => s.id === item.id)!, d.id)}>
          <Icon name={item.icon} size={22} /><div><span>{item.label}</span><strong>{states[item.id]?.label ?? 'Loading status…'}</strong></div><span aria-hidden="true">↗</span>
        </Link>)}
      </div>
      {!d.isActive && <p className="notice warn" role="note">This department is inactive. Review its configuration before reactivating it.</p>}
      {warnings.length > 0 && <aside className="configuration-attention" aria-label="Configuration needing attention"><Icon name="alert" size={22} /><div><h2>Needs your attention <span>{warnings.length}</span></h2><ul>{warnings.map(s => <li key={s.id}><Link to={settingHref(s, d.id)}>{s.title}</Link><span>{states[s.id].warning}</span></li>)}</ul></div></aside>}
      {failed.length > 0 && <p className="notice error" role="alert">Some configuration statuses could not be loaded. Use Retry on the affected cards; their settings remain available.</p>}
    </>}
    <SettingsCatalog settings={settings} departmentId={d.id} states={states} search={search} />
  </section>
}
