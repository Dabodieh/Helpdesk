import { useQuery } from '@tanstack/react-query'
import { Link, Navigate, useParams } from 'react-router-dom'
import { getPortalHelpTopic } from '../../api/portal'
import { ErrorNotice, Loading } from '../../components/common'

/**
 * A help topic is only a doorway: the server confirms the topic is still offered (its request form is usable by this requester right now) and the requester is
 * taken into the ordinary request form, which checks its own audience again and validates the submission.
 */
export default function PortalTopic() {
  const { topicId = '' } = useParams()
  const topic = useQuery({ queryKey: ['portal', 'topic', topicId], queryFn: () => getPortalHelpTopic(topicId), retry: false })
  if (topic.isPending) return <Loading label="Opening" />
  if (topic.isError) {
    return (
      <>
        <ErrorNotice error={topic.error} />
        <p>
          <Link to="/portal/submit">Back to request types</Link>
        </p>
      </>
    )
  }
  return <Navigate to={`/portal/submit/${topic.data.formId}`} replace />
}
