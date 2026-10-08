import { z } from 'zod'
import { api } from './http'

export const notificationSchema = z.object({
  id: z.string(),
  type: z.enum(['assigned', 'requester_reply', 'followed_update', 'mentioned', 'sla_approaching', 'sla_breached']),
  createdAt: z.string(),
  read: z.boolean(),
  reference: z.string(),
  subject: z.string(),
  /** For a mention: the internal note it is in (a link target, never text). */
  messageId: z.string().nullish(),
})
export type Notification = z.infer<typeof notificationSchema>

const listSchema = z.object({ unreadCount: z.number(), items: z.array(notificationSchema) })

/** The caller's own notifications. The server only returns ones whose ticket the caller can still read; a notification is a pointer, never access. */
export const listNotifications = () => api('GET', '/api/notifications?limit=20', { schema: listSchema })
export const markNotificationRead = (id: string) => api('POST', `/api/notifications/${encodeURIComponent(id)}/read`)
export const markAllNotificationsRead = () => api('POST', '/api/notifications/read-all')
