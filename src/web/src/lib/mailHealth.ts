import type { Tone } from '../components/common'

/** Presentation of mailbox health (status and reason codes from the server), shared by the mailbox admin and the department dashboard. */
export const HEALTH_TONE: Record<string, Tone> = { Healthy: 'ok', Attention: 'warn', Failed: 'bad', NotConfigured: 'off', Disabled: 'off' }
export const HEALTH_LABEL: Record<string, string> = { Healthy: 'Healthy', Attention: 'Attention', Failed: 'Failed', NotConfigured: 'Not set up', Disabled: 'Disabled' }
export const HEALTH_REASON: Record<string, string> = {
  subscription_not_created: 'No inbound subscription yet',
  subscription_missing: 'Inbound subscription is missing',
  subscription_failed: 'Inbound subscription failed',
  subscription_expired: 'Inbound subscription expired',
  subscription_expiring: 'Inbound subscription renewal is overdue',
  sync_not_started: 'Waiting for the first mail check',
  sync_failing: 'Inbound sync is failing',
  sync_stale: 'Inbound sync has not run recently',
  outbound_uncertain: 'Some sends have an uncertain outcome',
  outbound_failed: 'Some sends failed',
  inbound_failed: 'Some inbound mail failed',
  mailbox_inactive: 'Mailbox is deactivated',
}
