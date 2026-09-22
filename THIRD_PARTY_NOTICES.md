# Third-party notices

## sing-box

- https://github.com/SagerNet/sing-box
- Version pinned by the build: 1.14.0
- Licence: GNU General Public License v3.0

The build downloads the official, unmodified Windows binary and verifies it against the digest
GitHub publishes for the release asset. RouteShield launches it as a separate process and is not
affiliated with SagerNet.

## Archivo

- https://github.com/Omnibus-Type/Archivo
- Licence: SIL Open Font License 1.1

The Regular, SemiBold and ExtraBold instances are redistributed unmodified in
`src/RouteShield/Assets/Fonts`.

## .NET

- https://github.com/dotnet/runtime
- Licence: MIT

Microsoft and third-party licences apply to the self-contained runtime components shipped inside
the published executable.

## WireSock (not redistributed)

- https://www.wiresock.net
- Proprietary; free for personal use under its own terms.

RouteShield does not ship WireSock or its driver. When it is already installed — by its own
installer or by TunnlTo — RouteShield runs that copy's `wiresock-client.exe` for WireGuard profiles.
