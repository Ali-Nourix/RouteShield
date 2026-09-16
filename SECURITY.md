# Security model

## What is protected

- **Secrets at rest.** Profile bodies and subscription URLs are sealed with the current user's
  DPAPI key before `settings.json` is written, so a copied settings file is useless on another
  account or machine.
- **Configuration validation.** Every configuration is checked by the core itself before the
  core is started with it.
- **Leak containment.** The TUN adapter uses strict routing. While the tunnel is up and the
  policy is *Selected applications*, the routed executables are blocked by Windows Firewall from
  reaching the internet over any physical adapter.
- **DNS.** With secure DNS on, every name is answered from a private range (FakeIP) and the
  name itself travels to the proxy, so a blocked or lying local resolver cannot redirect a
  routed application. Only the resolver used for dialing the proxy server itself stays on the
  physical adapter, because nothing can resolve through a tunnel that is not up yet.
- **Handshakes.** Optionally, every TLS handshake with the proxy server is fragmented across
  packets and TLS records so the server name is never in one packet. It hides nothing from an
  observer who reassembles the stream; it is a robustness measure against simple filters.
- **Logs.** Everything written to the log is redacted first: keys, passwords, UUIDs, tokens,
  share links, and the path and query of any URL. The diagnostics bundle contains only redacted
  logs and a short environment summary.
- **Browser bridge.** The API the extensions read is bound to 127.0.0.1, answers one GET,
  sets no CORS headers and refuses any request whose Host header is not its own loopback
  address, which closes the DNS-rebinding route to a local API. It returns profile names and
  port numbers, never credentials. The proxies themselves are loopback SOCKS/HTTP inbounds of
  the running core. A tab or site the extension assigned to a profile fails closed: while the
  tunnel is down its requests go to a port nothing listens on and fail, instead of leaving on
  the open connection. The extensions ask for permission to observe requests (`webRequest`)
  only to learn which domains an assigned site loads from and to notice a dead proxy; request
  bodies are never read and nothing leaves the browser.
- **Supply chain.** The build downloads the pinned sing-box release from the official repository
  over HTTPS and verifies it against the digest GitHub publishes for that asset.

## What is not protected

- There is no signed WFP driver, so the kill switch is a firewall rule set rather than a kernel
  filter. It is removed when the tunnel stops and when RouteShield exits.
- The kill switch does not survive a reboot, and does not cover applications outside the
  selected list.
- The runtime configuration is written in plaintext to `%LocalAppData%\RouteShield\runtime.json`
  while the core reads it, and deleted when the core stops.
- The Clash API listens on loopback with a token that changes every run, but any process running
  as your user can read that token out of the runtime configuration.
- Any local process can use the bridge proxies while the tunnel is up, the same way any local
  process could already reach the exit-IP probe inbound. They carry no more than the tunnel
  itself does.
- The build is not code-signed, and the project has had no independent audit.

## Emergency cleanup

If RouteShield is killed before it can tidy up, an elevated PowerShell session can undo both
of its system changes:

```powershell
Get-NetFirewallRule -Group 'RouteShield Kill Switch' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-Process sing-box -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Reporting

Open an issue describing what happened and what you expected. Never attach a live profile,
subscription URL, or key to an issue — the exported diagnostics bundle is already redacted and
is the right thing to send.
