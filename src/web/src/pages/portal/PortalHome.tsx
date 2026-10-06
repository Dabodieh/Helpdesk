import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { listKnowledgeCategories, listPortalRequests, searchKnowledge } from '../../api/portal'
import { ErrorNotice, Loading } from '../../components/common'
import { RequestCard } from './parts'

/** The requester home: "how do I fix this myself?" (search) or "how do I contact support?" (submit). Nothing else. */
export default function PortalHome() {
  const navigate = useNavigate()
  const [q, setQ] = useState('')
  const recent = useQuery({ queryKey: ['portal', 'requests', 'active', 'home'], queryFn: () => listPortalRequests('active', null, 5) })
  const categories = useQuery({ queryKey: ['portal', 'knowledge', 'categories'], queryFn: listKnowledgeCategories, staleTime: 60_000 })
  const latest = useQuery({ queryKey: ['portal', 'knowledge', 'latest'], queryFn: () => searchKnowledge({ limit: 5 }), staleTime: 60_000 })

  function search(e: FormEvent) {
    e.preventDefault()
    void navigate(`/portal/knowledge${q.trim() ? `?q=${encodeURIComponent(q.trim())}` : ''}`)
  }

  return (
    <>
      <section className="portal-hero" aria-labelledby="portal-hero-h">
        <h1 id="portal-hero-h">How can we help?</h1>
        <form className="portal-search" role="search" onSubmit={search}>
          <label htmlFor="portal-q" className="sr-only">
            Search help articles
          </label>
          <input id="portal-q" type="search" value={q} placeholder="Search for an answer, for example “reset password”" onChange={(e) => setQ(e.target.value)} />
          <button type="submit" className="primary">
            Search
          </button>
        </form>
        <p>
          <Link to="/portal/submit" className="button-link primary-link">
            Submit a request
          </Link>
          <span className="muted"> Can’t find what you need? Tell us and we’ll look into it.</span>
        </p>
      </section>

      <section aria-labelledby="home-requests-h" className="portal-section">
        <div className="section-head">
          <h2 id="home-requests-h">Your open requests</h2>
          <Link to="/portal/requests">View all</Link>
        </div>
        {recent.isPending && <Loading label="Loading your requests" />}
        {recent.isError && <ErrorNotice error={recent.error} onRetry={() => void recent.refetch()} />}
        {recent.isSuccess && recent.data.items.length === 0 && <p className="empty">You have no open requests.</p>}
        {recent.isSuccess && (
          <ul className="card-list">
            {recent.data.items.map((r) => (
              <li key={r.reference}>
                <RequestCard request={r} />
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="home-kb-h" className="portal-section">
        <div className="section-head">
          <h2 id="home-kb-h">Browse help</h2>
          <Link to="/portal/knowledge">All articles</Link>
        </div>
        {categories.isSuccess && categories.data.length > 0 && (
          <ul className="tiles">
            {categories.data.map((c) => (
              <li key={c.id}>
                <Link to={`/portal/knowledge?category=${c.id}`} className="tile">
                  <strong>{c.name}</strong>
                  <span className="muted small">
                    {c.department} · {c.articleCount} {c.articleCount === 1 ? 'article' : 'articles'}
                  </span>
                  {c.description && <span className="small">{c.description}</span>}
                </Link>
              </li>
            ))}
          </ul>
        )}
        {latest.isSuccess && latest.data.items.length > 0 && (
          <>
            <h3>Recently added</h3>
            <ul className="link-list">
              {latest.data.items.map((a) => (
                <li key={a.slug}>
                  <Link to={`/portal/knowledge/${a.slug}`}>{a.title}</Link>
                  <span className="muted small"> {a.summary}</span>
                </li>
              ))}
            </ul>
          </>
        )}
        {latest.isSuccess && latest.data.items.length === 0 && <p className="empty">No help articles have been published yet.</p>}
      </section>
    </>
  )
}
