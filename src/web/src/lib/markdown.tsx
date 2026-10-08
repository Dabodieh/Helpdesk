import type { ReactNode } from 'react'

/**
 * A deliberately small Markdown renderer for knowledge articles. It builds React elements (never HTML strings), so there is no raw-HTML
 * path at all: `<script>`, inline HTML and event handlers are just text. Links are limited to http(s), mailto and in-app paths.
 * Supported: headings (# to ###), paragraphs, bullet and numbered lists, fenced code, **bold**, *italic*, `code`, [text](url).
 */

const SAFE_LINK = /^(https?:\/\/|mailto:|\/(?!\/))/i

function safeHref(url: string): string | null {
  const trimmed = url.trim()
  if (trimmed.includes(String.fromCharCode(92))) return null // a backslash: browsers read "/" + backslash + "host" like "//host", an external link dressed as an in-app one (SEC-013)
  return SAFE_LINK.test(trimmed) ? trimmed : null
}

function inline(text: string, keyBase: string): ReactNode[] {
  const out: ReactNode[] = []
  const pattern = /(\*\*[^*]+\*\*|\*[^*\s][^*]*\*|`[^`]+`|\[[^\]]+\]\([^)\s]+\))/g
  let last = 0
  let match: RegExpExecArray | null
  let i = 0
  while ((match = pattern.exec(text)) !== null) {
    if (match.index > last) out.push(text.slice(last, match.index))
    const token = match[0]
    const key = `${keyBase}-${i++}`
    if (token.startsWith('**')) out.push(<strong key={key}>{token.slice(2, -2)}</strong>)
    else if (token.startsWith('`')) out.push(<code key={key}>{token.slice(1, -1)}</code>)
    else if (token.startsWith('*')) out.push(<em key={key}>{token.slice(1, -1)}</em>)
    else {
      const [, label, url] = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(token)!
      const href = safeHref(url!)
      out.push(
        href ? (
          <a key={key} href={href} target={href.startsWith('/') ? undefined : '_blank'} rel="noopener noreferrer">
            {label}
          </a>
        ) : (
          <span key={key}>{label}</span>
        ),
      )
    }
    last = match.index + token.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

function renderMarkdown(source: string): ReactNode[] {
  const lines = source.replace(/\r\n/g, '\n').split('\n')
  const blocks: ReactNode[] = []
  let i = 0
  let key = 0
  while (i < lines.length) {
    const line = lines[i]!
    if (line.trim() === '') {
      i++
      continue
    }
    if (line.startsWith('```')) {
      const code: string[] = []
      i++
      while (i < lines.length && !lines[i]!.startsWith('```')) code.push(lines[i++]!)
      i++
      blocks.push(
        <pre key={key++}>
          <code>{code.join('\n')}</code>
        </pre>,
      )
      continue
    }
    const heading = /^(#{1,3})\s+(.*)$/.exec(line)
    if (heading) {
      const level = heading[1]!.length + 1 // the article title is the page's h1
      const Tag = `h${level}` as 'h2' | 'h3' | 'h4'
      blocks.push(<Tag key={key++}>{inline(heading[2]!, `h${key}`)}</Tag>)
      i++
      continue
    }
    if (/^\s*[-*]\s+/.test(line)) {
      const items: string[] = []
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i]!)) items.push(lines[i++]!.replace(/^\s*[-*]\s+/, ''))
      blocks.push(
        <ul key={key++}>
          {items.map((t, n) => (
            <li key={n}>{inline(t, `u${key}-${n}`)}</li>
          ))}
        </ul>,
      )
      continue
    }
    if (/^\s*\d+[.)]\s+/.test(line)) {
      const items: string[] = []
      while (i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i]!)) items.push(lines[i++]!.replace(/^\s*\d+[.)]\s+/, ''))
      blocks.push(
        <ol key={key++}>
          {items.map((t, n) => (
            <li key={n}>{inline(t, `o${key}-${n}`)}</li>
          ))}
        </ol>,
      )
      continue
    }
    const para: string[] = []
    while (i < lines.length && lines[i]!.trim() !== '' && !/^(#{1,3}\s|```|\s*[-*]\s+|\s*\d+[.)]\s+)/.test(lines[i]!)) para.push(lines[i++]!)
    blocks.push(<p key={key++}>{inline(para.join(' '), `p${key}`)}</p>)
  }
  return blocks
}

export function Markdown({ source }: { source: string }) {
  return <div className="markdown">{renderMarkdown(source)}</div>
}
