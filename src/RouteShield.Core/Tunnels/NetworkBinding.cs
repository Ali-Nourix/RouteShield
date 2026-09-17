namespace RouteShield.Tunnels;

/// <summary>
/// The adapter the core dials out on.
///
/// It deliberately carries no resolver. Naming the adapter's own DNS server looks tidy and is
/// wrong: on a censored network the ISP's resolver refuses the very names the tunnel exists to
/// reach — including the proxy provider's own address — while the system resolver Windows is
/// configured with answers them. The resolver stays the system's; only the route is pinned.
/// </summary>
/// <param name="InterfaceName">The Windows connection name, as the core knows it: "Wi-Fi", "Ethernet".</param>
public sealed record NetworkBinding(string InterfaceName);
