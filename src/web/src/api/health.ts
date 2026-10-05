import { z } from 'zod'

const healthSchema = z.enum(['Healthy', 'Degraded', 'Unhealthy'])

export async function fetchHealth(path: '/health/live' | '/health/ready') {
  const res = await fetch(path)
  const text = (await res.text()).trim()
  const parsed = healthSchema.safeParse(text)
  return { ok: res.ok, status: parsed.success ? parsed.data : ('Unhealthy' as const) }
}
