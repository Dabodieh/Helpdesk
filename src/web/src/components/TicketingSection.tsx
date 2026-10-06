import type { Department } from '../api/schemas'
import { Tabs } from './common'
import FormsSection from './FormsSection'
import TicketFieldsSection from './TicketFieldsSection'
import TicketTypesSection from './TicketTypesSection'

/** Ticketing configuration of a department: what kinds of ticket exist, which fields they carry, and how requesters submit them. */
export default function TicketingSection({ department }: { department: Department }) {
  return (
    <Tabs
      label="Ticketing configuration"
      tabs={[
        { id: 'types', label: 'Ticket types', content: <TicketTypesSection department={department} /> },
        { id: 'fields', label: 'Ticket fields', content: <TicketFieldsSection department={department} /> },
        { id: 'forms', label: 'Request forms', content: <FormsSection department={department} /> },
      ]}
    />
  )
}
