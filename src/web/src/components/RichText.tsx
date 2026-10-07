import { useEffect, useRef } from 'react'

/** Sanitised HTML is shown ONLY inside a sandboxed frame: no scripts, no forms, no remote content (CSP). The server has already sanitised it. */
export function SafeHtmlFrame({ html, title }: { html: string; title: string }) {
  const doc =
    `<!doctype html><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'">` +
    `<base target="_blank"><style>body{font:14px system-ui,sans-serif;margin:8px;color:#222;background:#fff;word-wrap:break-word}</style>${html}`
  return <iframe className="formatted" title={title} sandbox="allow-popups" referrerPolicy="no-referrer" srcDoc={doc} />
}

const COMMANDS = [
  { command: 'bold', label: 'Bold', text: 'B' },
  { command: 'italic', label: 'Italic', text: 'I' },
  { command: 'underline', label: 'Underline', text: 'U' },
] as const

/**
 * A small formatting editor (bold, italic, underline, links). It only produces candidate HTML: the server sanitises it on save, so nothing here is trusted. The content
 * is set from `value` when `resetKey` changes (switching department), not on every keystroke, so the caret never jumps.
 */
export default function RichText({ label, value, resetKey, onChange }: { label: string; value: string; resetKey: string; onChange: (html: string) => void }) {
  const area = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (area.current) area.current.innerHTML = value
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only a change of context resets the editor
  }, [resetKey])

  const run = (command: string, argument?: string) => {
    area.current?.focus()
    if (typeof document.execCommand === 'function') document.execCommand(command, false, argument)
    onChange(area.current?.innerHTML ?? '')
  }
  const link = () => {
    const url = window.prompt('Link address (https://…)', 'https://')
    if (url && /^(https?:\/\/|mailto:)/i.test(url.trim())) run('createLink', url.trim())
  }

  return (
    <div className="richtext">
      <div className="richtext-bar" role="toolbar" aria-label={`${label} formatting`}>
        {COMMANDS.map((c) => (
          <button key={c.command} type="button" className="ghost" aria-label={c.label} onClick={() => run(c.command)}>
            {c.text}
          </button>
        ))}
        <button type="button" className="ghost" aria-label="Link" onClick={link}>
          Link
        </button>
      </div>
      <div
        ref={area}
        className="richtext-area"
        role="textbox"
        aria-multiline="true"
        aria-label={label}
        contentEditable
        suppressContentEditableWarning
        onInput={(e) => onChange(e.currentTarget.innerHTML)}
      />
    </div>
  )
}
