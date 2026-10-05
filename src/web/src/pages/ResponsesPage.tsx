import CannedManager from '../components/CannedManager'
import { PageHeader } from '../components/common'

/** Personal canned responses: only visible to and usable by their owner. */
export default function ResponsesPage() {
  return (
    <div className="page">
      <PageHeader title="My canned responses" subtitle="Reusable replies only you can see. Insert one from the reply box; you always review before posting." />
      <CannedManager scope={{ scope: 'Personal' }} />
    </div>
  )
}
