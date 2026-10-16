import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { departmentsWith } from '../api/permissions'
import { useMe } from '../auth'
import { Icon } from '../components/icons'
import { PageHeader } from '../components/common'
import { CATEGORIES, REPORTS, readStore, reportByKey, writeStore } from '../lib/analytics'

export function useAnalyticsDepartments() {
  const me = useMe()
  return departmentsWith(me, 'tickets.read').map((d) => ({ id: d.departmentId, name: d.departmentName }))
}

export default function AnalyticsHome() {
  const departments = useAnalyticsDepartments()
  const [params, setParams] = useSearchParams()
  const dept = departments.find((d) => d.id === params.get('dept'))?.id ?? departments[0]?.id ?? ''
  const [search, setSearch] = useState('')
  const [store, setStore] = useState(readStore)
  const link = (key: string) => `/analytics/${key}${dept ? `?dept=${encodeURIComponent(dept)}` : ''}`
  const q = search.trim().toLowerCase()
  const shown = useMemo(() => REPORTS.filter((r) => !q || `${r.title} ${r.description} ${r.category} ${r.keywords}`.toLowerCase().includes(q)), [q])
  const toggleFavourite = (key: string) => setStore(writeStore((s) => ({ ...s, favourites: s.favourites.includes(key) ? s.favourites.filter((k) => k !== key) : [...s.favourites, key] })))
  const favourites = store.favourites.map(reportByKey).filter((r) => r !== undefined)
  const recent = store.recent.map(reportByKey).filter((r) => r !== undefined).slice(0, 4)

  if (departments.length === 0) {
    return (
      <div className="page analytics">
        <PageHeader title="Analytics" subtitle="Select a report." />
        <p className="empty">Reports need a department membership with ticket access. Ask a department administrator to add you.</p>
      </div>
    )
  }

  const card = (r: (typeof REPORTS)[number]) => (
    <li key={r.key} className="report-card">
      <Link to={link(r.key)} className="report-card-main">
        <span className="setting-icon"><Icon name={r.icon} size={22} /></span>
        <div><h3>{r.title}</h3><p>{r.description}</p></div>
      </Link>
      <button type="button" className={`star${store.favourites.includes(r.key) ? ' on' : ''}`} aria-pressed={store.favourites.includes(r.key)} aria-label={`${store.favourites.includes(r.key) ? 'Remove' : 'Add'} ${r.title} ${store.favourites.includes(r.key) ? 'from' : 'to'} favourites`} onClick={() => toggleFavourite(r.key)}>
        <Icon name="star" size={18} />
      </button>
    </li>
  )

  return (
    <div className="page analytics">
      <PageHeader title="Analytics" />
      <div className="analytics-toolbar">
        <label className="settings-search"><Icon name="search" size={18} /><input type="search" aria-label="Search reports" placeholder="Search reports, e.g. SLA, backlog or email…" value={search} onChange={(e) => setSearch(e.target.value)} /></label>
        {departments.length > 1 && (
          <label className="admin-department-select"><span>Department</span>
            <select value={dept} onChange={(e) => setParams({ dept: e.target.value }, { replace: true })}>{departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select>
          </label>
        )}
      </div>
      {!q && favourites.length > 0 && (
        <section aria-labelledby="fav-h"><h2 id="fav-h" className="hub-group-label">Favourites</h2><ul className="report-grid">{favourites.map(card)}</ul></section>
      )}
      {!q && recent.length > 0 && (
        <section aria-labelledby="recent-h"><h2 id="recent-h" className="hub-group-label">Recently viewed</h2><ul className="report-grid">{recent.map(card)}</ul></section>
      )}
      {shown.length === 0 && <div className="settings-empty"><h3>No matching reports</h3><p>Try a different word, such as “SLA”, “backlog” or “email”.</p></div>}
      {CATEGORIES.map((c) => {
        const items = shown.filter((r) => r.category === c)
        return items.length === 0 ? null : (
          <section key={c} aria-labelledby={`cat-${c}`}>
            <h2 id={`cat-${c}`} className="hub-group-label">{c}</h2>
            <ul className="report-grid">{items.map(card)}</ul>
          </section>
        )
      })}
    </div>
  )
}
