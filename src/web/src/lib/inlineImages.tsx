import type { ReactNode } from 'react'

/** A pasted image is written into a message body as [image:ID]; the id is an attachment of the same message. */
const MARKER = /\[image:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]/g

export const MAX_PASTED_IMAGES = 6
export const PASTE_TYPES = ['image/png', 'image/jpeg', 'image/gif']

export const imageMarker = (id: string) => `[image:${id}]`

/** Ids of the pasted images a body refers to (lower case, de-duplicated). */
export function imageIdsOf(body: string): string[] {
  return [...new Set([...body.matchAll(MARKER)].map((m) => m[1].toLowerCase()))]
}

/**
 * The body as React nodes: text through `renderText`, and each marker as an <img> ONLY when it names a downloadable attachment of this message
 * (so a marker can never point at an arbitrary URL). Anything else shows a short placeholder. No HTML is ever interpreted.
 */
export function renderBodyWithImages(
  body: string,
  attachments: { id: string; downloadable: boolean }[],
  hrefFor: (attachmentId: string) => string,
  renderText: (text: string) => ReactNode = (t) => t,
): ReactNode {
  if (!body.includes('[image:')) return renderText(body)
  const known = new Map(attachments.map((a) => [a.id.toLowerCase(), a]))
  const out: ReactNode[] = []
  let last = 0
  let i = 0
  for (const m of body.matchAll(MARKER)) {
    const at = m.index ?? 0
    if (at > last) out.push(<span key={`t${i}`}>{renderText(body.slice(last, at))}</span>)
    const att = known.get(m[1].toLowerCase())
    out.push(
      att?.downloadable ? (
        <img key={`i${i}`} className="msg-img" src={hrefFor(att.id)} alt="Pasted image" loading="lazy" />
      ) : (
        <span key={`i${i}`} className="muted small">
          [image not available]
        </span>
      ),
    )
    last = at + m[0].length
    i++
  }
  if (last < body.length) out.push(<span key="tail">{renderText(body.slice(last))}</span>)
  return out
}
