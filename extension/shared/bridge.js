/*
 * Talks to the RouteShield app on this machine. The app answers one read-only request on
 * loopback: which proxies are up, and which profile each one carries. Ports change on
 * every connect, so an assignment always refers to a route by what it is — a profile id,
 * "bypass", or "default" — and is resolved to a port at the moment a request is made.
 */
const RouteShieldBridge = (() => {
  const DEFAULT_PORT = 47831;
  const REQUEST_TIMEOUT_MS = 1500;

  const unreachable = () => ({ reachable: false, connected: false, active: null, routes: [], version: null });

  async function fetchState(port = DEFAULT_PORT) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);

    try {
      const response = await fetch(`http://127.0.0.1:${port}/v1/state`, {
        signal: controller.signal,
        cache: "no-store"
      });

      if (!response.ok) {
        return unreachable();
      }

      const data = await response.json();
      return {
        reachable: true,
        connected: Boolean(data.connected),
        active: data.active ?? null,
        version: data.version ?? null,
        routes: Array.isArray(data.routes) ? data.routes : []
      };
    } catch {
      return unreachable();
    } finally {
      clearTimeout(timer);
    }
  }

  /** The route an assignment points at right now, or null when it cannot be honoured. */
  function resolveRoute(state, assignment) {
    if (!state || !state.connected || !assignment || assignment.kind === "default") {
      return null;
    }

    if (assignment.kind === "bypass") {
      return state.routes.find((route) => route.kind === "bypass") ?? null;
    }

    return state.routes.find((route) => route.id && route.id === assignment.id) ?? null;
  }

  /** Short text for the toolbar badge; empty when the tab follows RouteShield's own routing. */
  function badgeFor(assignment, route) {
    if (!assignment || assignment.kind === "default") {
      return { text: "", color: "#201E1D" };
    }

    if (!route) {
      return { text: "···", color: "#9B9797" };
    }

    return assignment.kind === "bypass"
      ? { text: "OFF", color: "#605D5D" }
      : { text: "VPN", color: "#EC3013" };
  }

  return { DEFAULT_PORT, fetchState, resolveRoute, badgeFor };
})();
