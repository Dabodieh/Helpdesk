import CannedManager from '../components/CannedManager'
import { PageHeader } from '../components/common'

/** Personal canned responses: only visible to and usable by their owner. */
export default function ResponsesPage() {
  return (
    <div className="page">
      <PageHeader title="My canned responses" subtitle="Reusable replies only you can see." />
      <CannedManager scope={{ scope: 'Personal' }} />
    </div>
  )
}
