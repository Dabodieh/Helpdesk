import type { ReactNode } from 'react'

const paths: Record<string, ReactNode> = {
  inbox: <path d="M2 9l2-5h8l2 5v4H2V9zm0 0h3l1 2h4l1-2h3" />,
  team: (
    <>
      <circle cx="6" cy="6" r="2" />
      <circle cx="11" cy="7" r="1.6" />
      <path d="M2.5 13c0-2 1.6-3.2 3.5-3.2S9.5 11 9.5 13M10 10.2c1.8-.2 3.5.8 3.5 2.8" />
    </>
  ),
  unassigned: (
    <>
      <circle cx="8" cy="5.5" r="2.5" strokeDasharray="2 1.6" />
      <path d="M3 14c0-2.6 2.2-4 5-4s5 1.4 5 4" strokeDasharray="2 1.6" />
    </>
  ),
  all: <path d="M2 4h12M2 8h12M2 12h12" />,
  search: (
    <>
      <circle cx="7" cy="7" r="4" />
      <path d="M10 10l4 4" />
    </>
  ),
  plus: <path d="M8 3v10M3 8h10" />,
  admin: (
    <>
      <circle cx="8" cy="8" r="2" />
      <path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" />
    </>
  ),
  lock: (
    <>
      <rect x="3.5" y="7" width="9" height="6.5" rx="1" />
      <path d="M5.5 7V5a2.5 2.5 0 015 0v2" />
    </>
  ),
  reply: <path d="M2 3.5h12v7H8l-3 2.5v-2.5H2v-7z" />,
  nomail: (
    <>
      <rect x="2" y="3.5" width="12" height="9" rx="1" />
      <path d="M2.5 4.5L8 9l5.5-4.5M2 14L14 2" />
    </>
  ),
  back: <path d="M10 3L5 8l5 5" />,
  edit: <path d="M3 13l1-3.5L11 2.5l2.5 2.5-7 7L3 13z" />,
  help: (
    <>
      <circle cx="8" cy="8" r="6" />
      <path d="M6.2 6.2a1.9 1.9 0 113 1.5c-.8.5-1.2.9-1.2 1.8M8 11.5v.1" />
    </>
  ),
}

export function Icon({ name, size = 14 }: { name: keyof typeof paths; size?: number }) {
  return (
    <svg
      className="icon"
      width={size}
      height={size}
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.4"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {paths[name]}
    </svg>
  )
}
