// prototypes/cli-tui.ts — behavior for prototypes/cli-tui.html.
// Faithful full-window mock of the epic-setup selection screen (masonry
// columns, category headers, footer), three directions for integrating
// CLI/TUI apps. Data = remote catalog + the 7 new Development entries.
// Compile and inline over the <script> //__APP_JS__ marker:
//   npx -p typescript tsc --target es2020 --lib es2020,dom --strict prototypes/cli-tui.ts --outDir %TEMP%

type AppKind = "gui" | "terminal" | "tui";

interface ProtoApp {
  id: string;
  name: string;
  desc: string;
  kind: AppKind;
  via: string;
  chips?: string[]; // REVIEW, REQ. SETUP
}

interface Cat { name: string; hint?: string; apps: ProtoApp[]; }

const DEVS: ProtoApp[] = [
  { id: "vscode", name: "Visual Studio Code", desc: "Lightweight, extensible code editor", kind: "gui", via: "winget", chips: ["REVIEW"] },
  { id: "cursor", name: "Cursor", desc: "AI code editor", kind: "gui", via: "winget", chips: ["REVIEW"] },
  { id: "opencode", name: "opencode", desc: "Open-source AI coding agent for the terminal", kind: "tui", via: "github zip" },
  { id: "claude-code", name: "Claude Code", desc: "Anthropic agentic coding tool for the terminal", kind: "terminal", via: "winget" },
  { id: "kimi-code", name: "Kimi Code", desc: "Moonshot AI coding assistant CLI", kind: "terminal", via: "winget" },
  { id: "devin-cli", name: "Devin CLI", desc: "Cognition coding agent for the terminal", kind: "terminal", via: "script" },
  { id: "hermes-agent", name: "Hermes Agent", desc: "Open-source AI agent, CLI plus desktop", kind: "tui", via: "script" },
  { id: "zcode", name: "Z Code", desc: "Z.AI coding agent and editor", kind: "gui", via: "winget" },
  { id: "chatgpt", name: "ChatGPT", desc: "OpenAI chat app (Store install)", kind: "gui", via: "store", chips: ["REVIEW", "REQ. SETUP"] },
  { id: "git", name: "Git", desc: "Distributed version control for Windows", kind: "gui", via: "winget", chips: ["REVIEW"] },
];

const BASE_CATS: Cat[] = [
  { name: "Browsers", hint: "web & private browsers", apps: [
    { id: "chrome", name: "Google Chrome", desc: "Fast, secure browser by Google", kind: "gui", via: "winget" },
    { id: "brave", name: "Brave", desc: "Privacy-first browser", kind: "gui", via: "winget" },
    { id: "helium", name: "Helium", desc: "Privacy-focused Chromium fork", kind: "gui", via: "winget" },
  ]},
  { name: "Communication", hint: "chat & meetings", apps: [
    { id: "discord", name: "Discord", desc: "Voice, video & text chat", kind: "gui", via: "winget" },
    { id: "telegram", name: "Telegram Desktop", desc: "Fast and secure messaging", kind: "gui", via: "winget" },
  ]},
  { name: "Media", hint: "video, audio & creation", apps: [
    { id: "obs-studio", name: "OBS Studio", desc: "Streaming and recording studio", kind: "gui", via: "winget" },
    { id: "ffmpeg", name: "FFmpeg", desc: "Audio/video toolkit, portable", kind: "terminal", via: "winget" },
    { id: "yt-dlp", name: "yt-dlp", desc: "Command-line video downloader", kind: "terminal", via: "winget" },
  ]},
  { name: "Utilities", hint: "files & system", apps: [
    { id: "7-zip", name: "7-Zip", desc: "Open-source file archiver", kind: "gui", via: "winget" },
    { id: "powertoys", name: "PowerToys", desc: "Power-user utilities", kind: "gui", via: "winget" },
  ]},
  { name: "Development", hint: "tools & runtimes", apps: DEVS },
  { name: "Gaming", hint: "launchers, games & tools", apps: [
    { id: "steam", name: "Steam", desc: "Valve game store & library", kind: "gui", via: "winget" },
    { id: "osu-lazer", name: "osu! lazer", desc: "Open-source rhythm game", kind: "gui", via: "winget" },
  ]},
];

const RUN_CMD: Record<string, string> = {
  "opencode": "opencode", "claude-code": "claude", "kimi-code": "kimi",
  "devin-cli": "devin", "hermes-agent": "hermes", "ffmpeg": "ffmpeg", "yt-dlp": "yt-dlp",
};

function el<T extends HTMLElement>(id: string): T {
  const n = document.getElementById(id);
  if (!n) throw new Error("missing #" + id);
  return n as T;
}

function esc(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function kindBadge(a: ProtoApp): string {
  if (a.kind === "gui") return "";
  const cls = a.kind === "tui" ? "badge-tui" : "badge-cli";
  return `<span class="badge ${cls}">${a.kind === "tui" ? "TUI" : "CLI"}</span>`;
}

function warnChips(a: ProtoApp): string {
  return (a.chips ?? []).map((c) =>
    `<span class="badge ${c === "REVIEW" ? "badge-review" : "badge-req"}">${c}</span>`).join("");
}

function rowHtml(a: ProtoApp, checked: boolean, opts: { badges: boolean; runHint: boolean }): string {
  const run = opts.runHint && RUN_CMD[a.id] ? `<span class="run-hint">run › <code>${RUN_CMD[a.id]}</code></span>` : "";
  return `<label class="row" data-id="${a.id}">
    <span class="box${checked ? " on" : ""}">${checked ? "✓" : ""}</span>
    <span class="chip">${esc(a.name[0])}</span>
    <span class="row-main">
      <span class="row-name">${esc(a.name)}${opts.badges ? kindBadge(a) : ""}${warnChips(a)}</span>
      <span class="row-sub">${esc(a.desc)}</span>${run}
    </span>
  </label>`;
}

function colHtml(c: Cat, checked: Set<string>, n: number, opts: { badges: boolean; runHint: boolean }): string {
  const sel = c.apps.filter((a) => checked.has(a.id)).length;
  const hint = c.hint ? `<span class="cat-hint">(${esc(c.hint)})</span>` : "";
  return `<div class="col" data-col="${esc(c.name)}">
    <div class="cat-head"><span class="box"></span>
      <span class="cat-name">${esc(c.name)}</span>${hint}
      <span class="cat-count">${sel}/${c.apps.length}</span>
    </div>
    ${c.apps.map((a) => rowHtml(a, checked.has(a.id), opts)).join("")}
  </div>`;
}

function wireRows(root: HTMLElement, checked: Set<string>, rerender: () => void): void {
  for (const r of Array.from(root.querySelectorAll<HTMLElement>(".row"))) {
    r.addEventListener("click", (ev) => {
      ev.preventDefault();
      const id = r.dataset.id ?? "";
      if (checked.has(id)) checked.delete(id); else checked.add(id);
      rerender();
    });
  }
}

/* ---------- switcher ---------- */
function initSwitcher(): void {
  const btns = Array.from(document.querySelectorAll<HTMLButtonElement>("[data-proto]"));
  const panes = Array.from(document.querySelectorAll<HTMLElement>("[data-pane]"));
  const note = el<HTMLElement>("proto-note");
  const notes: Record<string, string> = {
    a: "A · Kind badges in place — terminal entries stay where they are, marked CLI/TUI next to REVIEW. One badge style + one filter.",
    b: "B · Terminal category — CLI/TUI tools move into their own masonry column with run-command hints. No new widgets, regrouped data.",
    c: "C · Terminal install stage — selection gains badges, and script installs get an approve-before-run terminal drawer.",
  };
  function show(key: string): void {
    for (const b of btns) b.classList.toggle("active", b.dataset.proto === key);
    for (const p of panes) p.classList.toggle("active", p.dataset.pane === key);
    note.textContent = notes[key] ?? "";
  }
  for (const b of btns) b.addEventListener("click", () => show(b.dataset.proto ?? "a"));
  show("a");
}

/* ---------- A: badges + kind filter ---------- */
function initA(): void {
  const cols = el<HTMLElement>("a-cols");
  const status = el<HTMLElement>("a-status");
  const cont = el<HTMLElement>("a-continue");
  const segs = Array.from(document.querySelectorAll<HTMLButtonElement>("[data-kind]"));
  const checked = new Set<string>();
  let kind: string = "all";

  function render(): void {
    const visible = BASE_CATS.map((c) => ({
      ...c, apps: c.apps.filter((a) => kind === "all" || a.kind === kind),
    })).filter((c) => c.apps.length > 0);
    cols.innerHTML = visible.map((c) => colHtml(c, checked, 0, { badges: true, runHint: false })).join("");
    wireRows(cols, checked, render);
    const total = BASE_CATS.reduce((n, c) => n + c.apps.length, 0);
    status.textContent = `${BASE_CATS.length} categories · ${total} apps available.`;
    cont.textContent = `Continue (${checked.size}/${total})`;
  }
  for (const s of segs) s.addEventListener("click", () => {
    kind = s.dataset.kind ?? "all";
    for (const x of segs) x.classList.toggle("active", x === s);
    render();
  });
  render();
}

/* ---------- B: terminal category ---------- */
function terminalCats(): Cat[] {
  const termApps = BASE_CATS.flatMap((c) => c.apps).filter((a) => a.kind !== "gui");
  const guiCats = BASE_CATS.map((c) => ({ ...c, apps: c.apps.filter((a) => a.kind === "gui") }))
    .filter((c) => c.apps.length > 0);
  const out: Cat[] = [];
  for (const c of guiCats) {
    out.push(c);
    if (c.name === "Development") out.push({ name: "Terminal", hint: "cli & tui tools", apps: termApps });
  }
  return out;
}

function initB(): void {
  const cols = el<HTMLElement>("b-cols");
  const sel = el<HTMLSelectElement>("b-select");
  const cont = el<HTMLElement>("b-continue");
  const checked = new Set<string>();
  const cats = terminalCats();

  sel.innerHTML = `<option value="__all">All categories</option>` +
    cats.map((c) => `<option value="${esc(c.name)}">${esc(c.name)}</option>`).join("");

  function render(): void {
    const f = sel.value;
    const visible = cats.filter((c) => f === "__all" || c.name === f);
    cols.innerHTML = visible.map((c) => colHtml(c, checked, 0, { badges: false, runHint: true })).join("");
    wireRows(cols, checked, render);
    const total = cats.reduce((n, c) => n + c.apps.length, 0);
    cont.textContent = `Continue (${checked.size}/${total})`;
  }
  sel.addEventListener("change", render);
  render();
}

/* ---------- C: terminal install stage ---------- */
const C_LINES: string[] = [
  "$ iex (irm https://static.devin.ai/cli/setup.ps1)",
  "downloading devin cli (win32-x64) … 12.4 MB",
  "installing to %USERPROFILE%\\.devin\\bin",
  "added to PATH — reopen the terminal to use it",
  "$ devin --version",
  "devin 1.4.0 (windows-x64)",
  "✓ devin-cli installed",
];

function initC(): void {
  const cols = el<HTMLElement>("c-cols");
  const cont = el<HTMLElement>("c-continue");
  const checked = new Set<string>(["devin-cli"]);
  const selView = el<HTMLElement>("c-select-view");
  const instView = el<HTMLElement>("c-install-view");
  const term = el<HTMLElement>("c-term");
  const approve = el<HTMLButtonElement>("c-approve");
  const back = el<HTMLButtonElement>("c-back");
  const status = el<HTMLElement>("c-status");
  let running = false;

  function render(): void {
    cols.innerHTML = BASE_CATS.map((c) => colHtml(c, checked, 0, { badges: true, runHint: false })).join("");
    wireRows(cols, checked, render);
    const total = BASE_CATS.reduce((n, c) => n + c.apps.length, 0);
    cont.textContent = `Continue (${checked.size}/${total})`;
  }

  cont.addEventListener("click", () => {
    if (checked.size === 0) return;
    selView.classList.remove("active");
    instView.classList.add("active");
  });
  back.addEventListener("click", () => {
    instView.classList.remove("active");
    selView.classList.add("active");
  });

  approve.addEventListener("click", async () => {
    if (running) return;
    running = true;
    approve.disabled = true;
    term.innerHTML = "";
    status.textContent = "Installing 1 app…";
    for (const line of C_LINES) {
      const div = document.createElement("div");
      div.className = "t-line" + (line.startsWith("$") ? " t-cmd" : line.startsWith("✓") ? " t-ok" : "");
      div.textContent = line;
      term.appendChild(div);
      term.scrollTop = term.scrollHeight;
      await new Promise((r) => setTimeout(r, 300));
    }
    status.textContent = "Done: 1 installed. Next: open a terminal and run devin";
    running = false;
  });

  render();
}

document.addEventListener("DOMContentLoaded", () => {
  initSwitcher();
  initA();
  initB();
  initC();
});
