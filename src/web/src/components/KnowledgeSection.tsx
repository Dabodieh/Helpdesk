import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { fieldErrors } from '../api/errors'
import {
  archiveArticle,
  createArticle,
  createCategory,
  getArticle,
  listArticles,
  listCategories,
  listOwners,
  publishArticle,
  reorderCategories,
  saveDraft,
  setMaintenance,
  updateCategory,
  type ArticleAdmin,
  type ArticleListItem,
  type CategoryAdmin,
} from '../api/knowledgeAdmin'
import type { Department } from '../api/schemas'
import { formatDateTime } from '../lib/format'
import { Markdown } from '../lib/markdown'
import { ErrorNotice, Field, Loading } from './common'
import Dialog from './Dialog'

function ArticleEditor({ department, id, categories, onClose }: { department: Department; id?: string; categories: CategoryAdmin[]; onClose: () => void }) {
  const qc = useQueryClient()
  const loaded = useQuery({ queryKey: ['knowledge-article', department.id, id], queryFn: () => getArticle(department.id, id!), enabled: !!id })
  if (id && loaded.isPending) {
    return (
      <Dialog title="Article" onClose={onClose}>
        <Loading label="Loading the article" />
      </Dialog>
    )
  }
  if (id && loaded.isError) {
    return (
      <Dialog title="Article" onClose={onClose}>
        <ErrorNotice error={loaded.error} onRetry={() => void loaded.refetch()} />
      </Dialog>
    )
  }
  // Keyed by id and version so a saved article re-initialises the form from the server's copy.
  return <EditorForm key={loaded.data?.version ?? 'new'} department={department} article={loaded.data} categories={categories} onClose={onClose} onChanged={() => qc.invalidateQueries({ queryKey: ['knowledge'] })} />
}

function EditorForm({ department, article, categories, onClose, onChanged }: { department: Department; article?: ArticleAdmin; categories: CategoryAdmin[]; onClose: () => void; onChanged: () => Promise<unknown> }) {
  const qc = useQueryClient()
  const [title, setTitle] = useState(article?.draft.title ?? '')
  const [summary, setSummary] = useState(article?.draft.summary ?? '')
  const [body, setBody] = useState(article?.draft.body ?? '')
  const [categoryId, setCategoryId] = useState(article?.categoryId ?? '')
  const [visibility, setVisibility] = useState(article?.visibility ?? 'Everyone')
  const [preview, setPreview] = useState(false)

  const save = useMutation({
    mutationFn: async (action: 'draft' | 'publish' | 'archive') => {
      const input = { title: title.trim(), summary: summary.trim(), body, visibility, categoryId: categoryId || null, clearCategory: !categoryId }
      let current = article ? await saveDraft(department.id, article.id, { ...input, version: article.version }) : await createArticle(department.id, input)
      if (action === 'publish') current = await publishArticle(department.id, current.id, current.version)
      if (action === 'archive') current = await archiveArticle(department.id, current.id, current.version)
      return { current, action }
    },
    onSuccess: async ({ current, action }) => {
      await onChanged()
      qc.setQueryData(['knowledge-article', department.id, current.id], current)
      if (action !== 'draft' || !article) onClose()
    },
  })
  const server = fieldErrors(save.error)
  function submit(e: FormEvent) {
    e.preventDefault()
    if (title.trim()) save.mutate('draft')
  }
  const published = article?.status === 'Published'
  return (
    <Dialog title={article ? `Edit “${article.draft.title || 'article'}”` : 'New article'} onClose={onClose}>
      <form className="dialog-form wide" onSubmit={submit} aria-label="Article">
        {article && (
          <p className="muted small" role="status">
            {article.status === 'Draft' && 'Draft: not visible to requesters.'}
            {published && (article.hasUnpublishedChanges ? 'Published. Your edits are saved as a draft and are not public until you publish them.' : 'Published and up to date.')}
            {article.status === 'Archived' && 'Archived: hidden from requesters. Publishing makes it visible again.'}
            {article.status !== 'Draft' ? ` Helpful: ${article.helpful} yes, ${article.notHelpful} no.` : ''}
          </p>
        )}
        <Field label="Title" error={server.title}>
          {(p) => <input {...p} data-autofocus value={title} maxLength={200} required onChange={(e) => setTitle(e.target.value)} />}
        </Field>
        <Field label="Summary" hint="One or two sentences shown in search results." error={server.summary}>
          {(p) => <textarea {...p} rows={2} maxLength={500} value={summary} onChange={(e) => setSummary(e.target.value)} />}
        </Field>
        <div className="editor-tabs">
          <button type="button" aria-pressed={!preview} onClick={() => setPreview(false)}>
            Write
          </button>
          <button type="button" aria-pressed={preview} onClick={() => setPreview(true)}>
            Preview
          </button>
        </div>
        {preview ? (
          <div className="preview" aria-label="Preview">
            <Markdown source={body || '*Nothing to preview yet.*'} />
          </div>
        ) : (
          <Field label="Article text" hint="Markdown: # headings, - lists, 1. steps, **bold**, [link text](https://…). Raw HTML is not rendered." error={server.body}>
            {(p) => <textarea {...p} rows={12} value={body} onChange={(e) => setBody(e.target.value)} />}
          </Field>
        )}
        <Field label="Category" error={server.categoryid}>
          {(p) => (
            <select {...p} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">No category</option>
              {categories.filter((c) => c.isActive || c.id === article?.categoryId).map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label="Who can read it" error={server.visibility}>
          {(p) => (
            <select {...p} value={visibility} onChange={(e) => setVisibility(e.target.value)}>
              <option value="Everyone">Every signed-in employee</option>
              <option value="Requesters">Only people who have raised a request with this department</option>
            </select>
          )}
        </Field>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Close
          </button>
          {published && (
            <button type="button" disabled={save.isPending} onClick={() => save.mutate('archive')}>
              Archive
            </button>
          )}
          <button type="submit" disabled={save.isPending || !title.trim()}>
            Save draft
          </button>
          <button type="button" className="primary" disabled={save.isPending || !title.trim() || !body.trim()} onClick={() => save.mutate('publish')}>
            {published ? 'Publish changes' : 'Publish'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Owner and review date of one article. Saving a new date is how an article is marked as reviewed. */
function MaintenanceDialog({ department, id, onClose }: { department: Department; id: string; onClose: () => void }) {
  const qc = useQueryClient()
  const article = useQuery({ queryKey: ['knowledge-article', department.id, id], queryFn: () => getArticle(department.id, id) })
  const owners = useQuery({ queryKey: ['knowledge-owners', department.id], queryFn: () => listOwners(department.id) })
  if (!article.data || !owners.data) {
    return (
      <Dialog title="Owner and review" onClose={onClose}>
        {article.isError || owners.isError ? <ErrorNotice error={article.error ?? owners.error} /> : <Loading label="Loading" />}
      </Dialog>
    )
  }
  return <MaintenanceForm department={department} article={article.data} owners={owners.data} onClose={onClose} onSaved={() => qc.invalidateQueries({ queryKey: ['knowledge'] })} />
}

function MaintenanceForm({ department, article, owners, onClose, onSaved }: { department: Department; article: ArticleAdmin; owners: { id: string; name: string }[]; onClose: () => void; onSaved: () => Promise<unknown> }) {
  const qc = useQueryClient()
  const [owner, setOwner] = useState(article.ownerUserId ?? '')
  const [reviewOn, setReviewOn] = useState(article.reviewOn ?? '')
  const save = useMutation({
    mutationFn: () => setMaintenance(department.id, article.id, { ownerUserId: owner || null, reviewOn: reviewOn || null, version: article.version }),
    onSuccess: async (saved) => {
      qc.setQueryData(['knowledge-article', department.id, saved.id], saved)
      await onSaved()
      onClose()
    },
  })
  const server = fieldErrors(save.error)
  return (
    <Dialog title={`Owner and review: ${article.draft.title}`} onClose={onClose}>
      <form
        className="dialog-form"
        aria-label="Owner and review"
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate()
        }}
      >
        <Field label="Owner" hint="Responsible for keeping the article correct. Only members who can manage knowledge in this department can own articles." error={server.owneruserid}>
          {(p) => (
            <select {...p} value={owner} onChange={(e) => setOwner(e.target.value)}>
              <option value="">Unowned</option>
              {owners.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label="Review by" hint="Optional. Published articles become overdue the day after this date. Saving a later date marks the article as reviewed." error={server.reviewon}>
          {(p) => <input {...p} type="date" value={reviewOn} onChange={(e) => setReviewOn(e.target.value)} />}
        </Field>
        <ErrorNotice error={save.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={save.isPending}>
            Save
          </button>
        </div>
      </form>
    </Dialog>
  )
}

function CategoryDialog({ department, categories, onClose }: { department: Department; categories: CategoryAdmin[]; onClose: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const refresh = () => qc.invalidateQueries({ queryKey: ['knowledge-categories', department.id] })
  const add = useMutation({
    mutationFn: () => createCategory(department.id, { name: name.trim(), description: description.trim() }),
    onSuccess: async () => {
      setName('')
      setDescription('')
      await refresh()
    },
  })
  const toggle = useMutation({ mutationFn: (c: CategoryAdmin) => updateCategory(department.id, c.id, { isActive: !c.isActive, version: c.version }), onSettled: refresh })
  const reorder = useMutation({ mutationFn: (ids: string[]) => reorderCategories(department.id, ids), onSuccess: (d) => qc.setQueryData(['knowledge-categories', department.id], d) })
  const server = fieldErrors(add.error)
  const move = (i: number, d: -1 | 1) => {
    const ids = categories.map((c) => c.id)
    const j = i + d
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j]!, ids[i]!]
    reorder.mutate(ids)
  }
  return (
    <Dialog title="Categories" onClose={onClose}>
      {categories.length === 0 && <p className="empty small">No categories yet.</p>}
      <ul className="views-list">
        {categories.map((c, i) => (
          <li key={c.id}>
            <span className="row-actions">
              <button type="button" className="ghost" aria-label={`Move ${c.name} up`} disabled={i === 0} onClick={() => move(i, -1)}>
                ↑
              </button>
              <button type="button" className="ghost" aria-label={`Move ${c.name} down`} disabled={i === categories.length - 1} onClick={() => move(i, 1)}>
                ↓
              </button>
            </span>
            <span className="grow">
              <strong>{c.name}</strong> <span className="muted small">{c.articleCount} articles{c.isActive ? '' : ' · inactive'}</span>
            </span>
            <button type="button" disabled={toggle.isPending} onClick={() => toggle.mutate(c)}>
              {c.isActive ? 'Deactivate' : 'Activate'}
            </button>
          </li>
        ))}
      </ul>
      <form
        className="dialog-form"
        aria-label="New category"
        onSubmit={(e) => {
          e.preventDefault()
          if (name.trim()) add.mutate()
        }}
      >
        <h3>Add a category</h3>
        <Field label="Name" error={server.name}>
          {(p) => <input {...p} value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label="Description (optional)" error={server.description}>
          {(p) => <input {...p} value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <ErrorNotice error={add.error ?? toggle.error ?? reorder.error} />
        <div className="dialog-actions">
          <button type="button" onClick={onClose}>
            Close
          </button>
          <button type="submit" className="primary" disabled={add.isPending || !name.trim()}>
            Add category
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** Department knowledge base administration (permission department.knowledge.manage). */
export default function KnowledgeSection({ department }: { department: Department }) {
  const [status, setStatus] = useState('')
  const [search, setSearch] = useState('')
  const [review, setReview] = useState('')
  const [maintaining, setMaintaining] = useState<string | null>(null)
  const [editing, setEditing] = useState<ArticleListItem | 'new' | null>(null)
  const [categoriesOpen, setCategoriesOpen] = useState(false)
  const categories = useQuery({ queryKey: ['knowledge-categories', department.id], queryFn: () => listCategories(department.id) })
  const articles = useQuery({ queryKey: ['knowledge', 'articles', department.id, status, search, review], queryFn: () => listArticles(department.id, { status: status || undefined, q: search.trim() || undefined, review: review || undefined }) })
  return (
    <section aria-labelledby="kb-h">
      <div className="section-head">
        <h2 id="kb-h">Knowledge base</h2>
        <span className="page-actions">
          <button type="button" onClick={() => setCategoriesOpen(true)}>
            Categories
          </button>
          <button type="button" className="primary" onClick={() => setEditing('new')}>
            New article
          </button>
        </span>
      </div>
      <p className="muted">Help articles for requesters. Only published articles are visible; editing a published article never changes what requesters read until you publish.</p>
      <div className="filters-row">
        <label className="fsel">
          <span>Status</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            <option>Draft</option>
            <option>Published</option>
            <option>Archived</option>
          </select>
        </label>
        <label className="fsel">
          <span>Maintenance</span>
          <select value={review} onChange={(e) => setReview(e.target.value)}>
            <option value="">All</option>
            <option value="overdue">Overdue for review</option>
            <option value="unowned">Unowned</option>
          </select>
        </label>
        <label className="fsel grow">
          <span>Search titles</span>
          <input type="search" value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
      </div>
      {articles.isPending && <Loading label="Loading articles" />}
      {articles.isError && <ErrorNotice error={articles.error} onRetry={() => void articles.refetch()} />}
      {articles.isSuccess && articles.data.length === 0 && <p className="empty">No articles found.</p>}
      {articles.isSuccess && articles.data.length > 0 && (
        <table className="grid">
          <caption className="sr-only">Knowledge articles of {department.name}</caption>
          <thead>
            <tr>
              <th scope="col">Title</th>
              <th scope="col">Status</th>
              <th scope="col">Category</th>
              <th scope="col">Owner</th>
              <th scope="col">Review by</th>
              <th scope="col">Updated</th>
              <th scope="col" />
            </tr>
          </thead>
          <tbody>
            {articles.data.map((a) => (
              <tr key={a.id}>
                <td>
                  <strong>{a.title}</strong> {a.visibility === 'Requesters' && <span className="badge">Requesters only</span>}
                </td>
                <td>
                  {a.status} {a.hasUnpublishedChanges && <span className="badge">Unpublished edits</span>}
                </td>
                <td>{a.category ?? <span className="muted">None</span>}</td>
                <td>{a.owner ?? <span className="badge">Unowned</span>}</td>
                <td>
                  {a.reviewOverdue ? <span className="badge">Overdue</span> : null} {a.reviewOn ?? (a.status === 'Published' ? <span className="muted">No review date</span> : null)}
                </td>
                <td>{formatDateTime(a.updatedAt)}</td>
                <td className="row-actions">
                  <button type="button" onClick={() => setEditing(a)}>
                    Edit
                  </button>
                  <button type="button" onClick={() => setMaintaining(a.id)}>
                    Owner &amp; review
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && <ArticleEditor department={department} id={editing === 'new' ? undefined : editing.id} categories={categories.data ?? []} onClose={() => setEditing(null)} />}
      {maintaining && <MaintenanceDialog department={department} id={maintaining} onClose={() => setMaintaining(null)} />}
      {categoriesOpen && <CategoryDialog department={department} categories={categories.data ?? []} onClose={() => setCategoriesOpen(false)} />}
    </section>
  )
}
