# Supabase as a free, no-storage VPN backend

## Decision

Do not deploy RouteShield's VPN data plane to hosted Supabase. A temporary, authenticated
WebSocket relay is technically possible, but that is not a reliable always-on VPN service,
and a Supabase connection does not grant a general-purpose VPS or a TUN/UDP server.

The existing Supabase project was discovered by listing project metadata only. No application
tables, stored data, logs or secrets were read; no project, table, migration, Edge Function,
branch, traffic relay or billable resource was created or modified for this review.

## Why avoiding database writes is not enough

- Hosted Edge Functions have **256 MB memory**, **2 seconds CPU per request**, and a worker
  wall-clock lifetime of **150 seconds on Free / 400 seconds on paid plans**. A pending
  `waitUntil()` can prevent early retirement, but cannot extend that hard limit.
- Incoming and outgoing WebSockets are supported, but they inherit the same hard worker
  limits. Reconnecting a WebSocket does not automatically make arbitrary VPN sessions,
  UDP traffic, DNS behavior and mobile background operation work.
- Edge Function responses consume egress even with zero database/storage writes. The
  current Free allowance includes **5 GB uncached egress**, shared at organization scope;
  it is not unlimited free bandwidth. Do not assume this particular organization is on Free
  merely because a project is accessible.
- No custom payload logging and no database/storage writes can be implemented in application
  code; **zero platform metadata/log retention cannot be promised**. Supabase exposes
  infrastructure logs and warns that URL-query authentication tokens may enter logs.
- An open unauthenticated relay would invite abuse and unexpected quota consumption. It is
  deliberately not included.

## Recommended separation

Use a user-owned VPN endpoint/VPS (or a provider's existing sing-box/WireGuard endpoint) for
traffic. RouteShield can operate with local encrypted profiles and no Supabase dependency at
all. If a control plane is required later, consider stateless short-lived requests separately
from VPN traffic, with authentication, strict destination allowlists, rate/size limits and a
verified platform retention policy. Do not write packet bodies, browsing history, subscription
URLs, private keys or tunnel logs to Supabase. This is an architecture proposal, not a deployed
service, and no free VPS or no-log guarantee is made.

## Official references checked

- https://supabase.com/docs/guides/functions/limits
- https://supabase.com/docs/guides/functions/websockets
- https://supabase.com/docs/guides/troubleshooting/edge-functions-worker-timeouts-and-websocket-drops
- https://supabase.com/docs/guides/platform/manage-your-usage/egress
- https://supabase.com/docs/guides/platform/billing-on-supabase

Limits and plan terms can change; recheck before provisioning.
