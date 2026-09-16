/*
 * Talks to the RouteShield app on this machine. The app answers one read-only request on
 * loopback: which proxies are up, and which profile each one carries. Ports change on
 * every connect, so an assignment always refers to a route by what it is — a profile id,
 * "bypass", or "default" — and is resolved to a port at the moment a request is made.
 *
 * The site helpers live here too, because both browsers need the same idea of "a site":
 * the registrable domain (youtube.com, bbc.co.uk), so that one choice covers every host
 * a site is served from.
 */
const RouteShieldBridge = (() => {
  const DEFAULT_PORT = 47831;
  const REQUEST_TIMEOUT_MS = 1500;

  /**
   * A port nothing listens on. A site assigned to a profile is sent here while the tunnel is
   * down, so its requests fail instead of quietly leaving on the user's own connection.
   */
  const DEAD_PORT = 1;

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

  /**
   * Whether requests under this assignment must not leave unprotected when no route is up.
   * A profile choice is a promise that the traffic goes through a VPN; "No VPN" is not.
   */
  function failsClosed(assignment) {
    return assignment?.kind === "profile";
  }

  /** Short text for the toolbar badge; empty when the tab follows RouteShield's own routing. */
  function badgeFor(assignment, route) {
    if (!assignment || assignment.kind === "default") {
      return { text: "", color: "#201E1D" };
    }

    if (!route) {
      return failsClosed(assignment)
        ? { text: "HELD", color: "#9B9797" }
        : { text: "···", color: "#9B9797" };
    }

    return assignment.kind === "bypass"
      ? { text: "OFF", color: "#605D5D" }
      : { text: "VPN", color: "#EC3013" };
  }

  // ── Sites ──

  /** Public suffixes with two labels, where the registrable domain has three. */
  const TWO_LEVEL_SUFFIXES = new Set([
    "co.uk", "org.uk", "me.uk", "ac.uk", "gov.uk", "net.uk", "sch.uk",
    "com.au", "net.au", "org.au", "edu.au", "gov.au",
    "co.nz", "net.nz", "org.nz",
    "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
    "co.kr", "or.kr", "ne.kr",
    "com.br", "net.br", "org.br", "gov.br",
    "com.cn", "net.cn", "org.cn", "gov.cn",
    "com.tw", "org.tw", "net.tw",
    "com.hk", "org.hk", "net.hk",
    "com.sg", "com.my", "com.tr", "com.mx", "com.ar", "com.co", "com.pe", "com.ve",
    "co.in", "net.in", "org.in", "co.za", "co.il", "org.il", "co.id", "co.th", "com.ua",
    "com.eg", "com.sa", "com.pk", "com.ph", "com.vn", "com.ng", "com.bd",
    "co.ir", "ac.ir", "org.ir", "net.ir", "gov.ir",
    "github.io", "gitlab.io", "pages.dev", "vercel.app", "netlify.app", "herokuapp.com",
    "web.app", "firebaseapp.com", "azurewebsites.net", "cloudfront.net", "amazonaws.com",
    "blogspot.com", "wordpress.com", "tumblr.com"
  ]);

  /** "rr3---sn-4g5edn7z.googlevideo.com" → "googlevideo.com"; "news.bbc.co.uk" → "bbc.co.uk"; addresses → null. */
  function registrableDomain(hostname) {
    if (!hostname) {
      return null;
    }

    const host = hostname.toLowerCase().replace(/\.$/, "");
    if (host === "localhost" || /^[\d.]+$/.test(host) || host.includes(":")) {
      return null;
    }

    const labels = host.split(".");
    if (labels.length < 2) {
      return null;
    }

    const lastTwo = labels.slice(-2).join(".");
    if (labels.length >= 3 && TWO_LEVEL_SUFFIXES.has(lastTwo)) {
      return labels.slice(-3).join(".");
    }

    return lastTwo;
  }

  /** The site a URL belongs to, or null for pages that cannot be assigned (chrome://, files, addresses). */
  function siteOf(url) {
    try {
      const parsed = new URL(url);
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:" && parsed.protocol !== "ws:" && parsed.protocol !== "wss:") {
        return null;
      }

      return registrableDomain(parsed.hostname);
    } catch {
      return null;
    }
  }

  /**
   * Domains a site is known to load its content from. A page rarely lives on one domain:
   * YouTube's video comes from googlevideo.com, Instagram's pictures from cdninstagram.com.
   * These are the starting set; the Chrome background learns the rest by watching the tab.
   */
  const SITE_FAMILIES = {
    "youtube.com": ["googlevideo.com", "ytimg.com", "ggpht.com", "gstatic.com", "googleapis.com", "google.com", "youtube-nocookie.com", "googleusercontent.com", "youtu.be", "ytimg.l.google.com"],
    "youtu.be": ["youtube.com", "googlevideo.com", "ytimg.com", "ggpht.com", "gstatic.com", "googleapis.com", "google.com"],
    "google.com": ["gstatic.com", "googleapis.com", "googleusercontent.com", "ggpht.com", "gvt1.com", "gvt2.com", "youtube.com", "googlevideo.com", "ytimg.com"],
    "gmail.com": ["google.com", "gstatic.com", "googleapis.com", "googleusercontent.com"],
    "twitter.com": ["twimg.com", "x.com", "t.co", "abs.twimg.com", "video.twimg.com"],
    "x.com": ["twimg.com", "twitter.com", "t.co"],
    "instagram.com": ["cdninstagram.com", "fbcdn.net", "facebook.com", "fbsbx.com"],
    "facebook.com": ["fbcdn.net", "fbsbx.com", "facebook.net", "messenger.com"],
    "messenger.com": ["facebook.com", "fbcdn.net", "fbsbx.com"],
    "whatsapp.com": ["whatsapp.net", "fbcdn.net", "facebook.com"],
    "threads.net": ["cdninstagram.com", "fbcdn.net", "instagram.com"],
    "twitch.tv": ["ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net", "live-video.net"],
    "reddit.com": ["redd.it", "redditmedia.com", "redditstatic.com", "reddit.map.fastly.net"],
    "spotify.com": ["scdn.co", "spotifycdn.com", "spotifycdn.net", "spotify.map.fastly.net"],
    "netflix.com": ["nflxvideo.net", "nflximg.net", "nflxext.com", "nflxso.net"],
    "discord.com": ["discordapp.com", "discordapp.net", "discord.gg", "discord.media"],
    "telegram.org": ["t.me", "telegram.me", "web.telegram.org", "telegram.dog"],
    "web.telegram.org": ["telegram.org", "t.me"],
    "t.me": ["telegram.org"],
    "soundcloud.com": ["sndcdn.com"],
    "vimeo.com": ["vimeocdn.com", "akamaized.net"],
    "tiktok.com": ["tiktokcdn.com", "tiktokv.com", "tiktokcdn-us.com", "byteoversea.com", "ibytedtos.com", "musical.ly"],
    "wikipedia.org": ["wikimedia.org", "wikidata.org"],
    "github.com": ["githubusercontent.com", "githubassets.com", "github.io", "githubapp.com"],
    "openai.com": ["oaistatic.com", "oaiusercontent.com", "chatgpt.com", "auth0.com"],
    "chatgpt.com": ["openai.com", "oaistatic.com", "oaiusercontent.com"],
    "claude.ai": ["anthropic.com"],
    "medium.com": ["miro.medium.com", "cdn-client.medium.com"],
    "linkedin.com": ["licdn.com"],
    "pinterest.com": ["pinimg.com"],
    "steampowered.com": ["steamstatic.com", "steamcommunity.com", "steamcontent.com"],
    "steamcommunity.com": ["steamstatic.com", "steampowered.com"],
    "bbc.co.uk": ["bbci.co.uk", "bbc.com"],
    "bbc.com": ["bbci.co.uk", "bbc.co.uk"]
  };

  function familyOf(site) {
    return SITE_FAMILIES[site] ?? [];
  }

  return { DEFAULT_PORT, DEAD_PORT, fetchState, resolveRoute, failsClosed, badgeFor, registrableDomain, siteOf, familyOf };
})();
