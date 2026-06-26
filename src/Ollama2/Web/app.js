// Ollama UI controller. Bridges the DOM to the C# backend via WebView2 messaging.
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
    pendingSources: null,
    searching: false,
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

  // ---- incoming messages from C# ----
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
      case "models": state.models = msg.models; renderModelMenu(); break;
      case "chatChunk": onChatChunk(msg); break;
      case "chatDone": onChatDone(msg); break;
      case "chatError": onChatError(msg); break;
      case "searching": onSearching(msg); break;
      case "searchResults": onSearchResults(msg); break;
      case "pullProgress": onPullProgress(msg); break;
      case "pullDone": onPullDone(msg); break;
      case "pullCancelled": onPullCancelled(msg); break;
      case "pullError": alert("Pull failed: " + msg.message); break;
      case "menu": onMenuEvent(msg.action); break;
      case "error": toast(msg.message); break;
    }
  }

  // ---- bootstrap ----
  async function init() {
    bindUI();
    bindKeyboard();
    const data = await call("getInitialState");
    onInitialState(data);
    await call("listModels").then(m => { state.models = m; renderModelMenu(); }).catch(() => {});
  }

  function onInitialState(data) {
    state.config = data.config || state.config;
    state.chats = data.chats || [];
    if (state.config) {
      state.currentModel = state.config.defaultModel;
      document.documentElement.setAttribute("data-theme", state.config.theme || "dark");
      $("#webSearchCheckbox").checked = !!state.config.webSearchEnabled;
      updateWebSearchToggle();
      if (!state.config.sidebarVisible) $("#sidebar").classList.add("collapsed");
    }
    renderChatList();
    if (state.chats.length && !state.currentId) openChat(state.chats[0].id);
    if (!data.serverReachable) toast("Ollama server not reachable at " + (state.config?.serverUrl || "localhost:11434") + ". Is `ollama serve` running?");
  }

  // ---- chat list ----
  function renderChatList() {
    const q = ($("#searchInput").value || "").toLowerCase();
    const list = $("#chatList");
    list.innerHTML = "";
    state.chats
      .filter(c => !q || c.title.toLowerCase().includes(q))
      .forEach(c => {
        const el = document.createElement("div");
        el.className = "chat-item" + (c.id === state.currentId ? " active" : "");
        el.innerHTML = `<div class="ci-title">${OllamaMD.escape(c.title)}</div>
          <div class="ci-meta"><span>${c.model || ""}</span><span>·</span><span>${timeAgo(c.updatedAt)}</span></div>
          <button class="ci-del" title="Delete">&times;</button>`;
        el.addEventListener("click", () => openChat(c.id));
        el.querySelector(".ci-del").addEventListener("click", (ev) => { ev.stopPropagation(); deleteChat(c.id); });
        list.appendChild(el);
      });
  }

  function timeAgo(ts) {
    if (!ts) return "";
    const s = Math.floor(Date.now() / 1000 - ts);
    if (s < 60) return "just now";
    if (s < 3600) return Math.floor(s / 60) + "m";
    if (s < 86400) return Math.floor(s / 3600) + "h";
    return Math.floor(s / 86400) + "d";
  }

  async function openChat(id) {
    state.currentId = id;
    const c = state.chats.find(x => x.id === id);
    if (!c) return;
    state.currentModel = c.model || state.currentModel;
    $("#modelLabel").textContent = state.currentModel || "Select a model";
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
    const avatar = role === "user" ? "You" : "O";
    const bodyHtml = opts.error
      ? `<div class="msg-error">${OllamaMD.escape(opts.error)}</div>`
      : (role === "user" ? OllamaMD.escape(content).replace(/\n/g, "<br>") : OllamaMD.render(content));
    wrap.innerHTML = `<div class="msg-avatar">${avatar}</div><div class="msg-body">${bodyHtml}</div>`;
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
      it.innerHTML = `<div class="mi-name">${OllamaMD.escape(mo.name)}</div><div class="mi-meta">${formatSize(mo.size)} · ${mo.details || ""}</div>`;
      it.addEventListener("click", () => { selectModel(mo.name); closeModelMenu(); });
      menu.appendChild(it);
    });
    const sep = document.createElement("div"); sep.className = "mm-sep"; menu.appendChild(sep);
    const pull = document.createElement("div"); pull.className = "mm-action"; pull.textContent = "+ Pull a model…";
    pull.addEventListener("click", () => { closeModelMenu(); showPullModal(); });
    menu.appendChild(pull);
    const manage = document.createElement("div"); manage.className = "mm-action"; manage.textContent = "Manage models";
    manage.addEventListener("click", () => { closeModelMenu(); showModelsModal(); });
    menu.appendChild(manage);
  }

  function formatSize(b) {
    if (!b) return "";
    const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0; while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
    return b.toFixed(b < 10 ? 1 : 0) + " " + u[i];
  }

  function selectModel(name) {
    state.currentModel = name;
    $("#modelLabel").textContent = name;
    if (state.currentId) {
      const c = state.chats.find(x => x.id === state.currentId);
      if (c) c.model = name;
    }
    renderModelMenu();
  }

  function closeModelMenu() { $("#modelMenu").classList.add("hidden"); }

  // ---- send message / streaming ----
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

    const webSearch = $("#webSearchCheckbox").checked;
    chat.messages.push({ role: "user", content: text });
    appendMessageEl("user", text);
    $("#emptyState").classList.add("hidden");
    input.value = ""; autoGrow();

    // assistant placeholder
    const el = appendMessageEl("assistant", "");
    const body = el.querySelector(".msg-body");
    body.innerHTML = '<span class="cursor"></span>';
    state.pendingAssistantEl = el;
    state.pendingAssistantText = "";
    state.pendingSources = null;
    state.streaming = true;
    setStreamingUI(true);

    // build message history for the server (exclude the placeholder)
    const history = chat.messages
      .filter(m => m.role === "user" || (m.role === "assistant" && m.content))
      .map(m => ({ role: m.role, content: m.content }));

    try {
      await call("sendMessage", { chatId: chat.id, model: state.currentModel, messages: history, webSearch });
    } catch (err) {
      onChatError({ chatId: chat.id, message: err.message });
    }
  }

  function onChatChunk(msg) {
    if (!state.pendingAssistantEl) return;
    state.pendingAssistantText += msg.content;
    const body = state.pendingAssistantEl.querySelector(".msg-body");
    body.innerHTML = OllamaMD.render(state.pendingAssistantText) + '<span class="cursor"></span>';
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  function onChatDone(msg) {
    state.streaming = false;
    setStreamingUI(false);
    if (!state.pendingAssistantEl) return;
    const body = state.pendingAssistantEl.querySelector(".msg-body");
    const text = state.pendingAssistantText;
    body.innerHTML = OllamaMD.render(text);
    if (msg.sources && msg.sources.length) body.appendChild(buildSourcesEl(msg.sources, true));
    appendMsgActions(state.pendingAssistantEl, text, { evalCount: msg.evalCount, totalMs: msg.totalMs });
    // persist into local chat object
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
    } else {
      toast(msg.message);
    }
  }

  function onSearching(msg) {
    state.searching = true;
    let s = $("#messages").querySelector(".search-status");
    if (!s) {
      s = document.createElement("div"); s.className = "search-status";
      $("#messages").appendChild(s);
    }
    s.innerHTML = `<div class="spinner"></div> Searching the web for "${OllamaMD.escape(msg.query)}"…`;
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  function onSearchResults(msg) {
    const s = $("#messages").querySelector(".search-status");
    if (s) s.remove();
    if (msg.results && msg.results.length) {
      const el = buildSourcesEl(msg.results, false);
      $("#messages").appendChild(el);
      $("#messages").scrollTop = $("#messages").scrollHeight;
    }
    state.pendingSources = msg.results;
  }

  function regenerate() {
    if (state.streaming) return;
    const c = state.chats.find(x => x.id === state.currentId);
    if (!c) return;
    // drop last assistant message
    while (c.messages.length && c.messages[c.messages.length - 1].role === "assistant") c.messages.pop();
    renderMessages(c);
    const history = c.messages.map(m => ({ role: m.role, content: m.content }));
    const el = appendMessageEl("assistant", "");
    el.querySelector(".msg-body").innerHTML = '<span class="cursor"></span>';
    state.pendingAssistantEl = el; state.pendingAssistantText = ""; state.streaming = true; setStreamingUI(true);
    call("sendMessage", { chatId: c.id, model: state.currentModel, messages: history, webSearch: $("#webSearchCheckbox").checked });
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

  // ---- modals ----
  function modal(html) {
    const root = $("#modalRoot");
    root.innerHTML = `<div class="modal">${html}</div>`;
    root.classList.remove("hidden");
    root.onclick = (e) => { if (e.target === root) closeModal(); };
  }
  function closeModal() { $("#modalRoot").classList.add("hidden"); $("#modalRoot").innerHTML = ""; }

  function showSettingsModal(tab = "general") {
    const c = state.config;
    modal(`
      <div class="modal-header">Settings <button class="icon-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">
        <div class="tabs">
          <button class="tab" data-tab="general">General</button>
          <button class="tab" data-tab="model">Model</button>
          <button class="tab" data-tab="web">Web Search</button>
          <button class="tab" data-tab="server">Server</button>
        </div>
        <div data-pane="general">
          <div class="field"><label>Theme</label><select id="cfgTheme"><option value="dark">Dark</option><option value="light">Light</option></select></div>
          <div class="field"><label>Default model</label><input type="text" id="cfgDefaultModel" /></div>
          <div class="field"><label>System prompt</label><textarea id="cfgSystem"></textarea></div>
          <div class="field"><label>Zoom</label><input type="number" id="cfgZoom" step="0.1" min="0.25" max="5" /></div>
        </div>
        <div data-pane="model" class="hidden">
          <div class="row">
            <div class="field"><label>Temperature</label><input type="number" id="cfgTemp" step="0.05" min="0" max="2" /></div>
            <div class="field"><label>Top K</label><input type="number" id="cfgTopK" min="1" /></div>
          </div>
          <div class="row">
            <div class="field"><label>Top P</label><input type="number" id="cfgTopP" step="0.05" min="0" max="1" /></div>
            <div class="field"><label>Context window</label><input type="number" id="cfgCtx" step="512" /></div>
          </div>
        </div>
        <div data-pane="web" class="hidden">
          <div class="field"><label class="checkbox-line"><input type="checkbox" id="cfgWsEnabled" /> Enable built-in web search</label></div>
          <div class="field"><label>Search provider</label><select id="cfgWsProvider"><option value="duckduckgo">DuckDuckGo (no key)</option><option value="brave">Brave (key)</option><option value="tavily">Tavily (key)</option></select></div>
          <div class="field"><label>API key (optional)</label><input type="text" id="cfgWsKey" placeholder="Only for Brave/Tavily" /></div>
          <div class="field"><label>Max results</label><input type="number" id="cfgWsMax" min="1" max="20" /></div>
        </div>
        <div data-pane="server" class="hidden">
          <div class="field"><label>Ollama server URL</label><input type="text" id="cfgServer" /></div>
          <button class="btn" id="testServer">Test connection</button> <span id="testResult"></span>
        </div>
      </div>
      <div class="modal-footer"><button class="btn" id="cancelSettings">Cancel</button><button class="btn primary" id="saveSettings">Save</button></div>
    `);
    $("#cfgTheme").value = c.theme; $("#cfgDefaultModel").value = c.defaultModel; $("#cfgSystem").value = c.systemPrompt;
    $("#cfgZoom").value = c.zoom; $("#cfgTemp").value = c.temperature; $("#cfgTopK").value = c.topK;
    $("#cfgTopP").value = c.topP; $("#cfgCtx").value = c.numCtx; $("#cfgWsEnabled").checked = c.webSearchEnabled;
    $("#cfgWsProvider").value = c.webSearchProvider || "duckduckgo"; $("#cfgWsKey").value = c.webSearchApiKey || "";
    $("#cfgWsMax").value = c.maxSearchResults; $("#cfgServer").value = c.serverUrl;
    $$(".tab").forEach(t => t.addEventListener("click", () => {
      $$(".tab").forEach(x => x.classList.remove("active")); t.classList.add("active");
      $$("[data-pane]").forEach(p => p.classList.toggle("hidden", p.dataset.pane !== t.dataset.tab));
    }));
    const t = $$(`.tab[data-tab="${tab}"]`)[0]; if (t) t.click();
    $("#closeModal").onclick = closeModal; $("#cancelSettings").onclick = closeModal;
    $("#testServer").onclick = async () => {
      $("#testResult").textContent = "Testing…";
      const ok = await call("testServer", { url: $("#cfgServer").value });
      $("#testResult").textContent = ok ? "✓ Connected" : "✗ Not reachable";
    };
    $("#saveSettings").onclick = async () => {
      const cfg = {
        serverUrl: $("#cfgServer").value, defaultModel: $("#cfgDefaultModel").value,
        webSearchEnabled: $("#cfgWsEnabled").checked, webSearchProvider: $("#cfgWsProvider").value,
        webSearchApiKey: $("#cfgWsKey").value, theme: $("#cfgTheme").value,
        sidebarVisible: state.config.sidebarVisible, zoom: parseFloat($("#cfgZoom").value) || 1,
        temperature: parseFloat($("#cfgTemp").value) || 0.8, topK: parseInt($("#cfgTopK").value) || 40,
        topP: parseFloat($("#cfgTopP").value) || 0.9, numCtx: parseInt($("#cfgCtx").value) || 4096,
        systemPrompt: $("#cfgSystem").value, maxSearchResults: parseInt($("#cfgWsMax").value) || 5,
        stream: true,
      };
      await call("saveConfig", { config: cfg });
      state.config = cfg;
      document.documentElement.setAttribute("data-theme", cfg.theme);
      closeModal();
      toast("Settings saved");
    };
  }

  function showModelsModal() {
    modal(`
      <div class="modal-header">Manage Models <button class="icon-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">
        <button class="btn primary" id="refreshModels">Refresh</button> <button class="btn" id="pullFromLib">Pull from library…</button>
        <div id="modelsList" style="margin-top:14px"></div>
      </div>
      <div class="modal-footer"><button class="btn" id="closeModels">Close</button></div>
    `);
    const render = () => {
      const list = $("#modelsList"); list.innerHTML = "";
      if (!state.models.length) { list.innerHTML = '<div class="kv-val">No models installed. Pull one from the library.</div>'; return; }
      state.models.forEach(m => {
        const row = document.createElement("div"); row.className = "kv-row";
        row.innerHTML = `<div><div>${OllamaMD.escape(m.name)}</div><div class="kv-val">${formatSize(m.size)} · ${OllamaMD.escape(m.digest).substring(0,12)}</div></div><div><button class="btn danger" data-del="${OllamaMD.escape(m.name)}">Delete</button></div>`;
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

  function refreshModels() { return call("listModels").then(m => { state.models = m; renderModelMenu(); if ($("#modelsList")) { showModelsModal(); } }).catch(() => {}); }

  function showPullModal() {
    const popular = ["llama3.2", "llama3.2:1b", "qwen2.5", "phi3", "mistral", "gemma2", "deepseek-r1", "llava", "nomic-embed-text"];
    modal(`
      <div class="modal-header">Pull Model <button class="icon-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">
        <div class="field"><label>Model name</label><input type="text" id="pullName" placeholder="e.g. llama3.2" list="pullList" />
          <datalist id="pullList">${popular.map(p => `<option value="${p}">`).join("")}</datalist></div>
        <div class="pull-progress"><div class="bar" id="pullBar"></div></div>
        <div id="pullStatus" class="kv-val"></div>
        <div style="margin-top:8px"><a href="https://ollama.com/library" target="_blank">Browse the model library ↗</a></div>
      </div>
      <div class="modal-footer"><button class="btn" id="cancelPull">Cancel</button><button class="btn primary" id="doPull">Pull</button></div>
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
  function onPullDone(msg) { if ($("#pullStatus")) { $("#pullStatus").textContent = "Done: " + msg.name; $("#doPull") && ($("#doPull").disabled = false); } toast("Pulled " + msg.name); refreshModels(); }
  function onPullCancelled(msg) { if ($("#pullStatus")) $("#pullStatus").textContent = "Cancelled"; if ($("#doPull")) $("#doPull").disabled = false; }

  function showShortcutsModal() {
    const rows = [
      ["New chat", "Ctrl+N"], ["Toggle sidebar", "Ctrl+B"], ["Open chat", "Ctrl+O"],
      ["Send message", "Enter"], ["Newline in composer", "Shift+Enter"], ["Stop generation", "Esc"],
      ["Zoom in / out / reset", "Ctrl+ +/−/0"], ["Reload", "Ctrl+R"], ["Dev tools", "F12"],
      ["Preferences", "Ctrl+,"], ["Copy code block", "Copy button on block"],
    ];
    modal(`<div class="modal-header">Keyboard Shortcuts <button class="icon-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">${rows.map(r => `<div class="shortcut-row"><span>${r[0]}</span><kbd>${r[1]}</kbd></div>`).join("")}</div>
      <div class="modal-footer"><button class="btn" id="ok">Close</button></div>`);
    $("#closeModal").onclick = closeModal; $("#ok").onclick = closeModal;
  }

  // ---- menu events from native menu bar ----
  function onMenuEvent(action) {
    switch (action) {
      case "newChat": newChat(); break;
      case "openChat": toast("Use the sidebar to open a chat"); break;
      case "exportChat": exportChat(); break;
      case "importChat": toast("Import via the chat file"); break;
      case "clearConversation": clearConversation(); break;
      case "deleteChat": if (state.currentId) deleteChat(state.currentId); break;
      case "toggleSidebar": $("#sidebar").classList.toggle("collapsed"); break;
      case "toggleTheme": toggleTheme(); break;
      case "pullModel": showPullModal(); break;
      case "deleteModel": showModelsModal(); break;
      case "manageModels": showModelsModal(); break;
      case "preferences": showSettingsModal("general"); break;
      case "serverSettings": showSettingsModal("server"); break;
      case "shortcuts": showShortcutsModal(); break;
      case "checkUpdates": toast("You're on the latest version"); break;
      case "editUndo": document.execCommand("undo"); break;
      case "editRedo": document.execCommand("redo"); break;
      case "editCut": document.execCommand("cut"); break;
      case "editCopy": document.execCommand("copy"); break;
      case "editPaste": document.execCommand("paste"); break;
      case "editSelectAll": document.execCommand("selectAll"); break;
    }
  }

  async function newChat() {
    const chat = await call("newChat", { model: state.currentModel });
    state.chats.unshift(chat);
    openChat(chat.id);
    $("#emptyState").classList.remove("hidden");
    $("#promptInput").focus();
  }

  async function exportChat() {
    if (!state.currentId) return;
    const json = await call("exportChat", { id: state.currentId });
    const blob = new Blob([json], { type: "application/json" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob); a.download = "ollama-chat.json"; a.click();
  }

  function clearConversation() {
    const c = state.chats.find(x => x.id === state.currentId);
    if (c) { c.messages = []; renderMessages(c); $("#emptyState").classList.remove("hidden"); }
  }

  function toggleTheme() {
    const next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", next);
    state.config.theme = next;
    call("saveConfig", { config: state.config });
  }

  function updateWebSearchToggle() {
    const on = $("#webSearchCheckbox").checked;
    $("#webSearchToggle").classList.toggle("active", on);
  }

  // ---- UI binding ----
  function bindUI() {
    $("#newChatBtn").addEventListener("click", newChat);
    $("#searchInput").addEventListener("input", renderChatList);
    $("#sidebarToggle").addEventListener("click", () => $("#sidebar").classList.toggle("collapsed"));
    $("#settingsBtn").addEventListener("click", () => showSettingsModal("general"));
    $("#manageModelsBtn").addEventListener("click", showModelsModal);
    $("#modelBtn").addEventListener("click", (e) => { e.stopPropagation(); $("#modelMenu").classList.toggle("hidden"); });
    document.addEventListener("click", (e) => { if (!e.target.closest(".model-picker")) closeModelMenu(); });
    $("#webSearchToggle").addEventListener("click", () => { $("#webSearchCheckbox").checked = !$("#webSearchCheckbox").checked; updateWebSearchToggle(); });
    $("#webSearchCheckbox").addEventListener("change", updateWebSearchToggle);
    $("#menuBtn").addEventListener("click", () => showShortcutsModal());
    $("#sendBtn").addEventListener("click", send);
    $("#stopBtn").addEventListener("click", stopGeneration);
    const input = $("#promptInput");
    input.addEventListener("input", autoGrow);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
    });
    $$(".suggestion").forEach(s => s.addEventListener("click", () => {
      input.value = s.dataset.prompt; autoGrow(); input.focus();
      if (s.dataset.prompt.toLowerCase().includes("latest")) $("#webSearchCheckbox").checked = true;
      updateWebSearchToggle();
    }));
  }

  function autoGrow() {
    const t = $("#promptInput"); t.style.height = "auto"; t.style.height = Math.min(t.scrollHeight, 200) + "px";
  }

  function bindKeyboard() {
    document.addEventListener("keydown", (e) => {
      const ctrl = e.ctrlKey || e.metaKey;
      if (ctrl && e.key.toLowerCase() === "n") { e.preventDefault(); newChat(); }
      else if (ctrl && e.key.toLowerCase() === "b") { e.preventDefault(); $("#sidebar").classList.toggle("collapsed"); }
      else if (ctrl && e.key === ",") { e.preventDefault(); showSettingsModal("general"); }
      else if (e.key === "Escape" && state.streaming) { stopGeneration(); }
    });
  }

  // ---- helpers ----
  function toast(msg) {
    let t = $("#toast");
    if (!t) { t = document.createElement("div"); t.id = "toast"; t.style.cssText = "position:fixed;bottom:24px;left:50%;transform:translateX(-50%);background:var(--surface-2);color:var(--text);padding:10px 16px;border-radius:8px;border:1px solid var(--border);font-size:13px;z-index:200;box-shadow:0 8px 24px rgba(0,0,0,.4)"; document.body.appendChild(t); }
    t.textContent = msg; t.style.opacity = "1"; t.style.display = "block";
    clearTimeout(t._h); t._h = setTimeout(() => { t.style.opacity = "0"; setTimeout(() => t.style.display = "none", 200); }, 3000);
  }

  window.OllamaUI = { copyCode: (btn) => {
    const code = btn.closest("pre").querySelector("code").innerText;
    navigator.clipboard.writeText(code).then(() => { btn.textContent = "Copied"; setTimeout(() => btn.textContent = "Copy", 1200); });
  } };

  document.addEventListener("DOMContentLoaded", init);
})();
