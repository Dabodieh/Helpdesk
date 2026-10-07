import type { TicketDetail } from '../api/ticketSchemas'
import CoreProperties from './CoreProperties'
import Followers from './Followers'
import RequesterSummary from './RequesterSummary'
import { SlaPanel } from './Sla'
import FieldSummary from './TicketProperties'
import { RelatedTickets, type TicketDialogName } from './TicketLinks'

/**
 * Everything about the ticket that is not the conversation, in one column: requester, core properties, SLA, custom fields and collaboration (followers,
 * related tickets). Each section keeps its own data source (cached queries); nothing is repeated in the header. Mutation controls follow the same
 * permissions as before and stay disabled on a merged ticket.
 */
export default function PropertiesPanel({
  ticket: t,
  routeRef,
  canUpdate,
  onProblem,
  onOpen,
  onHide,
  overlay,
}: {
  ticket: TicketDetail
  routeRef: string
  canUpdate: boolean
  onProblem: (e: unknown) => void
  onOpen: (dialog: TicketDialogName) => void
  onHide: () => void
  overlay: boolean
}) {
  const merged = !!t.mergedInto
  return (
    <aside className={overlay ? 'props-panel props-overlay' : 'props-panel'} aria-label="Ticket properties panel">
      <div className="props-head">
        <strong>Properties</strong>
        <button type="button" className="ghost" onClick={onHide} aria-label="Hide properties">
          Hide
        </button>
      </div>
      <RequesterSummary ticket={t} />
      <CoreProperties ticket={t} routeRef={routeRef} onProblem={onProblem} />
      <SlaPanel reference={t.reference} createdAt={t.createdAt} merged={merged} />
      <FieldSummary reference={t.reference} canEdit={canUpdate} onEdit={() => onOpen('details')} />
      <section className="prop-section" aria-label="Collaboration">
        {!merged && <Followers ticket={t} canManage={canUpdate} />}
        <RelatedTickets ticket={t} canEdit={canUpdate} onLink={() => onOpen('link')} />
      </section>
    </aside>
  )
}
