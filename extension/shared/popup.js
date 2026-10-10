/*
 * One popup for both browsers. The background script owns the state and the assignments;
 * the popup only asks for the current tab's context, renders it, and reports a choice back.
 */
const api = globalThis.browser ?? globalThis.chrome;

const elements = {
  status: document.getElementById("status"),
  statusText: document.getElementById("status-text"),
  subjectKicker: document.getElementById("subject-kicker"),
  subjectName: document.getElementById("subject-name"),
  choices: document.getElementById("choices"),
  note: document.getElementById("note"),
  footLeft: document.getElementById("foot-left"),
  refresh: document.getElementById("refresh")
};

let currentTab = null;

async function send(message) {
  return api.runtime.sendMessage({ ...message, tabId: currentTab?.id, url: currentTab?.url });
}

function sameAssignment(a, b) {
  return (a?.kind ?? "default") === (b?.kind ?? "default") && (a?.id ?? null) === (b?.id ?? null);
}

function renderStatus(state) {
  elements.status.className = "chip";

  if (!state.reachable) {
    elements.status.classList.add("chip-off");
    elements.statusText.textContent = "RouteShield is not running";
    return;
  }

  if (!state.connected) {
    elements.status.classList.add("chip-idle");
    elements.statusText.textContent = "Disconnected";
    return;
  }

  elements.status.classList.add("chip-live");
  elements.statusText.textContent = state.active ? `Connected · ${state.active}` : "Connected";
  elements.status.title = elements.statusText.textContent;
}

function choice({ label, hint, tag, assignment, checked, disabled }) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "choice";
  button.setAttribute("role", "radio");
  button.setAttribute("aria-checked", String(checked));
  button.disabled = Boolean(disabled);

  const box = document.createElement("span");
  box.className = "box";

  const text = document.createElement("span");
  text.className = "text";
  const title = document.createElement("div");
  title.className = "title";
  title.textContent = label;
  text.append(title);
  if (hint) {
    const hintLine = document.createElement("div");
    hintLine.className = "hint";
    hintLine.textContent = hint;
    text.append(hintLine);
  }

  button.append(box, text);

  if (tag) {
    const tagElement = document.createElement("span");
    tagElement.className = "tag";
    tagElement.textContent = tag;
    button.append(tagElement);
  }

  button.addEventListener("click", async () => render(await send({ type: "assign", assignment })));
  return button;
}

function render(context) {
  const { state, assignment, mode, subject, assignable = true, related = 0 } = context;

  renderStatus(state);
  elements.subjectKicker.textContent = mode === "site" ? "THIS SITE" : "THIS TAB";
  elements.subjectName.textContent = subject;
  elements.choices.innerHTML = "";

  const disabled = !assignable;

  elements.choices.append(
    choice({
      label: "Default",
      hint: "Follow RouteShield's own routing for this " + (mode === "site" ? "site" : "tab"),
      assignment: { kind: "default" },
      checked: sameAssignment(assignment, { kind: "default" }),
      disabled
    }),
    choice({
      label: "No VPN",
      hint: "Leave the tunnel; go out on your own connection",
      assignment: { kind: "bypass" },
      checked: assignment?.kind === "bypass",
      disabled
    })
  );

  const profiles = state.routes.filter((route) => route.kind === "active" || route.kind === "profile");
  for (const route of profiles) {
    elements.choices.append(
      choice({
        label: route.name,
        hint: route.kind === "active" ? "The profile RouteShield is connected to" : "Pinned profile",
        tag: route.kind === "active" ? "ACTIVE" : null,
        assignment: { kind: "profile", id: route.id },
        checked: assignment?.kind === "profile" && assignment.id === route.id,
        disabled
      })
    );
  }

  const held = assignment?.kind === "profile" && !RouteShieldBridge.resolveRoute(state, assignment);
  const scope = mode === "site" ? "site" : "tab";

  elements.note.className = "note";
  if (held) {
    elements.note.classList.add("warn");
    elements.note.textContent = state.reachable
      ? `The tunnel is down. Requests from this ${scope} are held, not sent unprotected, until it is back.`
      : `RouteShield is not running. Requests from this ${scope} are held, not sent unprotected, until it is.`;
  } else if (!state.reachable) {
    elements.note.classList.add("warn");
    elements.note.textContent = "Start RouteShield to make choices here. Existing choices are kept.";
  } else if (!state.connected) {
    elements.note.textContent = "Choices apply as soon as the tunnel is up.";
  } else if (mode === "site" && related > 0) {
    elements.note.textContent = `Covers every tab on this site and ${related} domain${related === 1 ? "" : "s"} it loads from — video, images and scripts included.`;
  } else if (profiles.length <= 1) {
    elements.note.textContent = "Pin more profiles in RouteShield → Profiles to offer them here.";
  } else if (mode === "site") {
    elements.note.textContent = "Chrome routes by site, not by tab: the choice covers every tab on this site and the domains it loads from.";
  } else {
    elements.note.textContent = "";
  }

  if (!assignable) {
    elements.note.textContent = "This page cannot be assigned; open a website first.";
  }

  elements.footLeft.textContent = state.reachable
    ? `RouteShield ${state.version ?? ""} · 127.0.0.1:${RouteShieldBridge.DEFAULT_PORT}`
    : "RouteShield";
}

async function init() {
  const [tab] = await api.tabs.query({ active: true, currentWindow: true });
  currentTab = tab ?? null;
  render(await send({ type: "context" }));
}

elements.refresh.addEventListener("click", async () => render(await send({ type: "refresh" })));

init().catch((error) => {
  elements.note.className = "note warn";
  elements.note.textContent = `The extension could not start: ${error.message}`;
});
