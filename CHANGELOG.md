# Changelog

## 1.7.1

### Fixed

- **The core stalled whenever its log could not be written fast enough.** sing-box logged three
  lines for every connection, up to a thousand a second while pages loaded, and RouteShield wrote
  each line to two files by opening, appending to and closing them, on the thread that drains the
  core's output. When that thread fell behind, which a virus scanner looking at every file close
  makes likely, the pipe filled, and a core that cannot write its log stops accepting connections
  until it can. Pages loaded in fits and starts. Against the real core, connections behind an
  unread log pipe began timing out after the first hundred, where the same core with its log
  drained answered 300 of 300. Now:
  - the core logs warnings and errors only, which still say why a dial failed;
  - the log files stay open, are written from memory once a second, and are rotated at 4 MB, with
    the previous file kept and included in diagnostics bundles;
  - the Diagnostics view takes new lines in one batch at background priority, so a burst of output
    never outranks input or drawing.
- **A dead node kept an automatic group down for up to three minutes.** The core re-tests its
  members every three minutes, and until then keeps dialling the node it chose even after that node
  has stopped answering. RouteShield now tests the chosen node on its own every 15 seconds. A failed
  test makes the core choose again at once. When no other member has a recent pass, every member is
  tested, at growing intervals while none answers. Connections still held open through the dead
  node are closed, so applications reconnect instead of hanging. The exception is connections that
  are still receiving data: then the node is alive, and a saturated link timed the test out.
- **Automatic groups changed their exit address on noise.** A group moved to another member as soon
  as that member measured 50 ms faster, and round trips through a filtered network drift by more
  than that between tests. Sites that tie a login to an address answered with captchas and
  sign-outs. The margin is now 150 ms.

## 1.7.0

### Fixed

- **An automatic group could carry every connection over a provider's quota line.** Panels put
  the account's remaining traffic and expiry into the subscription as extra entries — "7.75 GB
  left" on a link to 1.1.1.1:53966 — and RouteShield imported them as nodes. The core carries a
  group's traffic over its first member until its own test has passed, and over it for good when
  no test passes; the first entry is exactly where panels put these lines. The dashboard read
  "via 7.75 G…" and every connection was reset. Such entries are now recognised by their address —
  a public resolver, loopback, an unspecified, link-local or multicast address, port 0 or 1 — and
  never by their name, because some panels write the remaining traffic into every real node's name
  as well. They are kept as notes on the subscription. Entries saved by an earlier version are moved
  there when the app starts, and a group never takes one as a member.
- **Subscriptions dropped Hysteria2, TUIC and AnyTLS links.** The profile parser read them but the
  subscription reader did not recognise the schemes, so those nodes vanished on refresh without a
  word, and a Base64 body holding only such links was rejected as holding no configuration.
- **An adapter blocked by another VPN's filter counted as usable.** A full-tunnel client can refuse
  sockets on the other adapters with "access denied" instead of taking their route away. That now
  counts the same as having no route, and the tunnel says so instead of dialling into the filter.

### Added

- **Every node of an automatic group is tested as soon as the tunnel is up.** Each answer is stored
  with its delay and makes the core choose again, so the group is on the fastest node that answers
  within a second or two, instead of when the core's own round ends, which waits for its slowest
  member. The dashboard shows how many nodes answer and which is fastest. When none does, it says
  so, and names what is most likely in the way: another VPN holding every route, which only
  WireGuard on WireSock can get underneath; a subscription the provider reports as used up or
  expired; or a network that blocks the servers. Each node's result is in the log. A probe that
  fails later repeats the test, at most every two minutes.
- **Groups are ordered by the last latency test.** Nodes that answered come first, fastest on top,
  then the untested ones, then the ones that failed. The first member is the one that carries
  traffic before the core's first test is in, so it should be a good one.
- **Subscription usage.** The `subscription-userinfo` header that panels send is read. The library
  shows the traffic left and the time to expiry beside each subscription, with the provider's notes
  in the tooltip. A refresh that returns notes and no servers, which is what a panel sends for a
  spent account and sometimes by mistake, keeps the nodes already there and says why.

## 1.6.0

### Added

- **A second engine: WireSock, for WireGuard profiles — the one TunnlTo uses.** sing-box captures
  traffic with a virtual adapter and routes, so it competes for the routing table with any other
  VPN, and a corporate client such as Cisco AnyConnect in full-tunnel mode always wins: every dial
  either goes through the corporate network, where the firewall refuses it, or finds no route on
  the physical adapter at all. WireSock takes the chosen applications' packets at the network-driver
  level and sends them on the network card directly, underneath the routing table, so the other
  VPN's routes are never consulted. That is why TunnlTo worked on the same machine while
  RouteShield did not.

  *Security & behaviour → Engine* offers **Automatic** (the default), **sing-box** and **WireSock**.
  Automatic runs a WireGuard profile on WireSock when another VPN holds the default route, or when
  the profile is AmneziaWG, and everything else on sing-box. The routing policy is written into the
  profile as WireSock's own directives — `#@ws:AllowedApps` for the selected applications,
  `#@ws:DisallowedApps` for the excluded ones, `#@ws:DisallowedIPs` for the local network — and
  RouteShield itself rides along so the exit-address probe measures the tunnel. The dashboard says
  which engine is carrying the tunnel and why. WireSock is not shipped with RouteShield: it is found
  where its installer, or TunnlTo's, put it. It carries WireGuard only; the browser bridge, secure
  DNS and the firewall kill switch are sing-box features and are off while WireSock runs.
- **AmneziaWG profiles are recognised.** A `.conf` with junk-packet and header obfuscation (`Jc`,
  `Jmin`, `Jmax`, `S1`, `S2`, `H1`–`H4`) is labelled AmneziaWG. sing-box cannot speak it and would
  connect as plain WireGuard, which a DPI filter may block; the Automatic engine runs it on WireSock,
  which passes the obfuscation through untouched.

### Fixed

- **WireGuard stalled inside another VPN.** A peer sized for a plain 1500-byte link produced
  datagrams larger than a corporate VPN adapter carries — Cisco's runs at 1300 to 1400 — and the
  socket refused them: handshakes and small packets passed, pages and video stalled. The tunnel now
  reads the MTU of the adapter actually carrying it and sizes WireGuard, and the tunnel interface in
  front of it, to fit. Stream-based proxies are unaffected; they re-segment everything themselves.

## 1.5.1

### Fixed

- **1.5.0 broke name resolution.** Pinning the outbound adapter also pointed the resolver at
  that adapter's own DNS server, on the reasoning that the system list might hold a server only
  reachable through the VPN we had stopped using. On a censored network that reasoning is
  backwards: the ISP's own resolver refuses the very names the tunnel exists to reach — 1014
  `REFUSED` answers in one session, including the WireGuard profile's own peer address, so the
  tunnel could not come up at all — where the resolver Windows is configured with answers them.
  Names are answered by the system resolver again, bound adapter or not. Only the route is
  pinned now.

### Added

- **The adapter is verified before the tunnel uses it.** A corporate VPN in full-tunnel mode
  takes the default route and leaves no route on the physical adapter, so a socket bound to it
  fails instantly with "a socket operation was attempted to an unreachable network" — every
  dial, the proxy server included. RouteShield now checks whether the operating system can route
  on the adapter it picked, moves to the next candidate if it cannot, and when none can carry
  traffic says so on the dashboard: which adapter does hold the default route, and that a VPN in
  full-tunnel mode is what does this. No setting can undo that from inside RouteShield; the
  message now says so instead of leaving a tunnel that looks connected and reaches nothing.
- **Probe failures name their cause.** "The SSL connection could not be established" is the
  outermost message of a chain and says nothing on its own; the whole chain is now reported.

## 1.5.0

### Fixed

- **Another VPN took the tunnel with it.** A corporate client such as Cisco AnyConnect installs
  its own default route while it is connected, and the core — told to detect the default
  interface — followed it. Every dial to a proxy server then left from inside the corporate
  network, where its firewall refused or dropped them, so RouteShield reported itself connected
  while nothing could reach the outside: `dial tcp …: i/o timeout` and `An existing connection
  was forcibly closed by the remote host`, from a 10.x address belonging to the other VPN.

  RouteShield now names the adapter it leaves on instead of asking which one holds the default
  route. *Security & behaviour → Outbound adapter* offers **Avoid other VPNs** (the default:
  a physical adapter, skipping anything belonging to a VPN or a virtual switch), **Follow
  Windows** (the old behaviour), or one adapter you name. The bound adapter's own resolvers are
  used too, since the system list holds resolvers that only answer through the VPN we just
  stopped dialling through. When the network changes — the other VPN connecting or dropping —
  the tunnel moves itself to the adapter that is up now, with the leak guard still armed.
- **WireGuard profiles failed every IPv6 connection** with `missing IPv6 local address` when the
  peer had only an IPv4 address of its own. A node that cannot carry IPv6 is no longer handed
  IPv6 destinations: the tunnel is built without them and AAAA queries are answered empty.
- **WireGuard profiles dropped large packets** with `wsasendmsg: A message sent on a datagram
  socket was larger than the internal message buffer`. A WireGuard peer wraps each packet in one
  datagram, so the tunnel interface no longer offers more than the peer's own MTU.

## 1.4.0

### Fixed

- **Text was hard to read in dark mode.** Light glyphs on a dark ground are rasterised thinner
  than dark glyphs on paper: subpixel antialiasing spreads each stem over three coloured thirds
  and covers none of them fully, so a regular weight read as grey with coloured fringes however
  white its colour was. Dark mode now draws text with hinted metrics and greyscale antialiasing,
  which puts whole pixels into the stems, and the palette's text and secondary steps are brighter.
  Glyph rendering belongs to the palette now rather than to the text style, so it changes with the
  theme instead of staying pinned to the light one.
- **List rows could ignore the palette.** The framework's own `ListBox` style sets a foreground,
  and a theme setter outranks inheritance, so rows kept a system colour rather than the palette's.

### Added

- **QUIC is refused for routed applications** (on by default). QUIC is UDP, and UDP through a
  proxy has no shared congestion control with the tunnel underneath, so a browser loads a page
  over it and then stalls on the video. Refusing it — with an ICMP unreachable, so the browser
  gives up at once instead of waiting out a timeout — makes it fall back to HTTP/2 over TCP.
  This is what Clash and Hiddify configurations do, and it is the single biggest difference to
  how a proxied browser feels. Only the applications the policy routes are affected; excluded
  applications keep QUIC on their own connection.
- **Iranian sites stay on the local connection** (on by default). Everything under `.ir`, and
  the large services on other domains — Digikala, Aparat, Zarinpal, Divar, the payment gateways,
  ArvanCloud — is routed directly instead of travelling abroad and back. Matching is by name,
  which is what a connection carries once secure DNS is answering, so no address list is needed.
- **Subscriptions are fetched through the tunnel** when one is up. The address a subscription
  lives at is usually blocked by the same network the tunnel exists to get around, which is why
  a refresh failed with a connection timeout. The request now carries a user agent naming
  sing-box, so providers serve the document this app can read, and accepts compressed responses.

### Changed

- VMess carries UDP as `xudp`, as VLESS already did.

## 1.3.0

### Fixed

- **A selected sidebar item or segment turned black but its label stayed black.** The app-wide
  text style forced the ink colour on every label, including the ones generated inside a
  selected control. Text now inherits its colour from the control that holds it, so a label
  turns light the moment the control fills with ink — in the navigation, the Name/Latency
  switch, tool tips and every button alike.
- **Through the Chrome extension a site loaded but its video did not.** Chrome routes by site,
  and a site is more than its own domain: YouTube's page comes from youtube.com, the video from
  googlevideo.com, which went out unprotected and was blocked. Each assigned site now carries the
  domains it is known to load from, and the extension learns the rest by watching the tab, so the
  player, images and scripts follow the page. Firefox matches a site's service-worker requests,
  which carry no tab, to the assigned tab by origin.
- **The popup's status chip ran off the right edge** with a long profile name; it now shrinks and
  ends in an ellipsis.
- **The Firefox add-on package could not be installed.** Windows PowerShell's `Compress-Archive`
  writes entry names with backslashes, which Firefox rejects. The build now writes the archive
  itself, with forward slashes and `manifest.json` at the root as Mozilla's packaging guide
  describes, and names it `.xpi`. CI runs Mozilla's `web-ext lint` over the staged add-on.

### Added

- **Dark mode.** *Security & behaviour → Appearance* offers System, Light and Dark. System
  follows the Windows app colour and changes with it; the palette swaps in place without a
  restart. The extension popup follows the browser's colour scheme.
- **Automatic selection.** Every subscription with two or more nodes gets a *Fastest of …*
  entry. Connecting to it hands the core all of the subscription's nodes as a `urltest` group:
  the core measures them, carries traffic over the fastest, re-tests every three minutes and
  moves to the next node when the chosen one stops answering, without dropping the tunnel. The
  dashboard names the node in use.
- **Hysteria2, TUIC and AnyTLS share links** (`hysteria2://`, `hy2://`, `tuic://`,
  `anytls://`), including Hysteria2 obfuscation, bandwidth hints and port hopping. Hysteria2 and
  TUIC run over QUIC and keep their speed on lossy, throttled links.
- **Fragment TLS handshakes** (*Security & behaviour*, off by default): the handshake with the
  proxy server is split across several packets and TLS records, so a firewall that reads the
  server name from the first packet never sees it whole. QUIC-based protocols are left alone.
- **Browser fingerprint by default.** Links that name no `fp=` now imitate Chrome's TLS client
  hello whenever the transport allows it (TCP, WebSocket, HTTPUpgrade); gRPC and HTTP/2
  transports are left as declared, and `fp=none` opts out.
- **Bridge ports stay put across reconnects** when they are still free, so a browser that
  resolved a route a moment before a reconnect keeps working. Both extensions also re-read the
  state as soon as a proxy connection fails.
- **The extensions fail closed.** A tab or site assigned to a profile is never allowed to fall
  back to the open connection: while the tunnel is down its requests are held (sent to a port
  nothing listens on) and the badge reads HELD. "No VPN" still leaves directly.
- The Firefox manifest declares `data_collection_permissions: none`, which Firefox 140+ shows
  at install time and AMO requires of new submissions.

### Changed

- The profile editor is collapsed by default; the library takes the full width. *Open editor*
  in the page header, *New profile* and *Import file* open it.
- Rules between sections are drawn at 12% ink instead of 40%.
- The Firefox add-on ships as `RouteShield-Extension-firefox-<version>.xpi`; the installable
  files also sit in the app's `extensions` folder, where *Open extension folder* leads.

## 1.2.0

### Fixed

- **Browsers in *Selected applications* mode could not load anything, while *Full system
  tunnel* worked.** On Windows every DNS query is sent by the DNS Client service, never by the
  application, so no per-process rule can carry a routed application's DNS through the tunnel;
  a blocked or poisoned local resolver then broke the application. Secure DNS now answers every
  name from a private range (FakeIP) and the name itself travels to the proxy. Direct traffic is
  resolved at connect time on the physical adapter, exactly as it would be without RouteShield.
  Verified end to end against the sing-box 1.14 core.
- **The DISCONNECT button showed no label.** The implicit text style pinned ink on ink inside
  the dark card; the label now follows the button's own colour, including on hover.
- **The exit-IP and latency probe gave up after one attempt.** It now retries three times over
  the first seconds, then refreshes the latency figure every minute while connected.

### Added

- **Latency test.** A throwaway core carries every profile at once and the Clash API times a
  request through each; results appear per row and the library can sort by them.
- **Browser bridge and extensions.** While the tunnel is up, the active profile, every pinned
  profile and a "No VPN" bypass are exposed as loopback proxies, discovered through a read-only
  loopback API. The Firefox add-on assigns a route per tab; the Chrome add-on per site, because
  Chrome has no per-tab proxy. Both are packaged with every release.
- **Local names stay local.** With local network access on, `.local`, `.lan`, `.home`,
  `.internal`, `.home.arpa` and `.localdomain` go direct alongside private addresses.
- VLESS carries UDP as `xudp`, the encoding Xray servers expect, so QUIC, DNS and games inside
  the tunnel no longer fall back to one connection per destination.

### Changed

- The Profiles page groups the library by subscription, with each subscription's status,
  refresh and edit actions on its own heading; subscriptions are added and edited in a dialog.
- Pills and dots gave way to rounded rectangles throughout: state tags, badges, status marks.
- Without IPv6 in the tunnel, AAAA queries get an empty successful answer instead of a real
  address that would have travelled around the tunnel.


## 1.1.0

### Fixed

- **The tunnel would not start on sing-box 1.12 and later.** The generated configuration named
  no resolver for dialing, so the core refused it with `missing route.default_domain_resolver or
  domain_resolver in dial fields`, fatal as of 1.14. Every configuration now sets
  `route.default_domain_resolver` to a resolver that answers outside the tunnel, and the proxy
  node carries the same resolver explicitly.
- **Reality profiles could not connect.** sing-box rejects a Reality client without a uTLS
  profile; a link that asked for Reality without `fp=` now gets the Chrome fingerprint instead
  of a failed start.
- **The bootstrap resolver could be a dangling reference.** With secure DNS switched off, the
  proxy pointed at a DNS tag that was never declared.
- **Executables were matched by exact path,** so the same program dropped out of the routing
  policy when Windows reported its path with different casing. Matching is now case-insensitive.
- **The event log stayed empty.** The core was told to log to a file, which silences its
  standard output — the only thing the app was reading. It now logs to standard output, and the
  app mirrors it to disk itself.

### Added

- Throughput read from the core's Clash API, and latency and exit address measured through a
  loopback inbound the core owns.
- DNS rules that follow the routing policy: routed applications resolve inside the tunnel,
  everything else on the system resolver.
- `RouteShield.Core`, a cross-platform library holding link parsing and configuration building,
  with a test suite that runs every generated configuration through a real sing-box binary.
- GitHub Actions: CI on every push, and a tag-triggered workflow that publishes the packaged
  build as the release for that tag.

### Changed

- The interface was rebuilt on the Modernist design system — see `docs/design-system.md`.
- Loopback ports for the probe and the control API are reserved at start instead of fixed, and
  the control API gets a fresh token on every run.
- Log redaction keeps URL hosts (so a failure stays diagnosable) while dropping the path and
  query, which is where subscription tokens live.

## 1.0.0

- First release.
