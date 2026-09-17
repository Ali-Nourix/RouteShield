namespace RouteShield.Tunnels;

/// <summary>
/// The adapter the core dials out on, and the resolvers that adapter offers.
///
/// Both halves matter together. Pinning the adapter without the resolvers would leave the core
/// asking a corporate DNS server that is only reachable through the VPN we just stopped using,
/// so a bound run carries the chosen adapter's own resolvers instead of the system list.
/// </summary>
/// <param name="InterfaceName">The Windows connection name, as the core knows it: "Wi-Fi", "Ethernet".</param>
/// <param name="DnsAddresses">Resolvers reachable on that adapter; empty falls back to the system resolver.</param>
public sealed record NetworkBinding(string InterfaceName, IReadOnlyList<string> DnsAddresses)
{
    public static NetworkBinding? None => null;
}
