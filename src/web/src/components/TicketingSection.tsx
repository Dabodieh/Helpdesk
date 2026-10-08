import type { Department } from '../api/schemas'
import { Tabs } from './common'
import FormsSection from './FormsSection'
import HelpTopicsSection from './HelpTopicsSection'
import TicketFieldsSection from './TicketFieldsSection'
import TicketTypesSection from './TicketTypesSection'

/** Ticketing configuration of a department: what kinds of ticket exist, which fields they carry, and how requesters submit them. */
export default function TicketingSection({ department }: { department: Department }) {
  return (
    <Tabs
      label="Ticketing configuration"
      param="tab"
      tabs={[
        { id: 'types', label: 'Ticket types', content: <TicketTypesSection department={department} /> },
        { id: 'fields', label: 'Ticket fields', content: <TicketFieldsSection department={department} /> },
        { id: 'forms', label: 'Request forms', content: <FormsSection department={department} /> },
        { id: 'topics', label: 'Help topics', content: <HelpTopicsSection department={department} /> },
      ]}
    />
  )
}
