(function () {
  const $ = (s) => document.querySelector(s);
  const $$ = (s) => Array.from(document.querySelectorAll(s));

  const state = {
    config: null,
    chats: [],
    currentId: null,
    models: [],
    currentModel: "",
    streaming: false,
    pendingAssistantEl: null,
    pendingAssistantText: "",
    view: "chat",
  };

  let rpcId = 0;
  const pending = new Map();
  function call(action, payload = {}) {
    const id = "rpc" + (++rpcId);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage(JSON.stringify({ id, action, ...payload }));
    });
  }
  function emit(action, payload = {}) {
    window.chrome.webview.postMessage(JSON.stringify({ action, ...payload }));
  }

  window.chrome.webview.addEventListener("message", (e) => {
    const msg = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
    if (msg.id && pending.has(msg.id)) {
      const p = pending.get(msg.id); pending.delete(msg.id);
      msg.ok ? p.resolve(msg.data) : p.reject(new Error(msg.error));
      return;
    }
    handleEvent(msg);
  });

  function handleEvent(msg) {
    switch (msg.event) {
      case "state": onInitialState(msg); break;
      case "models": state.models = msg.models; renderModelMenu(); updateComposerModel(); break;
      case "chatChunk": onChatChunk(msg); break;
      case "chatDone": onChatDone(msg); break;
      case "chatError": onChatError(msg); break;
      case "searching": onSearching(msg); break;
      case "searchResults": onSearchResults(msg); break;
      case "pullProgress": onPullProgress(msg); break;
      case "pullDone": onPullDone(msg); break;
      case "pullCancelled": onPullCancelled(msg); break;
      case "pullError": toast("Pull failed: " + msg.message); break;
      case "error": toast(msg.message); break;
    }
  }

  async function init() {
    bindUI();
    bindKeyboard();
    initLaunchList();
    const data = await call("getInitialState");
    onInitialState(data);
    call("listModels").then(m => { state.models = m; renderModelMenu(); updateComposerModel(); }).catch(() => {});
  }

  function onInitialState(data) {
    state.config = data.config || state.config;
    state.chats = data.chats || [];
    if (state.config) {
      state.currentModel = state.config.defaultModel;
      $("#modelLabel").textContent = state.currentModel || "Select a model";
      updateComposerModel();
      if (!state.config.sidebarVisible) $("#sidebar").classList.add("collapsed");
    }
    renderChatList();
    if (state.chats.length && !state.currentId) openChat(state.chats[0].id);
    if (!data.serverReachable) toast("Ollama server not reachable. Is `ollama serve` running?");
  }

  // ---- View routing ----
  function showView(name) {
    state.view = name;
    $("#viewChat").classList.toggle("hidden", name !== "chat");
    $("#viewLaunch").classList.toggle("hidden", name !== "launch");
    $("#viewSettings").classList.toggle("hidden", name !== "settings");
    if (name === "chat") $("#promptInput").focus();
  }

  // ---- chat list ----
  function renderChatList() {
    const list = $("#chatList");
    list.innerHTML = "";
    state.chats.forEach(c => {
      const el = document.createElement("div");
      el.className = "chat-item" + (c.id === state.currentId ? " active" : "");
      el.textContent = c.title;
      const del = document.createElement("span");
      del.className = "ci-del"; del.innerHTML = "&times;"; del.title = "Delete";
      del.addEventListener("click", (ev) => { ev.stopPropagation(); deleteChat(c.id); });
      el.appendChild(del);
      el.addEventListener("click", () => openChat(c.id));
      list.appendChild(el);
    });
  }

  async function openChat(id) {
    state.currentId = id;
    const c = state.chats.find(x => x.id === id);
    if (!c) return;
    state.currentModel = c.model || state.currentModel;
    $("#modelLabel").textContent = state.currentModel || "Select a model";
    updateComposerModel();
    renderChatList();
    renderMessages(c);
    $("#emptyState").classList.toggle("hidden", c.messages.length > 0);
  }

  function renderMessages(c) {
    const m = $("#messages");
    m.innerHTML = "";
    c.messages.forEach(msg => appendMessageEl(msg.role, msg.content, { sources: msg.sources, error: msg.error, evalCount: msg.evalCount, totalMs: msg.totalMs }));
  }

  function appendMessageEl(role, content, opts = {}) {
    const m = $("#messages");
    const wrap = document.createElement("div");
    wrap.className = "msg " + role;
    const bodyHtml = opts.error
      ? `<div class="msg-error">${OllamaMD.escape(opts.error)}</div>`
      : (role === "user" ? OllamaMD.escape(content).replace(/\n/g, "<br>") : OllamaMD.render(content));
    wrap.innerHTML = `<div class="msg-role">${role}</div><div class="msg-body">${bodyHtml}</div>`;
    if (opts.sources && opts.sources.length) wrap.querySelector(".msg-body").appendChild(buildSourcesEl(opts.sources, true));
    if (role === "assistant" && !opts.error) appendMsgActions(wrap, content, opts);
    m.appendChild(wrap);
    m.scrollTop = m.scrollHeight;
    return wrap;
  }

  function buildSourcesEl(sources, compact) {
    const el = document.createElement("div");
    el.className = "sources";
    el.innerHTML = `<div class="sources-title">Sources</div>` + sources.map((s, i) =>
      `<a class="source-card" href="${OllamaMD.escape(s.url)}" target="_blank" rel="noopener">
        <div class="sc-title">[${i + 1}] ${OllamaMD.escape(s.title)}</div>
        <div class="sc-url">${OllamaMD.escape(s.url)}</div>
        ${compact ? "" : `<div class="sc-snippet">${OllamaMD.escape(s.snippet)}</div>`}
      </a>`).join("");
    return el;
  }

  function appendMsgActions(wrap, content, opts) {
    const body = wrap.querySelector(".msg-body");
    const actions = document.createElement("div");
    actions.className = "msg-actions";
    actions.innerHTML = `<button class="act-copy">Copy</button><button class="act-regen">Regenerate</button>`;
    actions.querySelector(".act-copy").addEventListener("click", () => navigator.clipboard.writeText(content));
    actions.querySelector(".act-regen").addEventListener("click", () => regenerate());
    body.appendChild(actions);
    if (opts.evalCount || opts.totalMs) {
      const meta = document.createElement("div");
      meta.className = "msg-meta";
      meta.textContent = [opts.evalCount ? opts.evalCount + " tokens" : null, opts.totalMs ? (opts.totalMs / 1000).toFixed(1) + "s" : null].filter(Boolean).join(" · ");
      body.appendChild(meta);
    }
  }

  // ---- model picker ----
  function renderModelMenu() {
    const menu = $("#modelMenu");
    menu.innerHTML = "";
    if (!state.models.length) {
      menu.innerHTML = `<div class="model-item"><div class="mi-name">No models installed</div><div class="mi-meta">Pull a model to get started</div></div><div class="mm-sep"></div>`;
    }
    state.models.forEach(mo => {
      const it = document.createElement("div");
      it.className = "model-item" + (mo.name === state.currentModel ? " selected" : "");
      it.innerHTML = `<div class="mi-name">${OllamaMD.escape(mo.name)}</div><div class="mi-meta">${formatSize(mo.size)}</div>`;
      it.addEventListener("click", () => { selectModel(mo.name); closeModelMenu(); });
      menu.appendChild(it);
    });
    const sep = document.createElement("div"); sep.className = "mm-sep"; menu.appendChild(sep);
    const pull = document.createElement("div"); pull.className = "mm-action"; pull.textContent = "+ Pull a model…";
    pull.addEventListener("click", () => { closeModelMenu(); showPullModal(); });
    menu.appendChild(pull);
    const manage = document.createElement("div"); manage.className = "mm-action"; manage.textContent = "Manage models";
    manage.addEventListener("click", () => { closeModelMenu(); showManageModelsModal(); });
    menu.appendChild(manage);
  }

  function updateComposerModel() {
    $("#composerModelLabel").textContent = state.currentModel || "Select";
  }

  function formatSize(b) {
    if (!b) return "";
    const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0; while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
    return b.toFixed(b < 10 ? 1 : 0) + " " + u[i];
  }

  function selectModel(name) {
    state.currentModel = name;
    $("#modelLabel").textContent = name;
    updateComposerModel();
    if (state.currentId) { const c = state.chats.find(x => x.id === state.currentId); if (c) c.model = name; }
    renderModelMenu();
  }

  function closeModelMenu() { $("#modelMenu").classList.add("hidden"); }

  // ---- send / stream ----
  async function send() {
    const input = $("#promptInput");
    const text = input.value.trim();
    if (!text || state.streaming) return;
    if (!state.currentModel) { toast("Select a model first"); return; }

    let chat = state.chats.find(c => c.id === state.currentId);
    if (!chat) {
      chat = await call("newChat", { model: state.currentModel });
      state.chats.unshift(chat);
      state.currentId = chat.id;
    }

    const webSearch = $("#webSearchToggle").classList.contains("active");
    chat.messages.push({ role: "user", content: text });
    appendMessageEl("user", text);
    $("#emptyState").classList.add("hidden");
    input.value = ""; autoGrow();

    const el = appendMessageEl("assistant", "");
    el.querySelector(".msg-body").innerHTML = '<span class="cursor"></span>';
    state.pendingAssistantEl = el;
    state.pendingAssistantText = "";
    state.streaming = true;
    setStreamingUI(true);

    const history = chat.messages
      .filter(m => m.role === "user" || (m.role === "assistant" && m.content))
      .map(m => ({ role: m.role, content: m.content }));

    try { await call("sendMessage", { chatId: chat.id, model: state.currentModel, messages: history, webSearch }); }
    catch (err) { onChatError({ chatId: chat.id, message: err.message }); }
  }

  function onChatChunk(msg) {
    if (!state.pendingAssistantEl) return;
    state.pendingAssistantText += msg.content;
    state.pendingAssistantEl.querySelector(".msg-body").innerHTML = OllamaMD.render(state.pendingAssistantText) + '<span class="cursor"></span>';
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  function onChatDone(msg) {
    state.streaming = false;
    setStreamingUI(false);
    if (!state.pendingAssistantEl) return;
    const text = state.pendingAssistantText;
    state.pendingAssistantEl.querySelector(".msg-body").innerHTML = OllamaMD.render(text);
    if (msg.sources && msg.sources.length) state.pendingAssistantEl.querySelector(".msg-body").appendChild(buildSourcesEl(msg.sources, true));
    appendMsgActions(state.pendingAssistantEl, text, { evalCount: msg.evalCount, totalMs: msg.totalMs });
    const c = state.chats.find(x => x.id === msg.chatId);
    if (c) {
      c.messages.push({ role: "assistant", content: text, sources: msg.sources, evalCount: msg.evalCount, totalMs: msg.totalMs });
      if (c.title === "New Chat") { c.title = deriveTitle(text); renderChatList(); }
    }
    state.pendingAssistantEl = null;
    state.pendingAssistantText = "";
  }

  function onChatError(msg) {
    state.streaming = false;
    setStreamingUI(false);
    if (state.pendingAssistantEl) {
      state.pendingAssistantEl.querySelector(".msg-body").innerHTML = `<div class="msg-error">${OllamaMD.escape(msg.message)}</div>`;
      state.pendingAssistantEl = null;
    } else { toast(msg.message); }
  }

  function onSearching(msg) {
    let s = $("#messages").querySelector(".search-status");
    if (!s) { s = document.createElement("div"); s.className = "search-status"; $("#messages").appendChild(s); }
    s.innerHTML = `<div class="spinner"></div> Searching the web for "${OllamaMD.escape(msg.query)}"…`;
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  function onSearchResults(msg) {
    const s = $("#messages").querySelector(".search-status");
    if (s) s.remove();
    if (msg.results && msg.results.length) {
      $("#messages").appendChild(buildSourcesEl(msg.results, false));
      $("#messages").scrollTop = $("#messages").scrollHeight;
    }
  }

  function regenerate() {
    if (state.streaming) return;
    const c = state.chats.find(x => x.id === state.currentId);
    if (!c) return;
    while (c.messages.length && c.messages[c.messages.length - 1].role === "assistant") c.messages.pop();
    renderMessages(c);
    const history = c.messages.map(m => ({ role: m.role, content: m.content }));
    const el = appendMessageEl("assistant", "");
    el.querySelector(".msg-body").innerHTML = '<span class="cursor"></span>';
    state.pendingAssistantEl = el; state.pendingAssistantText = ""; state.streaming = true; setStreamingUI(true);
    call("sendMessage", { chatId: c.id, model: state.currentModel, messages: history, webSearch: $("#webSearchToggle").classList.contains("active") });
  }

  function stopGeneration() { emit("stopGeneration"); }

  function setStreamingUI(on) {
    $("#sendBtn").classList.toggle("hidden", on);
    $("#stopBtn").classList.toggle("hidden", !on);
  }

  function deriveTitle(text) {
    const plain = text.replace(/[#*`>_~]/g, "").trim();
    return plain.length > 48 ? plain.substring(0, 48) + "…" : plain || "New Chat";
  }

  async function deleteChat(id) {
    await call("deleteChat", { id });
    state.chats = state.chats.filter(c => c.id !== id);
    if (state.currentId === id) {
      state.currentId = null;
      $("#messages").innerHTML = "";
      $("#emptyState").classList.remove("hidden");
    }
    renderChatList();
  }

  // ---- Launch page ----
  const LAUNCH_APPS = [
    { name: "Claude Code", desc: "Anthropic's coding tool with autogrants.", cmd: "claude", icon: "🟠" },
    { name: "Codex App", desc: "An AI agent you can delegate real work to, by OpenAI.", cmd: "codex-app", icon: "🔵" },
    { name: "Hermes Agent", desc: "Self-improving AI agent built by Nous Research.", cmd: "hermes", icon: "⚫" },
    { name: "OpenClaw", desc: "Personal AI with 100+ skills.", cmd: "openclaw", icon: "🔴" },
    { name: "OpenCode", desc: "Anomaly's open-source coding agent.", cmd: "opencode", icon: "⬜" },
    { name: "Codex", desc: "OpenAI's open source coding agent.", cmd: "codex", icon: "🟣" },
    { name: "Copilot CLI", desc: "GitHub's AI coding agent for the terminal.", cmd: "copilot", icon: "⚪" },
    { name: "Droid", desc: "Factory's coding agent across terminal and IDEs.", cmd: "droid", icon: "🟤" },
  ];

  function initLaunchList() {
    const list = $("#launchList");
    list.innerHTML = "";
    LAUNCH_APPS.forEach(app => {
      const item = document.createElement("div");
      item.className = "launch-item";
      item.innerHTML = `
        <div class="launch-icon">${app.icon}</div>
        <div class="launch-info">
          <div class="launch-name">${OllamaMD.escape(app.name)}</div>
          <div class="launch-desc">${OllamaMD.escape(app.desc)}</div>
          <div class="launch-cmd-bar">
            <span class="launch-cmd">ollama launch ${OllamaMD.escape(app.cmd)}</span>
            <button class="launch-copy" title="Copy">
              <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>
            </button>
          </div>
        </div>
      `;
      item.querySelector(".launch-copy").addEventListener("click", () => {
        navigator.clipboard.writeText(`ollama launch ${app.cmd}`);
        toast("Copied to clipboard");
      });
      list.appendChild(item);
    });
  }

  // ---- Settings page ----
  function initSettings() {
    $("#profileName").textContent = state.config?.defaultModel || "User";
    $("#profileAvatar").textContent = (state.config?.defaultModel || "U").charAt(0).toUpperCase();

    // Toggles
    $$(".toggle").forEach(t => {
      t.addEventListener("click", () => { t.classList.toggle("on"); });
    });

    // Close behavior buttons
    const cb = state.config?.closeBehavior || "tray";
    $$("#closeBehaviorOptions .pill-btn").forEach(btn => btn.classList.remove("selected"));
    const activeBtn = $(`#closeBehaviorOptions [data-val="${cb}"]`);
    if (activeBtn) activeBtn.classList.add("selected");

    $$("#closeBehaviorOptions .pill-btn").forEach(btn => {
      btn.onclick = () => {
        $$("#closeBehaviorOptions .pill-btn").forEach(b => b.classList.remove("selected"));
        btn.classList.add("selected");
        const val = btn.dataset.val;
        if (state.config) { state.config.closeBehavior = val; call("saveConfig", { config: state.config }); }
        toast(val === "tray" ? "App will keep running in the background when closed" : "App will quit when closed");
      };
    });

    $("#browseModelPath").addEventListener("click", () => toast("Browse dialog would open here"));
    $("#resetDefaults").addEventListener("click", () => {
      $$(".toggle").forEach(t => t.classList.add("on"));
      $$("#closeBehaviorOptions .pill-btn").forEach(b => b.classList.remove("selected"));
      $("#cbTray").classList.add("selected");
      if (state.config) {
        state.config.closeBehavior = "tray";
        call("saveConfig", { config: state.config });
      }
      toast("Settings reset to defaults");
    });
  }

  // ---- modals (manage models, pull) ----
  function showManageModelsModal() {
    modal(`
      <div class="modal-header">Models <button class="close-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">
        <div style="margin-bottom:12px"><button class="pill-btn outline" id="refreshModels">Refresh</button> <button class="pill-btn primary" id="pullFromLib">Pull from library…</button></div>
        <div id="modelsList"></div>
      </div>
      <div class="modal-footer"><button class="pill-btn outline" id="closeModels">Close</button></div>
    `);
    const render = () => {
      const list = $("#modelsList"); list.innerHTML = "";
      if (!state.models.length) { list.innerHTML = '<div style="color:var(--text-muted);font-size:13px">No models installed. Pull one from the library.</div>'; return; }
      state.models.forEach(m => {
        const row = document.createElement("div"); row.className = "kv-row";
        row.innerHTML = `<div><div>${OllamaMD.escape(m.name)}</div><div class="kv-val">${formatSize(m.size)}</div></div><div><button class="pill-btn outline danger" data-del="${OllamaMD.escape(m.name)}">Delete</button></div>`;
        list.appendChild(row);
      });
      $$("[data-del]").forEach(b => b.addEventListener("click", async () => {
        if (!confirm("Delete " + b.dataset.del + "?")) return;
        await call("deleteModel", { name: b.dataset.del });
        await refreshModels();
      }));
    };
    render();
    $("#closeModal").onclick = closeModal; $("#closeModels").onclick = closeModal;
    $("#refreshModels").onclick = refreshModels;
    $("#pullFromLib").onclick = showPullModal;
  }

  function refreshModels() { return call("listModels").then(m => { state.models = m; renderModelMenu(); updateComposerModel(); if ($("#modelsList")) showManageModelsModal(); }).catch(() => {}); }

  function showPullModal() {
    const popular = ["llama3.2", "llama3.2:1b", "qwen2.5", "phi3", "mistral", "gemma2", "deepseek-r1", "llava", "nomic-embed-text"];
    modal(`
      <div class="modal-header">Pull Model <button class="close-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">
        <div class="field"><label>Model name</label><input type="text" id="pullName" placeholder="e.g. llama3.2" list="pullList" style="width:100%;padding:9px 12px;background:var(--bg);border:1px solid var(--border);border-radius:8px;color:var(--text);font-size:14px"/></div>
        <div class="pull-progress"><div class="bar" id="pullBar"></div></div>
        <div id="pullStatus" style="color:var(--text-muted);font-size:13px"></div>
        <div style="margin-top:8px"><a href="https://ollama.com/library" target="_blank" style="color:var(--accent-blue);font-size:13px">Browse the model library ↗</a></div>
      </div>
      <div class="modal-footer"><button class="pill-btn outline" id="cancelPull">Cancel</button><button class="pill-btn primary" id="doPull">Pull</button></div>
    `);
    $("#closeModal").onclick = closeModal; $("#cancelPull").onclick = closeModal;
    $("#doPull").onclick = () => {
      const name = $("#pullName").value.trim(); if (!name) return;
      $("#doPull").disabled = true; $("#pullStatus").textContent = "Starting…";
      emit("pullModel", { name });
    };
  }

  function onPullProgress(msg) {
    if (!$("#pullBar")) return;
    $("#pullBar").style.width = msg.percent + "%";
    $("#pullStatus").textContent = msg.status + (msg.total ? ` · ${msg.percent}%` : "");
  }
  function onPullDone(msg) { if ($("#pullStatus")) { $("#pullStatus").textContent = "Done: " + msg.name; if ($("#doPull")) $("#doPull").disabled = false; } toast("Pulled " + msg.name); refreshModels(); }
  function onPullCancelled(msg) { if ($("#pullStatus")) $("#pullStatus").textContent = "Cancelled"; if ($("#doPull")) $("#doPull").disabled = false; }

  function modal(html) {
    const root = $("#modalRoot");
    root.innerHTML = `<div class="modal">${html}</div>`;
    root.classList.add("visible");
    root.onclick = (e) => { if (e.target === root) closeModal(); };
  }
  function closeModal() { $("#modalRoot").classList.remove("visible"); $("#modalRoot").innerHTML = ""; }

  // ---- UI binding ----
  function bindUI() {
    $("#newChatBtn").addEventListener("click", newChat);
    $("#launchBtn").addEventListener("click", () => showView("launch"));
    $("#settingsBtn").addEventListener("click", () => { showView("settings"); initSettings(); });
    $("#backFromLaunch").addEventListener("click", () => showView("chat"));
    $("#backFromSettings").addEventListener("click", () => showView("chat"));

    $("#sidebarToggle") && $("#sidebarToggle").addEventListener("click", () => $("#sidebar").classList.toggle("collapsed"));
    $("#modelBtn").addEventListener("click", (e) => { e.stopPropagation(); $("#modelMenu").classList.toggle("hidden"); });
    $("#modelPill").addEventListener("click", (e) => { e.stopPropagation(); $("#modelMenu").classList.toggle("hidden"); });
    document.addEventListener("click", (e) => { if (!e.target.closest(".model-picker")) closeModelMenu(); });

    $("#webSearchToggle").addEventListener("click", () => {
      $("#webSearchToggle").classList.toggle("active");
    });

    $("#sendBtn").addEventListener("click", send);
    $("#stopBtn").addEventListener("click", stopGeneration);
    const input = $("#promptInput");
    input.addEventListener("input", autoGrow);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
    });
  }

  function autoGrow() {
    const t = $("#promptInput"); t.style.height = "auto"; t.style.height = Math.min(t.scrollHeight, 200) + "px";
  }

  function bindKeyboard() {
    document.addEventListener("keydown", (e) => {
      const ctrl = e.ctrlKey || e.metaKey;
      if (ctrl && e.key.toLowerCase() === "n") { e.preventDefault(); newChat(); }
      else if (ctrl && e.key.toLowerCase() === "b") { e.preventDefault(); $("#sidebar").classList.toggle("collapsed"); }
      else if (e.key === "Escape" && state.streaming) { stopGeneration(); }
    });
  }

  async function newChat() {
    const chat = await call("newChat", { model: state.currentModel });
    state.chats.unshift(chat);
    showView("chat");
    openChat(chat.id);
    $("#emptyState").classList.remove("hidden");
    $("#messages").innerHTML = "";
    $("#promptInput").focus();
  }

  function toast(msg) {
    let t = $("#toast");
    if (!t) { t = document.createElement("div"); t.id = "toast"; t.style.cssText = "position:fixed;bottom:20px;left:50%;transform:translateX(-50%);background:#27272A;color:#FAFAFA;padding:10px 16px;border-radius:8px;font-size:13px;z-index:200;border:1px solid #3F3F46"; document.body.appendChild(t); }
    t.textContent = msg; t.style.opacity = "1"; t.style.display = "block";
    clearTimeout(t._h); t._h = setTimeout(() => { t.style.opacity = "0"; setTimeout(() => t.style.display = "none", 200); }, 3000);
  }

  window.OllamaUI = { copyCode: (btn) => {
    const code = btn.closest("pre").querySelector("code").innerText;
    navigator.clipboard.writeText(code).then(() => { btn.textContent = "Copied"; setTimeout(() => btn.textContent = "Copy", 1200); });
  } };

  document.addEventListener("DOMContentLoaded", init);
})();
