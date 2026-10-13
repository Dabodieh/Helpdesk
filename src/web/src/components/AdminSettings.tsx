import { Link } from 'react-router-dom'
import type { Department } from '../api/schemas'
import { ADMIN_CATEGORIES, matchesSetting, settingHref, type AdminSetting } from '../lib/adminSettings'
import type { ConfigurationState } from '../hooks/useAdminConfiguration'
import { StatusBadge } from './common'
import { Icon } from './icons'

export function DepartmentSelect({ departments, value, onChange, disabled = false }: { departments: Department[]; value: string; onChange: (id: string) => void; disabled?: boolean }) {
  return <label className="admin-department-select"><span>Department</span><select value={value} onChange={e => onChange(e.target.value)} disabled={disabled}>
    {!value && <option value="">Select a department</option>}
    {departments.map(d => <option key={d.id} value={d.id}>{d.name}{d.isActive ? '' : ' · Inactive'}</option>)}
  </select></label>
}
export function SettingsSearch({ value, onChange, label = 'Search administration settings' }: { value: string; onChange: (value: string) => void; label?: string }) {
  return <div className="settings-search"><Icon name="search" size={20} /><input type="search" aria-label={label} placeholder="Search settings…" value={value} onChange={e => onChange(e.target.value)} />{value && <button type="button" className="ghost" aria-label="Clear settings search" onClick={() => onChange('')}>Clear</button>}</div>
}
export function SettingsCatalog({ settings, departmentId, states, search = '', category = '' }: { settings: AdminSetting[]; departmentId: string; states: Record<string, ConfigurationState>; search?: string; category?: string }) {
  const visible = settings.filter(s => (!category || s.category === category) && matchesSetting(s, search))
  return <div className="settings-catalog">
    {search && <p className="settings-results" role="status">{visible.length} {visible.length === 1 ? 'setting' : 'settings'} matching “{search}”</p>}
    {visible.length === 0 && <div className="settings-empty"><Icon name="search" size={24} /><h3>No matching settings</h3><p>Try a different term or category. Only settings available to you are shown.</p></div>}
    {ADMIN_CATEGORIES.map(group => {
      const items = visible.filter(s => s.category === group.id)
      if (!items.length) return null
      return <section key={group.id} className="settings-category" aria-labelledby={`settings-${group.id}`}>
        <div className="settings-category-heading"><h2 id={`settings-${group.id}`}>{group.title}</h2></div>
        <div className="settings-card-grid">
          {items.map(setting => {
            const status = states[setting.id]
            return <article className="setting-card" key={setting.id}>
              <Link to={settingHref(setting, departmentId)} className="setting-main" aria-label={`Open ${setting.title}`}>
                <span className="setting-icon"><Icon name={setting.icon} size={22} /></span>
                <div><h3>{setting.title}</h3><p>{setting.description}</p></div>
              </Link>
              <div className="setting-footer">
                {status ? <StatusBadge tone={status.tone}>{status.label}</StatusBadge> : <span className="muted">{setting.content ? 'Live operational view' : 'Department setting'}</span>}
                {status?.error && <button type="button" className="ghost" aria-label={`Retry ${setting.title} status`} onClick={status.retry}>Retry</button>}
                {!status?.error && <span aria-hidden="true">{status?.loading ? 'Checking' : 'Manage →'}</span>}
              </div>
            </article>
          })}
        </div>
      </section>
    })}
  </div>
}
