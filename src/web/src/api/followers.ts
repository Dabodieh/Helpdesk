import { z } from 'zod'
import { api } from './http'

const tk = (ticket: string) => `/api/tickets/${encodeURIComponent(ticket)}`

export const followersSchema = z.object({
  items: z.array(z.object({ userId: z.string(), displayName: z.string(), addedAt: z.string() })),
  /** Whether the signed-in user follows the ticket. */
  following: z.boolean(),
})
export type Followers = z.infer<typeof followersSchema>

export const followerCandidateSchema = z.object({ userId: z.string(), displayName: z.string(), email: z.string().nullish() })
export type FollowerCandidate = z.infer<typeof followerCandidateSchema>

export const getFollowers = (ticket: string) => api('GET', `${tk(ticket)}/followers`, { schema: followersSchema })

/** Follow or unfollow as the signed-in user (needs only read access). */
export const followMe = (ticket: string) => api('POST', `${tk(ticket)}/followers/me`, { schema: followersSchema })
export const unfollowMe = (ticket: string) => api('DELETE', `${tk(ticket)}/followers/me`, { schema: followersSchema })

/** Add or remove somebody else (needs tickets.update; the server only accepts readers of the ticket department). */
export const addFollower = (ticket: string, userId: string) => api('POST', `${tk(ticket)}/followers/${encodeURIComponent(userId)}`, { schema: followersSchema })
export const removeFollower = (ticket: string, userId: string) => api('DELETE', `${tk(ticket)}/followers/${encodeURIComponent(userId)}`, { schema: followersSchema })

export const searchFollowerCandidates = (ticket: string, q: string) =>
  api('GET', `${tk(ticket)}/follower-candidates?q=${encodeURIComponent(q)}`, { schema: z.array(followerCandidateSchema) })
