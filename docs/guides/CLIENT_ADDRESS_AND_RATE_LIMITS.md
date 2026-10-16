# Client address, forwarded headers and rate limits (S-03)

Status 2026-10-16: investigated and hardened in code and tests (not deployed). The behaviour of the real Cloudflare edge and cloudflared is NOT verified; section 5 lists the one controlled check still needed.

## 1. How a request reaches the application
Browser -> Cloudflare edge (Access in front) -> cloudflared container -> `app` over the Compose network. Nothing else is published. The application sees cloudflared as the TCP peer; the real client address can only come from forwarding headers.

## 2. What the application trusts
- `UseForwardedHeaders` is the first middleware (before HSTS, logging, authentication and the rate limiter), so every later component, including the limiter, sees the rewritten address and scheme.
- Only `X-Forwarded-For` and `X-Forwarded-Proto` are honoured. `X-Forwarded-Host`, `Forwarded`, `CF-Connecting-IP` and `X-Real-IP` are ignored (host is fixed by `AllowedHosts`).
- They are honoured only when the TCP peer is in `Proxy:KnownProxies` or `Proxy:KnownNetworks`. With neither set the middleware does nothing (production start-up refuses to run with neither). Loopback is not trusted.
- `ForwardLimit` is 1 (explicit): only the rightmost `X-Forwarded-For` entry, the one the trusted proxy appended, is used. Entries a client puts in front of it are ignored.
- Compose trusts `PROXY_KNOWN_NETWORKS` (default `172.16.0.0/12`, the private Docker ranges).

## 3. Findings (evidence: `ClientAddressTrustTests`, `ProductionHostingTests`, `AbuseControlTests`; synthetic requests through the real pipeline, simulated peer address)
| # | Question | Result |
|---|---|---|
| 1 | Headers trusted | `X-Forwarded-For`, `X-Forwarded-Proto`, from trusted peers only. Confirmed. |
| 2 | Does XFF give the real client | In the application: yes, the rightmost entry. Whether Cloudflare appends the connecting address and cloudflared passes it unchanged is documented Cloudflare behaviour but UNVERIFIED here (section 5). |
| 3 | Trusted range too broad | `172.16.0.0/12` is about a million addresses; the only thing that can connect is cloudflared (nothing is published), so the practical exposure is a compromised sibling container on the Compose network. Low. Now configurable (`PROXY_KNOWN_NETWORKS`) so a host can pin the exact subnet; not narrowed by default because the cloudflared address is dynamic and changing the Production network is a deployment change. |
| 4 | Forged XFF bypassing limits | NOT reproducible. From an untrusted peer the header is ignored; from a trusted peer, forged left-hand entries and longer chains are ignored; missing, empty, malformed or out-of-range values fall back to the proxy address. Confirmed by tests. |
| 5 | Users sharing one bucket | Not while forwarding works: each forwarded address has its own bucket. If the peer were ever outside the trusted range the headers would be ignored and everyone would share cloudflared's address (60 sign-in requests a minute in total). Mitigation: the range covers the Docker pools in use (`helpdesk_default` is 172.25.0.0/16); verify in section 5. People behind one office NAT share a bucket by design (60 a minute is far above normal use). |
| 6 | Graph webhooks | Limited per sender address (600 a minute), keyed after forwarding like sign-in. Reaching the webhook through the tunnel needs a path-limited Access bypass for `/api/mail/graph`, which does not exist today (mail runs in polling mode) and is a Cloudflare change that needs its own approval; the notification itself is authenticated by the subscription `clientState`. |
| 7 | Defect found | **IPv6**: the bucket key was the full address, so one subscriber (who holds a whole /64) could mint a new bucket per request and evade the sign-in and webhook limits. IPv4-mapped IPv6 and plain IPv4 forms of one client also keyed differently. Both confirmed by failing tests, now fixed. |
| 8 | Future Unraid or dedicated Docker | Nothing Cloudflare-specific is used. Set `PROXY_KNOWN_NETWORKS` (or `Proxy__KnownProxies__0`) to the proxy's network or address; a different reverse proxy (nginx, Traefik) must append the client address to `X-Forwarded-For` (not replace it with `$remote_addr` of an earlier hop) and set `X-Forwarded-Proto`. |

## 4. Fix
`AbuseControls.ClientKey`: IPv4-mapped IPv6 becomes IPv4, other IPv6 addresses are reduced to their /64. Used for the webhook, sign-in and anonymous per-user buckets. `ForwardLimit = 1` is now explicit. The rate limiter stays on in Production with no off switch.

## 5. Controlled live-tunnel verification still required (needs approval; not run)
Cloudflare, cloudflared and Access were not started or changed. Before relying on per-client limits, with the tunnel up for the single allowed operator:
1. From the operator's browser make one request through the tunnel and capture, from a temporary debug log of the application or a throw-away echo container on the same Compose network, the TCP peer address and the `X-Forwarded-For`, `X-Forwarded-Proto`, `CF-Connecting-IP` headers.
2. Expect: the peer inside `PROXY_KNOWN_NETWORKS`; `X-Forwarded-For` ending with the operator's public address (compare with `CF-Connecting-IP`); `X-Forwarded-Proto: https`.
3. Send the same request with a forged `X-Forwarded-For: 198.51.100.9` from the operator's browser and confirm the rightmost entry is still the operator's address (Cloudflare appends, never replaces).
4. Stop the tunnel afterwards. Record the result in the master audit.
