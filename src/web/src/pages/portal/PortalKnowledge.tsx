import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { type FormEvent } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { getKnowledgeArticle, listKnowledgeCategories, searchKnowledge, sendArticleFeedback } from '../../api/portal'
import { ErrorNotice, Loading } from '../../components/common'
import { formatDateTime } from '../../lib/format'
import { Markdown } from '../../lib/markdown'

/** Knowledge search and browse: published help only, paged by the server. */
export function KnowledgeHome() {
  const [params, setParams] = useSearchParams()
  const query = params.get('q') ?? ''
  const category = params.get('category') ?? undefined
  const categories = useQuery({ queryKey: ['portal', 'knowledge', 'categories'], queryFn: listKnowledgeCategories, staleTime: 60_000 })
  const results = useInfiniteQuery({
    queryKey: ['portal', 'knowledge', 'search', query, category],
    queryFn: ({ pageParam }) => searchKnowledge({ q: query || undefined, categoryId: category, cursor: pageParam }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
  })
  const items = results.data?.pages.flatMap((p) => p.items) ?? []
  const activeCategory = categories.data?.find((c) => c.id === category)

  function search(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const text = String(new FormData(e.currentTarget).get('q') ?? '')
    const next = new URLSearchParams()
    if (text.trim()) next.set('q', text.trim())
    if (category) next.set('category', category)
    setParams(next)
  }

  return (
    <>
      <h1>Knowledge</h1>
      <form className="portal-search" role="search" onSubmit={search}>
        <label htmlFor="kb-q" className="sr-only">
          Search help articles
        </label>
        <input id="kb-q" name="q" key={query} type="search" defaultValue={query} placeholder="Search help articles" />
        <button type="submit" className="primary">
          Search
        </button>
      </form>
      {categories.isSuccess && categories.data.length > 0 && (
        <nav aria-label="Categories" className="chips-row">
          <Link to="/portal/knowledge" className={!category ? 'chip-link active' : 'chip-link'} aria-current={!category ? 'true' : undefined}>
            All
          </Link>
          {categories.data.map((c) => (
            <Link key={c.id} to={`/portal/knowledge?category=${c.id}`} className={c.id === category ? 'chip-link active' : 'chip-link'} aria-current={c.id === category ? 'true' : undefined}>
              {c.name}
            </Link>
          ))}
        </nav>
      )}
      <h2 className="sr-only">Results</h2>
      {(query || activeCategory) && (
        <p className="muted" role="status">
          {query ? `Results for “${query}”` : ''}
          {activeCategory ? ` in ${activeCategory.name}` : ''}
        </p>
      )}
      {results.isPending && <Loading label="Searching" />}
      {results.isError && <ErrorNotice error={results.error} onRetry={() => void results.refetch()} />}
      {results.isSuccess && items.length === 0 && (
        <div className="empty">
          <p>No articles found.</p>
          <p>
            <Link to="/portal/submit">Submit a request</Link> and we’ll help you directly.
          </p>
        </div>
      )}
      <ul className="article-list">
        {items.map((a) => (
          <li key={a.slug}>
            <Link to={`/portal/knowledge/${a.slug}`}>
              <strong>{a.title}</strong>
            </Link>
            <span className="muted small">
              {a.department}
              {a.category ? ` · ${a.category}` : ''}
            </span>
            {a.summary && <span>{a.summary}</span>}
          </li>
        ))}
      </ul>
      {results.hasNextPage && (
        <p>
          <button type="button" disabled={results.isFetchingNextPage} onClick={() => void results.fetchNextPage()}>
            {results.isFetchingNextPage ? 'Loading…' : 'Show more'}
          </button>
        </p>
      )}
    </>
  )
}

/** One published article. Draft or archived articles are plain "not found". */
export function KnowledgeArticle() {
  const { slug = '' } = useParams()
  const qc = useQueryClient()
  const article = useQuery({ queryKey: ['portal', 'knowledge', 'article', slug], queryFn: () => getKnowledgeArticle(slug) })
  const vote = useMutation({
    mutationFn: (helpful: boolean) => sendArticleFeedback(slug, helpful),
    onSuccess: (data) => qc.setQueryData(['portal', 'knowledge', 'article', slug], data),
  })
  if (article.isPending) return <Loading label="Loading the article" />
  if (article.isError) {
    return (
      <>
        <ErrorNotice error={article.error} />
        <p>
          <Link to="/portal/knowledge">← Knowledge</Link>
        </p>
      </>
    )
  }
  const a = article.data
  return (
    <article className="article">
      <p>
        <Link to="/portal/knowledge">← Knowledge</Link>
      </p>
      <p className="muted small">
        {a.department}
        {a.category ? ` · ${a.category}` : ''}
      </p>
      <h1>{a.title}</h1>
      {a.summary && <p className="lead">{a.summary}</p>}
      <Markdown source={a.body} />
      <p className="muted small">Published {formatDateTime(a.publishedAt)}</p>
      <section className="feedback" aria-labelledby="fb-h">
        <h2 id="fb-h">Was this helpful?</h2>
        <div className="seg" role="group" aria-label="Feedback">
          <button type="button" aria-pressed={a.myFeedback === true} disabled={vote.isPending} onClick={() => vote.mutate(true)}>
            Yes
          </button>
          <button type="button" aria-pressed={a.myFeedback === false} disabled={vote.isPending} onClick={() => vote.mutate(false)}>
            No
          </button>
        </div>
        {a.myFeedback !== null && a.myFeedback !== undefined && <p role="status" className="muted small">Thanks for the feedback.</p>}
        <p>
          Still need help? <Link to="/portal/submit">Submit a request</Link>
        </p>
      </section>
    </article>
  )
}
