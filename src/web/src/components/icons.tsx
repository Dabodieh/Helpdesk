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
  admin: <><path d="M6.5 1.5h3l.4 1.8 1.3.8 1.8-.5 1.5 2.6-1.4 1.3v1.5l1.4 1.3-1.5 2.6-1.8-.5-1.3.8-.4 1.8h-3l-.4-1.8-1.3-.8-1.8.5L1.5 10l1.4-1.3V7.2L1.5 5.9 3 3.3l1.8.5 1.3-.8.4-1.5Z" /><circle cx="8" cy="8" r="2" /></>,
  bell: <><path d="M3 11h10l-1.5-2V6a3.5 3.5 0 0 0-7 0v3L3 11Z" /><path d="M6 13a2 2 0 0 0 4 0" /></>,  lock: (
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
  mail: (
    <>
      <rect x="2" y="3.5" width="12" height="9" rx="1" />
      <path d="M2.5 4.5L8 9l5.5-4.5" />
    </>
  ),
  clock: (
    <>
      <circle cx="8" cy="8" r="6" />
      <path d="M8 4.5V8l2.5 1.5" />
    </>
  ),
  attach: <path d="M11.5 7.5l-4 4a2.5 2.5 0 01-3.5-3.5l5-5a1.7 1.7 0 012.4 2.4l-5 5a.8.8 0 01-1.2-1.2l4-4" />,
  density: <path d="M2 4h12M2 8h12M2 12h12" />,
  star: <path d="M8 2l1.8 3.7 4 .6-2.9 2.8.7 4L8 11.2 4.4 13.1l.7-4L2.2 6.3l4-.6L8 2z" />,
  views: (
    <>
      <rect x="2" y="3" width="12" height="10" rx="1.5" />
      <path d="M2 6.5h12M6 6.5V13" />
    </>
  ),
  file: <path d="M4 2h5l3 3v9H4V2zM9 2v3h3" />,
  image: (
    <>
      <rect x="2" y="3" width="12" height="10" rx="1" />
      <circle cx="6" cy="7" r="1.2" />
      <path d="M2.5 12l3.5-3 3 2.5 2-1.5 2.5 2" />
    </>
  ),
  download: <path d="M8 2v8m0 0L5 7m3 3l3-3M3 13h10" />,
  alert: (
    <>
      <path d="M8 2l6.5 11.5h-13L8 2z" />
      <path d="M8 6.5v3M8 11.5v.1" />
    </>
  ),
  person: (
    <>
      <circle cx="8" cy="5.5" r="2.5" />
      <path d="M3 14c0-2.6 2.2-4 5-4s5 1.4 5 4" />
    </>
  ),
  chat: <path d="M2 3.5h12v7H8l-3 2.5v-2.5H2v-7z" />,
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
