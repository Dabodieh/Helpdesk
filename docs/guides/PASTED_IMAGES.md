# Pasted images in replies and notes

Agents can paste (Ctrl+V) or drop PNG, JPEG or GIF images into the reply box. This feature is part of the attachment capability and adds no switch of its own.

## How it works

1. The browser uploads the image as raw bytes to `POST /api/tickets/{ticket}/inline-images` (needs `tickets.note`, CSRF, normal Department authorisation). The server checks the type from the file signature (never the client's claim), a 3 MB limit (`Attachments:MaxInlineImageBytes`) and the attachment pipeline (hash, scan, object store). SVG, HTML and executables are refused. The image stays pending: only its author can attach it, and only to the same ticket.
2. Posting the message names it as `[image:ID]` (at most 6). All images must be claimable or the post fails.
3. A public reply is emailed with `cid:` references and the images attached as Graph inline file attachments (3 MB each). Nothing is ever embedded as a data URI. If an image cannot be read at send time the email is not sent (`inline_image_unavailable`). Internal notes are never emailed and their images are not exposed to requesters.
4. The agent view and the requester portal show a marker as an image only when it names a downloadable attachment of that same message.

## Availability

`GET /api/ticket-capabilities` reports `attachmentsEnabled`. When it is false the composer refuses a paste with "Image uploads are unavailable." and the server refuses the upload (409) and any message naming an image.

To turn it on, as for every attachment:

- `Attachments:Enabled=true` and, in any environment that holds real data, `Attachments:RequireCleanScan=true` with an approved scanning engine behind `IAttachmentScanner` (open decision D-08). Production stays at `Enabled=false` until then.
- Live Graph evidence that inline attachments render correctly in the supported mail clients (simulator tests do not prove this).

## Known limits

- Pending uploads that are never sent are not cleaned up yet (needs a retention decision).
- Search snippets show the raw `[image:ID]` marker.
- WebP is not supported.
