import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type RefObject } from 'react'
import { describeError } from '../api/errors'
import { pickMacros, runMacro, type MacroPick } from '../api/macros'
import type { TicketDetail } from '../api/ticketSchemas'
import { useApplyTicket } from '../hooks/useApplyTicket'
import type { ComposerHandle } from './Composer'
import { ErrorNotice, Loading } from './common'
import Dialog from './Dialog'

/**
 * Run a macro on the open ticket: search, preview what it will do, apply. The page stays on the ticket. A Public Reply action only fills the reply box
 * with an editable draft; nothing is sent until the agent presses Send in the normal composer.
 */
export default function MacroRunner({
  ticket: t,
  routeRef,
  composerRef,
  onClose,
}: {
  ticket: TicketDetail
  routeRef: string
  composerRef: RefObject<ComposerHandle | null>
  onClose: () => void
}) {
  const qc = useQueryClient()
  const apply = useApplyTicket(routeRef)
  const [text, setText] = useState('')
  const [term, setTerm] = useState('')
  const [chosen, setChosen] = useState<MacroPick | null>(null)
  useEffect(() => {
    const h = setTimeout(() => setTerm(text.trim()), 250)
    return () => clearTimeout(h)
  }, [text])
  const macros = useQuery({ queryKey: ['ticket', t.id, 'macros', term], queryFn: () => pickMacros(t.id, term) })
  const run = useMutation({
    mutationFn: (m: MacroPick) => runMacro(t.id, m.id, t.version),
    onSuccess: async (res) => {
      apply(res.ticket)
      await qc.invalidateQueries({ queryKey: ['ticket', t.id] })
      if (res.draft) composerRef.current?.insertDraft(res.draft.body)
      onClose()
    },
  })

  if (chosen) {
    return (
      <Dialog title={chosen.name} onClose={onClose}>
        <div className="dialog-body macro-preview" aria-label="Macro preview">
          <p>Will:</p>
          <ul>
            {chosen.summary.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
          <p className="muted small">Nothing is emailed. A reply action only fills the reply box so you can edit it and press Send.</p>
          {run.isError && (
            <p role="alert" className="notice error">
              {describeError(run.error)}
            </p>
          )}
          <div className="dialog-actions">
            <button type="button" onClick={() => setChosen(null)}>
              Cancel
            </button>
            <button type="button" className="primary" data-autofocus disabled={run.isPending} onClick={() => run.mutate(chosen)}>
              {run.isPending ? 'Applying…' : 'Apply macro'}
            </button>
          </div>
        </div>
      </Dialog>
    )
  }

  return (
    <Dialog title="Run macro" onClose={onClose}>
      <div className="dialog-body">
        <label htmlFor="macro-search">Search macros</label>
        <input id="macro-search" data-autofocus type="search" value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
        {macros.isPending && <Loading label="Loading macros" />}
        {macros.isError && <ErrorNotice error={macros.error} onRetry={() => void macros.refetch()} />}
        {macros.isSuccess && macros.data.length === 0 && <p className="empty small">No macros available for this ticket.</p>}
        <ul className="candidate-list macro-list" aria-label="Macros">
          {macros.data?.map((m) => (
            <li key={m.id}>
              <button type="button" onClick={() => setChosen(m)}>
                <strong>{m.name}</strong>
                {m.description ? <span className="muted small"> · {m.description}</span> : null}
                <span className="muted small macro-summary">{m.summary.join(' · ')}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </Dialog>
  )
}
