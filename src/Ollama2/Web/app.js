(function () {
  const $ = (s) => document.querySelector(s);
  const $$ = (s) => Array.from(document.querySelectorAll(s));

  const state = {
    config: null,
    chats: [],
    currentId: null,
    draftChat: null,
    models: [],
    currentModel: "",
    streaming: false,
    pendingAssistantEl: null,
    pendingAssistantText: "",
    view: "chat",
    appVersion: "",
    hardware: null,
    modelVisionCache: {},   // modelName -> bool (supports vision)
    modelVisionLoading: {}, // modelName -> bool (currently checking)
    pendingImages: [],      // [{ dataUrl, name }] — base64 data URLs to send
  };

  let _rpcCounter = 0;
  const pending = new Map();
  function call(action, payload = {}) {
    const id = "rpc" + (++_rpcCounter);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage(JSON.stringify({ _rpcId: id, action, payload }));
    });
  }
  function emit(action, payload = {}) {
    window.chrome.webview.postMessage(JSON.stringify({ action, ...payload }));
  }

  window.chrome.webview.addEventListener("message", (e) => {
    const msg = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
    const rpcKey = msg._rpcId || msg.id;
    if (rpcKey && pending.has(rpcKey)) {
      const p = pending.get(rpcKey); pending.delete(rpcKey);
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
      case "updateStatus": onUpdateStatus(msg); break;
      case "updateReady": onUpdateReady(msg); break;
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
    state.appVersion = data.appVersion || "";
    state.hardware = data.hardware || null;
    if (state.config) {
      state.currentModel = state.config.defaultModel;
      $("#composerModelLabel").textContent = state.currentModel || "Select";
      updateComposerModel();
      if (!state.config.sidebarVisible) { $("#sidebar").classList.add("collapsed"); $("#topbarNewChat").classList.remove("hidden"); }
      if (state.config.webSearchEnabled) { $("#webSearchToggle").classList.add("active"); }
      const spn2 = $("#sidebarProfileName"); if (spn2) spn2.textContent = state.config.defaultModel || "User";
      const spa2 = $("#sidebarProfileAvatar"); if (spa2) spa2.textContent = (state.config.defaultModel || "U").charAt(0).toUpperCase();
      updateAttachButtonVisibility();
    }
    renderChatList();
    if (state.chats.length && !state.currentId) openChat(state.chats[0].id);
    if (!data.serverReachable) toast("Server not reachable. Is `ollama serve` running?");
  }

  // ---- View routing ----
  function showView(name) {
    state.view = name;
    $("#viewChat").classList.toggle("active", name === "chat");
    $("#viewLaunch").classList.toggle("active", name === "launch");
    $("#viewSettings").classList.toggle("active", name === "settings");
    if (name === "chat") $("#promptInput").focus();
  }

  // ---- chat list ----
  function renderChatList() {
    const list = $("#chatList");
    list.innerHTML = "";
    if (state.chats.length === 0) return;
    const label = document.createElement("div");
    label.className = "chat-list-label";
    label.textContent = "Chats";
    list.appendChild(label);
    state.chats.forEach(c => {
      const el = document.createElement("div");
      el.className = "chat-item";
      el.textContent = c.title;
      el.addEventListener("click", () => openChat(c.id));
      el.addEventListener("contextmenu", (ev) => {
        ev.preventDefault();
        showContextMenu(ev.clientX, ev.clientY, c, el);
      });
      list.appendChild(el);
    });
  }

  let _ctxMenu = null;
  function showContextMenu(x, y, c, el) {
    if (_ctxMenu) { _ctxMenu.remove(); _ctxMenu = null; }

    const menu = document.createElement("div");
    menu.className = "context-menu";
    menu.innerHTML = `
      <div class="context-menu-item" data-action="rename">
        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/></svg>
        <span>Rename</span>
      </div>
      <div class="context-menu-item danger" data-action="delete">
        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6"/><path d="M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"/></svg>
        <span>Delete</span>
      </div>
    `;

    menu.querySelector('[data-action="rename"]').addEventListener("click", () => {
      menu.remove(); _ctxMenu = null;
      startRename(el, c);
    });
    menu.querySelector('[data-action="delete"]').addEventListener("click", () => {
      menu.remove(); _ctxMenu = null;
      deleteChat(c.id);
    });

    document.body.appendChild(menu);
    _ctxMenu = menu;

    // Position, keep inside viewport
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const mr = menu.getBoundingClientRect();
    menu.style.left = Math.min(x, vw - mr.width - 8) + "px";
    menu.style.top = Math.min(y, vh - mr.height - 8) + "px";

    // Close on next click anywhere
    const closeOnClick = (ev) => {
      if (!menu.contains(ev.target)) { menu.remove(); _ctxMenu = null; document.removeEventListener("click", closeOnClick); }
    };
    // Delay so current click doesn't immediately close it
    setTimeout(() => document.addEventListener("click", closeOnClick), 0);
  }

  function startRename(el, c) {
    const input = document.createElement("input");
    input.type = "text";
    input.className = "ci-rename-input";
    input.value = c.title;
    input.style.width = el.offsetWidth + "px";
    el.innerHTML = "";
    el.appendChild(input);
    input.focus();
    input.select();

    const save = async () => {
      const newTitle = input.value.trim();
      if (newTitle && newTitle !== c.title) {
        c.title = newTitle;
        await call("renameChat", { id: c.id, title: newTitle });
      }
      renderChatList();
    };

    input.addEventListener("blur", save);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); input.blur(); }
      else if (e.key === "Escape") { renderChatList(); }
    });
  }

  async function openChat(id) {
    showView("chat");
    state.currentId = id;
    const c = state.chats.find(x => x.id === id) || (state.draftChat && state.draftChat.id === id ? state.draftChat : null);
    if (!c) return;
    state.currentModel = c.model || state.currentModel;
    $("#composerModelLabel").textContent = state.currentModel || "Select";
    updateComposerModel();
    renderChatList();
    renderMessages(c);
    const empty = c.messages.length === 0;
    $("#emptyState").classList.toggle("hidden", !empty);
    $("#viewChat").classList.toggle("chat-empty", empty);
  }

  function renderMessages(c) {
    const m = $("#messages");
    m.innerHTML = "";
    c.messages.forEach(msg => appendMessageEl(msg.role, msg.content, { sources: msg.sources, error: msg.error, evalCount: msg.evalCount, totalMs: msg.totalMs, images: msg.images }));
  }

  function appendMessageEl(role, content, opts = {}) {
    const m = $("#messages");
    const wrap = document.createElement("div");
    wrap.className = "msg " + role;
    // Build images HTML for user messages with images
    let imagesHtml = "";
    if (role === "user" && opts.images && opts.images.length) {
      imagesHtml = `<div class="msg-images">${opts.images.map(url => `<img src="${url}" alt="attachment">`).join("")}</div>`;
    }
    const bodyHtml = opts.error
      ? `<div class="msg-error">${OllamaMD.escape(opts.error)}</div>`
      : (role === "user" ? imagesHtml + OllamaMD.escape(content).replace(/\n/g, "<br>") : OllamaMD.render(content));
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
  const EFFORT_LEVELS = [
    { key: "low",    label: "Low",    desc: "Faster responses, less thorough" },
    { key: "medium", label: "Medium", desc: "Balanced speed and quality" },
    { key: "high",   label: "High",   desc: "More thorough, takes longer" },
    { key: "max",    label: "Max",    desc: "Maximum depth, slowest" },
  ];

  function renderModelMenu() {
    const menu = $("#modelMenu");
    menu.innerHTML = "";
    if (!state.models.length) {
      const empty = document.createElement("div");
      empty.className = "model-item";
      empty.innerHTML = `<div class="model-item-left"><div class="mi-name">No models installed</div><div class="mi-desc">Pull a model to get started</div></div>`;
      menu.appendChild(empty);
    }
    state.models.forEach(mo => {
      const info = getModelInfo(mo.name);
      const it = document.createElement("div");
      it.className = "model-item" + (mo.name === state.currentModel ? " selected" : "");
      const isSelected = mo.name === state.currentModel;
      it.innerHTML = `
        <div class="model-item-left">
          <div class="mi-name">${OllamaMD.escape(mo.name)}</div>
          <div class="mi-desc">${OllamaMD.escape(info.desc)}</div>
          <div class="mi-meta">${formatSize(mo.size)}${info.context ? ` · ${info.context} context` : ""}${info.tags.length ? ` · ${info.tags.join(", ")}` : ""}</div>
        </div>
        <div class="mi-check${isSelected ? "" : " hidden"}">
          <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>
        </div>
      `;
      it.addEventListener("click", () => { selectModel(mo.name); closeModelMenu(); });
      menu.appendChild(it);
    });

    // ---- Effort submenu row ----
    const sep1 = document.createElement("div"); sep1.className = "mm-sep"; menu.appendChild(sep1);
    const effortRow = document.createElement("div"); effortRow.className = "mm-submenu-row";
    const effLabel = (EFFORT_LEVELS.find(e => e.key === (state.config?.effort || "medium"))?.label) || "Medium";
    effortRow.innerHTML = `
      <div class="mm-submenu-label">Effort</div>
      <div class="mm-submenu-val">${OllamaMD.escape(effLabel)} <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18 6-6-6-6"/></svg></div>
    `;
    effortRow.addEventListener("click", (e) => {
      e.stopPropagation();
      showEffortSubmenu();
    });
    menu.appendChild(effortRow);

    // ---- Actions ----
    const sep2 = document.createElement("div"); sep2.className = "mm-sep"; menu.appendChild(sep2);
    const pull = document.createElement("div"); pull.className = "mm-action";
    pull.innerHTML = `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg><span>Pull a model…</span>`;
    pull.addEventListener("click", () => { closeModelMenu(); showPullModal(); });
    menu.appendChild(pull);
    const manage = document.createElement("div"); manage.className = "mm-action";
    manage.innerHTML = `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.72l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.72l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.72V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.72l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.38a2 2 0 0 0-.73-2.73l-.15-.1a2 2 0 0 1-1-1.72v-.51a2 2 0 0 1 1-1.72l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.72V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/></svg><span>Manage models</span>`;
    manage.addEventListener("click", () => { closeModelMenu(); showManageModelsModal(); });
    menu.appendChild(manage);

    positionMenu();
  }

  function showEffortSubmenu() {
    const em = $("#effortMenu");
    em.innerHTML = "";
    em.classList.add("open");

    // Description
    const desc = document.createElement("div"); desc.className = "mm-submenu-desc";
    desc.textContent = "Higher effort means more thorough responses, but takes longer and uses your limits faster.";
    em.appendChild(desc);

    // Effort levels
    const currentEffort = state.config?.effort || "medium";
    EFFORT_LEVELS.forEach(lvl => {
      const row = document.createElement("div");
      row.className = "mm-effort-row" + (lvl.key === currentEffort ? " selected" : "");
      row.dataset.effort = lvl.key;
      row.innerHTML = `
        <div class="mm-effort-name">${OllamaMD.escape(lvl.label)}${lvl.key === "medium" ? ' <span class="mm-effort-default">Default</span>' : ""}</div>
        <div class="mm-effort-desc">${OllamaMD.escape(lvl.desc)}</div>
      `;
      row.addEventListener("click", async (e) => {
        e.stopPropagation();
        if (state.config) {
          state.config.effort = lvl.key;
          await call("saveConfig", { config: state.config });
        }
        showEffortSubmenu();
        renderModelMenu();
      });
      em.appendChild(row);
    });

    // Thinking toggle
    const sep = document.createElement("div"); sep.className = "mm-sep"; em.appendChild(sep);
    const thinkingOn = state.config?.thinkingEnabled === true;
    const thinkingRow = document.createElement("div");
    thinkingRow.className = "mm-thinking-row";
    thinkingRow.innerHTML = `
      <div class="mm-thinking-left">
        <div class="mm-thinking-title">Thinking</div>
        <div class="mm-thinking-desc">Can think for more complex tasks</div>
      </div>
      <div class="toggle ${thinkingOn ? "on" : ""}" id="modelMenuThinking"></div>
    `;
    thinkingRow.addEventListener("click", async (e) => {
      e.stopPropagation();
      if (!state.config) return;
      const t = thinkingRow.querySelector("#modelMenuThinking");
      const on = !t.classList.contains("on");
      t.classList.toggle("on", on);
      state.config.thinkingEnabled = on;
      await call("saveConfig", { config: state.config });
    });
    em.appendChild(thinkingRow);

    positionMenu();
  }

  function hideEffortMenu() {
    $("#effortMenu")?.classList.remove("open");
  }

  function positionMenu() {
    const picker = $("#composerModelPicker");
    const menu = $("#modelMenu");
    const em = $("#effortMenu");
    if (!picker || !menu || !menu.classList.contains("open")) return;
    const rect = picker.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    const menuW = 280;
    const effortW = 260;
    const gap = 8;
    const pad = 16;
    const bothW = menuW + effortW + gap;

    // ---- Vertical positioning ----
    const spaceAbove = rect.top - pad;
    const spaceBelow = vh - rect.bottom - pad;
    const preferredH = 380;
    let menuH, openAbove;
    if (spaceAbove >= preferredH) {
      menuH = preferredH; openAbove = true;
    } else if (spaceBelow >= preferredH) {
      menuH = preferredH; openAbove = false;
    } else if (spaceAbove >= spaceBelow) {
      menuH = Math.max(200, spaceAbove); openAbove = true;
    } else {
      menuH = Math.max(200, spaceBelow); openAbove = false;
    }
    menu.style.maxHeight = menuH + "px";
    if (em && em.classList.contains("open")) em.style.maxHeight = menuH + "px";

    const menuTop = openAbove ? (rect.top - menuH - gap) : (rect.bottom + gap);
    menu.style.top = menuTop + "px";
    if (em && em.classList.contains("open")) em.style.top = menuTop + "px";

    // ---- Horizontal positioning ----
    // Model menu: try left-aligned, fallback to right-aligned
    let menuLeft = rect.left;
    if (menuLeft + menuW > vw - pad) {
      menuLeft = Math.max(pad, rect.right - menuW);
    }
    menu.style.left = menuLeft + "px";
    menu.style.bottom = "auto";

    // Effort menu: try right of model menu, fallback to left
    if (em && em.classList.contains("open")) {
      let effortLeft = menuLeft + menuW + gap;
      let effortRight = menuLeft - effortW - gap;
      if (effortLeft + effortW <= vw - pad) {
        em.style.left = effortLeft + "px";
        em.style.right = "auto";
      } else if (effortRight >= pad) {
        em.style.left = effortRight + "px";
        em.style.right = "auto";
      } else {
        // Not enough room either side — shrink and place wherever there's more room
        const roomRight = vw - menuLeft - menuW - gap - pad;
        const roomLeft = menuLeft - gap - pad;
        if (roomRight >= roomLeft) {
          em.style.left = (menuLeft + menuW + gap) + "px";
          em.style.maxWidth = Math.max(180, roomRight) + "px";
        } else {
          em.style.left = Math.max(pad, menuLeft - gap - effortW) + "px";
          em.style.maxWidth = Math.max(180, roomLeft) + "px";
        }
      }
      em.style.bottom = "auto";
    }
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
    $("#composerModelLabel").textContent = name;
    updateComposerModel();
    if (state.config) { state.config.defaultModel = name; call("saveConfig", { config: state.config }); }
    if (state.currentId) { const c = state.chats.find(x => x.id === state.currentId); if (c) c.model = name; }
    renderModelMenu();
    updateAttachButtonVisibility();
    // Clear pending images if switching to a non-vision model
    checkModelVision(name).then(supports => {
      if (!supports && state.pendingImages.length) {
        state.pendingImages = [];
        renderPendingImages();
      }
    });
  }

  function closeModelMenu() { $("#modelMenu")?.classList.remove("open"); hideEffortMenu(); }

  // ---- File / image handling ----
  const MAX_IMAGE_SIZE = 20 * 1024 * 1024; // 20 MB

  async function handleFiles(fileList) {
    // Only allow images, and only if the current model supports vision
    const supportsVision = await checkModelVision(state.currentModel);
    if (!supportsVision) {
      toast("This model doesn't support images. Select a vision model (e.g. llava).");
      return;
    }
    const files = Array.from(fileList).filter(f => f.type.startsWith("image/"));
    if (!files.length) {
      toast("Only image files are supported.");
      return;
    }
    for (const f of files) {
      if (f.size > MAX_IMAGE_SIZE) {
        toast(`"${f.name}" is too large (max 20 MB).`);
        continue;
      }
      const dataUrl = await readFileAsDataURL(f);
      state.pendingImages.push({ dataUrl, name: f.name });
    }
    renderPendingImages();
  }

  function readFileAsDataURL(file) {
    return new Promise((resolve, reject) => {
      const r = new FileReader();
      r.onload = () => resolve(r.result);
      r.onerror = reject;
      r.readAsDataURL(file);
    });
  }

  function renderPendingImages() {
    const container = $("#pendingImages");
    if (!container) return;
    container.innerHTML = "";
    state.pendingImages.forEach((img, idx) => {
      const el = document.createElement("div");
      el.className = "pending-img";
      el.innerHTML = `<img src="${img.dataUrl}" alt="${OllamaMD.escape(img.name)}"><button class="pending-img-remove" title="Remove">&times;</button>`;
      el.querySelector(".pending-img-remove").addEventListener("click", () => {
        state.pendingImages.splice(idx, 1);
        renderPendingImages();
      });
      container.appendChild(el);
    });
  }

  function clearPendingImages() {
    state.pendingImages = [];
    renderPendingImages();
  }

  // Extract base64 (without data: prefix) from a data URL
  function dataUrlToBase64(dataUrl) {
    const idx = dataUrl.indexOf(",");
    return idx >= 0 ? dataUrl.substring(idx + 1) : dataUrl;
  }

  // ---- send / stream ----
  async function send() {
    const input = $("#promptInput");
    const text = input.value.trim();
    const images = [...state.pendingImages];
    if ((!text && !images.length) || state.streaming) return;
    if (!state.currentModel) { toast("Select a model first"); return; }

    let chat = state.chats.find(c => c.id === state.currentId);

    // If this is a draft chat, persist it first
    if (!chat && state.draftChat && state.draftChat.id === state.currentId) {
      const persisted = await call("newChat", { model: state.currentModel });
      persisted.messages = [...state.draftChat.messages];
      state.chats.unshift(persisted);
      state.currentId = persisted.id;
      chat = persisted;
      state.draftChat = null;
      renderChatList();
    }

    if (!chat) return;

    const webSearch = $("#webSearchToggle").classList.contains("active");
    const imageBase64 = images.map(img => dataUrlToBase64(img.dataUrl));
    const imageDataUrls = images.map(img => img.dataUrl);
    chat.messages.push({ role: "user", content: text, images: imageDataUrls });
    appendMessageEl("user", text, { images: imageDataUrls });
    $("#emptyState").classList.add("hidden");
    $("#viewChat").classList.remove("chat-empty");
    input.value = ""; autoGrow();
    clearPendingImages();

    const el = appendMessageEl("assistant", "");
    el.querySelector(".msg-body").innerHTML = '<span class="cursor"></span>';
    state.pendingAssistantEl = el;
    state.pendingAssistantText = "";
    state.streaming = true;
    setStreamingUI(true);

    // Build history — include images on the latest user message
    const history = chat.messages
      .filter(m => m.role === "user" || (m.role === "assistant" && m.content))
      .map((m, i) => {
        const msg = { role: m.role, content: m.content };
        // Attach images to the last user message
        if (m.role === "user" && m.images && m.images.length && i === chat.messages.length - 1) {
          msg.images = m.images.map(dataUrl => dataUrlToBase64(dataUrl));
        }
        return msg;
      });

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
      if (c.title === "New Chat") {
        const firstUser = c.messages.find(m => m.role === "user");
        c.title = generateTitle(firstUser?.content || text);
        call("renameChat", { id: c.id, title: c.title }).catch(() => {});
        renderChatList();
      }
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

  async function regenerate() {
    if (state.streaming) return;
    let c = state.chats.find(x => x.id === state.currentId);
    // If current chat is a draft, persist it first
    if (!c && state.draftChat && state.draftChat.id === state.currentId) {
      const persisted = await call("newChat", { model: state.currentModel });
      persisted.messages = [...state.draftChat.messages];
      state.chats.unshift(persisted);
      state.currentId = persisted.id;
      c = persisted;
      state.draftChat = null;
      renderChatList();
    }
    if (!c) return;
    while (c.messages.length && c.messages[c.messages.length - 1].role === "assistant") c.messages.pop();
    renderMessages(c);
    const history = c.messages.map(m => {
      const msg = { role: m.role, content: m.content };
      if (m.role === "user" && m.images && m.images.length) {
        msg.images = m.images.map(dataUrl => dataUrlToBase64(dataUrl));
      }
      return msg;
    });
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

  function generateTitle(firstUserMessage) {
    if (!firstUserMessage) return "New Chat";
    // Strip markdown and clean up
    let t = firstUserMessage.replace(/[#*`>_~\[\]\(\)\|]/g, "").trim();
    // Remove common question/request prefixes
    t = t.replace(/^(please\s+|can\s+you\s+|could\s+you\s+|how\s+(do|can|should)\s+i\s+|what\s+(is|are|does)\s+|explain\s+|tell\s+me\s+about\s+|write\s+|create\s+|generate\s+|make\s+|describe\s+|help\s+me\s+|show\s+me\s+how\s+to\s+)/i, "");
    t = t.replace(/^(the\s+|a\s+|an\s+)/i, "");
    t = t.trim();
    if (!t) return "New Chat";
    // Limit to ~6 words
    const words = t.split(/\s+/);
    if (words.length > 7) return words.slice(0, 6).join(" ") + "…";
    // Capitalize first letter
    return t.charAt(0).toUpperCase() + t.slice(1);
  }

  async function deleteChat(id) {
    // Check if it's a draft
    if (state.draftChat && state.draftChat.id === id) {
      state.draftChat = null;
      if (state.currentId === id) {
        state.currentId = null;
        $("#messages").innerHTML = "";
        $("#emptyState").classList.remove("hidden");
        $("#viewChat").classList.add("chat-empty");
      }
      renderChatList();
      return;
    }
    await call("deleteChat", { id });
    state.chats = state.chats.filter(c => c.id !== id);
    if (state.currentId === id) {
      state.currentId = null;
      $("#messages").innerHTML = "";
      $("#emptyState").classList.remove("hidden");
      $("#viewChat").classList.add("chat-empty");
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
    const cfg = state.config || {};
    $("#profileName").textContent = cfg.defaultModel || "User";
    $("#profileAvatar").textContent = (cfg.defaultModel || "U").charAt(0).toUpperCase();
    const spn = $("#sidebarProfileName"); if (spn) spn.textContent = cfg.defaultModel || "User";
    const spa = $("#sidebarProfileAvatar"); if (spa) spa.textContent = (cfg.defaultModel || "U").charAt(0).toUpperCase();
    $("#appVersion").textContent = "v" + state.appVersion;

    // Cloud toggle
    const cloudToggle = $("#toggleCloud");
    cloudToggle.classList.toggle("on", cfg.webSearchEnabled !== false);
    cloudToggle.onclick = () => {
      const on = cloudToggle.classList.toggle("on");
      if (state.config) { state.config.webSearchEnabled = on; call("saveConfig", { config: state.config }); }
    };

    // Auto-download toggle
    const autoToggle = $("#toggleAutoUpdate");
    autoToggle.classList.toggle("on", cfg.checkUpdatesOnStartup !== false);
    autoToggle.onclick = () => {
      const on = autoToggle.classList.toggle("on");
      if (state.config) { state.config.checkUpdatesOnStartup = on; call("saveConfig", { config: state.config }); }
      toast(on ? "Updates will be checked on startup" : "Auto-update disabled");
    };

    // Check now button
    const checkBtn = $("#checkNowBtn");
    if (checkBtn) {
      checkBtn.onclick = async () => {
        checkBtn.textContent = "Checking...";
        try {
          const release = await call("checkForUpdates");
          if (!release) { toast("You are on the latest version."); }
          else {
            toast(`Update available: ${release.tag_name}. Downloading...`);
            const path = await call("downloadUpdate", { release });
            if (path) {
              if (confirm(`Update downloaded. Install ${release.tag_name} now? The app will restart.`)) {
                await call("installUpdate", { path });
              }
            }
          }
        } catch (e) { toast("Update check failed: " + e.message); }
        finally { checkBtn.textContent = "Check now"; }
      };
    }

    // Expose to network toggle
    const netToggle = $("#toggleNetwork");
    netToggle.classList.toggle("on", cfg.exposeToNetwork === true);
    netToggle.onclick = () => {
      const on = netToggle.classList.toggle("on");
      if (state.config) { state.config.exposeToNetwork = on; call("saveConfig", { config: state.config }); }
      if (on) toast("To apply: set OLLAMA_HOST=0.0.0.0 and restart ollama serve");
      else toast("Network exposure disabled");
    };

    // Model location
    $("#modelPathInput").value = cfg.modelPath || "C:\\Users\\" + (navigator.userAgent.match(/Win/) ? "user" : "user") + "\\.ollama\\models";
    $("#browseModelPath").onclick = async () => {
      const path = await call("browseFolder");
      if (path) {
        $("#modelPathInput").value = path;
        if (state.config) { state.config.modelPath = path; call("saveConfig", { config: state.config }); }
        toast("To apply: set OLLAMA_MODELS env var and restart ollama serve");
      }
    };

    // Context length slider
    const ctxValues = [4096, 8192, 16384, 32768, 65536, 131072, 262144];
    const ctxLabels = ["4k", "8k", "16k", "32k", "64k", "128k", "256k"];
    const currentCtx = cfg.numCtx || 4096;
    let ctxIdx = ctxValues.indexOf(currentCtx);
    if (ctxIdx < 0) ctxIdx = 6;

    const wrap = $("#contextSliderWrap");
    const fill = $("#contextSliderFill");
    const thumb = $("#contextSliderThumb");
    let dragging = false;

    function setSlider(idx) {
      const pct = (idx / (ctxLabels.length - 1)) * 100;
      fill.style.width = pct + "%";
      thumb.style.left = pct + "%";
      $("#contextValue").textContent = ctxLabels[idx];
    }
    setSlider(ctxIdx);

    function snapToIndex(clientX) {
      const rect = wrap.getBoundingClientRect();
      const x = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
      const idx = Math.round(x * (ctxLabels.length - 1));
      return Math.max(0, Math.min(ctxLabels.length - 1, idx));
    }

    thumb.addEventListener("mousedown", (e) => { dragging = true; e.preventDefault(); });
    wrap.addEventListener("mousedown", (e) => {
      const idx = snapToIndex(e.clientX);
      ctxIdx = idx;
      setSlider(idx);
      if (state.config) { state.config.numCtx = ctxValues[idx]; call("saveConfig", { config: state.config }); }
    });
    document.addEventListener("mousemove", (e) => {
      if (!dragging) return;
      const idx = snapToIndex(e.clientX);
      ctxIdx = idx;
      setSlider(idx);
    });
    document.addEventListener("mouseup", () => {
      if (dragging && state.config) {
        state.config.numCtx = ctxValues[ctxIdx];
        call("saveConfig", { config: state.config });
      }
      dragging = false;
    });

    // Close behavior buttons
    const cb = cfg.closeBehavior || "tray";
    $$("#closeBehaviorOptions .pill-btn").forEach(btn => btn.classList.remove("selected"));
    const activeBtn = $(`#closeBehaviorOptions [data-val="${cb}"]`);
    if (activeBtn) activeBtn.classList.add("selected");
    $$("#closeBehaviorOptions .pill-btn").forEach(btn => {
      btn.onclick = () => {
        $$("#closeBehaviorOptions .pill-btn").forEach(b => b.classList.remove("selected"));
        btn.classList.add("selected");
        const val = btn.dataset.val;
        if (state.config) { state.config.closeBehavior = val; call("saveConfig", { config: state.config }); }
        toast(val === "tray" ? "App will keep running when closed" : "App will quit when closed");
      };
    });

    // Effort buttons
    const eff = cfg.effort || "medium";
    $$("#effortOptions .pill-btn").forEach(btn => btn.classList.remove("selected"));
    const effBtn = $(`#effortOptions [data-val="${eff}"]`);
    if (effBtn) effBtn.classList.add("selected");
    $$("#effortOptions .pill-btn").forEach(btn => {
      btn.onclick = () => {
        $$("#effortOptions .pill-btn").forEach(b => b.classList.remove("selected"));
        btn.classList.add("selected");
        const val = btn.dataset.val;
        if (state.config) { state.config.effort = val; call("saveConfig", { config: state.config }); }
        toast(`Effort set to ${val}`);
      };
    });

    // Thinking toggle
    const thinkToggle = $("#toggleThinking");
    thinkToggle.classList.toggle("on", cfg.thinkingEnabled === true);
    thinkToggle.onclick = () => {
      const on = thinkToggle.classList.toggle("on");
      if (state.config) { state.config.thinkingEnabled = on; call("saveConfig", { config: state.config }); }
      toast(on ? "Thinking enabled" : "Thinking disabled");
    };

    // Reset to defaults
    $("#resetDefaults").onclick = () => {
      if (!confirm("Reset all settings to defaults?")) return;
      const defaults = {
        serverUrl: "http://localhost:11434", defaultModel: "llama3.2",
        webSearchEnabled: true, webSearchProvider: "duckduckgo",
        webSearchApiKey: "", theme: "dark", sidebarVisible: true, zoom: 1,
        temperature: 0.8, topK: 40, topP: 0.9, numCtx: 4096,
        systemPrompt: "", maxSearchResults: 5, closeBehavior: "tray",
        checkUpdatesOnStartup: true, exposeToNetwork: false, modelPath: "", stream: true,
        effort: "medium", thinkingEnabled: false,
      };
      state.config = { ...state.config, ...defaults };
      call("saveConfig", { config: state.config });
      initSettings(); // re-render
      toast("Settings reset to defaults");
    };
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

  const MODEL_LIBRARY = {
    "llama3.2:1b":    { desc: "Tiny & fast, ideal for quick chat and edge devices",                 size: "1.3 GB", power: 18,  params: "1.2B", context: "128k", tags: ["Fast"] },
    "phi3":           { desc: "Microsoft's compact model with surprisingly strong reasoning",       size: "2.3 GB", power: 30,  params: "3.8B", context: "128k", tags: ["Reasoning"] },
    "gemma2:2b":      { desc: "Google's ultra-compact model, great for low-latency tasks",           size: "1.6 GB", power: 28,  params: "2.6B", context: "8k",   tags: ["Fast"] },
    "qwen2.5:0.5b":   { desc: "Alibaba's ultra-small multilingual model for basic tasks",           size: "0.4 GB", power: 12,  params: "0.5B", context: "32k",  tags: ["Fast","Multilingual"] },
    "llama3.2":       { desc: "Meta's lightweight 3B model — excellent speed-to-quality ratio",       size: "2.0 GB", power: 32,  params: "3.2B", context: "128k", tags: ["Fast"] },
    "qwen2.5":        { desc: "Alibaba's capable 7B model with strong multilingual support",         size: "4.7 GB", power: 48,  params: "7.6B", context: "128k", tags: ["Multilingual"] },
    "mistral":        { desc: "Mistral AI's efficient 7B, top-tier performance for its size",       size: "4.1 GB", power: 50,  params: "7.3B", context: "128k", tags: ["Efficient"] },
    "deepseek-r1":    { desc: "Reasoning-specialist model that thinks step-by-step",                size: "4.7 GB", power: 55,  params: "7B",   context: "64k",  tags: ["Reasoning"] },
    "llama3.1":       { desc: "Meta's best 8B open model, strong across the board",                  size: "4.7 GB", power: 58,  params: "8B",   context: "128k", tags: ["Balanced"] },
    "qwen2.5:32b":    { desc: "Alibaba's 32B powerhouse, excellent reasoning and coding",             size: "18 GB", power: 78,  params: "32.5B",context: "128k", tags: ["Coding","Multilingual"] },
    "mixtral":        { desc: "Mixture-of-experts 47B — very capable, handles complex tasks",       size: "26 GB", power: 82,  params: "47B",  context: "32k",  tags: ["MoE","Efficient"] },
    "llama3.1:70b":   { desc: "Meta's 70B giant, near-frontier quality for local inference",          size: "40 GB", power: 90,  params: "70B",  context: "128k", tags: ["Powerful"] },
    "qwen2.5:72b":    { desc: "Alibaba's 72B flagship, among the strongest open models",             size: "45 GB", power: 92,  params: "72B",  context: "128k", tags: ["Powerful","Multilingual"] },
    "llama3.1:405b":  { desc: "Meta's 405B frontier-class model — maximum power, maximum size",      size: "230 GB",power: 100, params: "405B", context: "128k", tags: ["Frontier"] },
    "llava":          { desc: "Vision model that understands images alongside text",                  size: "4.7 GB", power: 35,  params: "7B",   context: "4k",   tags: ["Vision"] },
    "nomic-embed-text":{ desc: "Specialized embedding model for RAG and semantic search",            size: "0.3 GB", power: 8,   params: "0.1B", context: "8k",   tags: ["Embedding"] },
  };

  function getModelInfo(name) {
    const lower = name.toLowerCase();
    if (MODEL_LIBRARY[lower]) return MODEL_LIBRARY[lower];
    return { desc: "Model from the library", size: "", power: 45, params: "?", context: "?", tags: [] };
  }

  // ---- Vision model detection ----
  // Known vision model name patterns (fast check without API call)
  const VISION_NAME_PATTERNS = [
    "llava", "bakllava", "moondream", "llama3.2-vision", "llama3.2:11b-vision",
    "llama3.2:90b-vision", "minicpm-v", "qwen2.5-vl", "qwen2-vl", "internvl",
    "granite3.2-vision", "gemma3", "pixtral", "mistral-small3.1",
  ];

  function isVisionModelByName(name) {
    const lower = (name || "").toLowerCase();
    return VISION_NAME_PATTERNS.some(p => lower.includes(p));
  }

  async function checkModelVision(name) {
    if (!name) return false;
    if (state.modelVisionCache[name] !== undefined) return state.modelVisionCache[name];
    if (state.modelVisionLoading[name]) return false;
    state.modelVisionLoading[name] = true;
    try {
      // Fast path: check by name first
      if (isVisionModelByName(name)) {
        state.modelVisionCache[name] = true;
        return true;
      }
      // Query /api/show for capabilities
      const info = await call("showModel", { name });
      let supportsVision = false;
      // Check capabilities array (newer Ollama)
      if (info && info.capabilities) {
        const caps = Array.isArray(info.capabilities) ? info.capabilities : [];
        supportsVision = caps.some(c => String(c).toLowerCase() === "vision");
      }
      // Check projector_info presence (indicates vision projector)
      if (!supportsVision && info && info.projector_info) {
        supportsVision = true;
      }
      // Check model_info families for "clip"
      if (!supportsVision && info && info.model_info) {
        const mi = info.model_info;
        const fam = mi["general.family"] || mi["general.architecture"] || "";
        if (String(fam).toLowerCase().includes("clip")) supportsVision = true;
      }
      state.modelVisionCache[name] = supportsVision;
      return supportsVision;
    } catch {
      // If show fails, fall back to name-based check
      const fallback = isVisionModelByName(name);
      state.modelVisionCache[name] = fallback;
      return fallback;
    } finally {
      state.modelVisionLoading[name] = false;
    }
  }

  async function updateAttachButtonVisibility() {
    const btn = $("#attachBtn");
    if (!btn) return;
    const supports = await checkModelVision(state.currentModel);
    btn.style.display = supports ? "" : "none";
  }

  function getRecommendedModels() {
    const hw = state.hardware;
    const ram = hw?.ramGb || 8;
    const vram = hw?.gpuVramGb || 0;
    const hasGpu = vram > 0;
    const effective = hasGpu ? Math.max(ram * 0.5, vram) : ram * 0.7;

    if (effective < 4) {
      return ["llama3.2:1b", "phi3", "qwen2.5:0.5b"];
    } else if (effective < 8) {
      return ["llama3.2", "gemma2:2b", "qwen2.5"];
    } else if (effective < 16) {
      return ["llama3.1", "mistral", "deepseek-r1"];
    } else if (effective < 32) {
      return ["llama3.1:70b", "mixtral", "qwen2.5:32b"];
    } else {
      return ["llama3.1:405b", "qwen2.5:72b", "mixtral"];
    }
  }

  function buildModelCard(name) {
    const info = getModelInfo(name);
    const powerLabel = info.power < 25 ? "Light" : info.power < 50 ? "Balanced" : info.power < 75 ? "Strong" : "Powerhouse";
    const tagsHtml = info.tags.map(t => `<span class="rec-tag">${OllamaMD.escape(t)}</span>`).join("");
    return `
      <div class="rec-item" data-name="${OllamaMD.escape(name)}">
        <div class="rec-top">
          <div class="rec-name">${OllamaMD.escape(name)}</div>
          <div class="rec-meta"><span class="rec-power-label">${powerLabel}</span></div>
        </div>
        <div class="rec-desc">${OllamaMD.escape(info.desc)}</div>
        <div class="rec-specs">
          <span class="rec-spec"><strong>${OllamaMD.escape(info.params)}</strong> params</span>
          <span class="rec-spec"><strong>${OllamaMD.escape(info.context)}</strong> context</span>
          <span class="rec-spec"><strong>${OllamaMD.escape(info.size)}</strong> download</span>
        </div>
        ${tagsHtml ? `<div class="rec-tags">${tagsHtml}</div>` : ""}
        <div class="power-bar" title="Power rating: ${info.power}/100">
          <div class="power-bar-track"></div>
          <div class="power-bar-dot" style="left:${info.power}%"></div>
        </div>
      </div>`;
  }

  function showPullModal() {
    const popular = ["llama3.2", "llama3.2:1b", "qwen2.5", "phi3", "mistral", "gemma2", "deepseek-r1", "llava", "nomic-embed-text"];
    const hw = state.hardware;
    const hwText = hw
      ? `<div style="font-size:12px;color:var(--text-muted);margin-bottom:12px">Detected: ${hw.cpu} · ${hw.ramGb}GB RAM${hw.gpu ? ` · ${hw.gpu} (${hw.gpuVramGb}GB VRAM)` : ""}</div>`
      : "";
    const recNames = getRecommendedModels();
    const recHtml = recNames.map(buildModelCard).join("");
    const popHtml = popular.map(buildModelCard).join("");

    modal(`
      <div class="modal-header">Pull Model <button class="close-btn" id="closeModal">&times;</button></div>
      <div class="modal-body">
        ${hwText}
        <div class="field"><label>Model name</label><input type="text" id="pullName" placeholder="e.g. llama3.2" list="pullList" style="width:100%;padding:9px 12px;background:var(--bg);border:1px solid var(--border);border-radius:8px;color:var(--text);font-size:14px"/></div>
        <div class="pull-progress"><div class="bar" id="pullBar"></div></div>
        <div id="pullStatus" style="color:var(--text-muted);font-size:13px"></div>
        <div style="margin-top:12px"><label style="font-size:13px;color:var(--text-secondary)">Recommended for your hardware</label>
          <div class="rec-list">${recHtml}</div>
        </div>
        <div style="margin-top:12px"><label style="font-size:13px;color:var(--text-secondary)">Popular models</label>
          <div class="rec-list">${popHtml}</div>
        </div>
        <div style="margin-top:8px"><a href="https://ollama.com/library" target="_blank" style="color:var(--accent);font-size:13px">Browse the full library ↗</a></div>
      </div>
      <div class="modal-footer"><button class="pill-btn outline" id="cancelPull">Cancel</button><button class="pill-btn primary" id="doPull">Pull</button></div>
    `);
    $("#closeModal").onclick = closeModal; $("#cancelPull").onclick = closeModal;
    $("#doPull").onclick = () => {
      const name = $("#pullName").value.trim(); if (!name) return;
      $("#doPull").disabled = true; $("#pullStatus").textContent = "Starting…";
      emit("pullModel", { name });
    };
    $$(".rec-item").forEach(el => {
      el.addEventListener("click", () => {
        $("#pullName").value = el.dataset.name;
      });
    });
  }

  function onPullProgress(msg) {
    if (!$("#pullBar")) return;
    $("#pullBar").style.width = msg.percent + "%";
    $("#pullStatus").textContent = msg.status + (msg.total ? ` · ${msg.percent}%` : "");
  }
  function onPullDone(msg) { if ($("#pullStatus")) { $("#pullStatus").textContent = "Done: " + msg.name; if ($("#doPull")) $("#doPull").disabled = false; } toast("Pulled " + msg.name); refreshModels(); }
  function onPullCancelled(msg) { if ($("#pullStatus")) $("#pullStatus").textContent = "Cancelled"; if ($("#doPull")) $("#doPull").disabled = false; }

  function onUpdateStatus(msg) {
    if (msg.progress > 0 && msg.progress < 100) {
      toast(`Update: ${msg.status} (${Math.round(msg.progress)}%)`);
    } else {
      toast(msg.status);
    }
  }

  function onUpdateReady(msg) {
    showUpdateBanner(msg.version, msg.path);
  }

  function showUpdateBanner(version, path) {
    let banner = $("#updateBanner");
    if (!banner) {
      banner = document.createElement("div");
      banner.id = "updateBanner";
      banner.style.cssText = "position:fixed;top:12px;left:50%;transform:translateX(-50%);z-index:150;background:#27272A;border:1px solid #3F3F46;border-radius:10px;padding:10px 16px;display:flex;align-items:center;gap:12px;box-shadow:0 8px 24px rgba(0,0,0,0.4)";
      document.body.appendChild(banner);
    }
    banner.innerHTML = `
      <span style="font-size:13px;color:var(--text)">${OllamaMD.escape(version)} is ready to install</span>
      <button class="pill-btn primary" id="installUpdateBtn" style="font-size:12px;padding:5px 12px">Install now</button>
      <button id="dismissUpdateBtn" style="color:#71717A;font-size:18px;padding:2px 6px;background:none;border-radius:4px">&times;</button>
    `;
    banner.style.display = "flex";
    $("#installUpdateBtn").onclick = async () => {
      if (confirm(`Install ${OllamaMD.escape(version)} now? The app will restart.`)) {
        await call("installUpdate", { path });
      }
    };
    $("#dismissUpdateBtn").onclick = () => { banner.style.display = "none"; };
  }

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

    $("#sidebarToggle") && $("#sidebarToggle").addEventListener("click", () => {
      const sb = $("#sidebar");
      sb.classList.toggle("collapsed");
      const isCollapsed = sb.classList.contains("collapsed");
      $("#topbarNewChat").classList.toggle("hidden", !isCollapsed);
      if (state.config) { state.config.sidebarVisible = !isCollapsed; call("saveConfig", { config: state.config }); }
    });
    $("#topbarNewChat").addEventListener("click", newChat);
    $("#modelPill").addEventListener("click", (e) => {
      e.stopPropagation();
      const menu = $("#modelMenu");
      const wasClosed = !menu.classList.contains("open");
      menu.classList.toggle("open");
      if (wasClosed) { renderModelMenu(); positionMenu(); }
      else { hideEffortMenu(); }
    });
    document.addEventListener("click", (e) => { if (!e.target.closest(".model-picker")) { closeModelMenu(); } });
    window.addEventListener("resize", () => { positionMenu(); });

    $("#webSearchToggle").addEventListener("click", () => {
      const on = $("#webSearchToggle").classList.toggle("active");
      if (state.config) { state.config.webSearchEnabled = on; call("saveConfig", { config: state.config }); }
    });

    $("#sendBtn").addEventListener("click", send);
    $("#stopBtn").addEventListener("click", stopGeneration);
    const input = $("#promptInput");
    input.addEventListener("input", autoGrow);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
    });

    // ---- File attach ----
    $("#attachBtn").addEventListener("click", () => $("#fileInput").click());
    $("#fileInput").addEventListener("change", (e) => {
      handleFiles(e.target.files);
      e.target.value = ""; // reset so same file can be re-selected
    });

    // ---- Drag and drop on composer ----
    const composerBox = $(".composer-box");
    if (composerBox) {
      ["dragenter", "dragover"].forEach(ev => {
        composerBox.addEventListener(ev, (e) => {
          e.preventDefault();
          e.stopPropagation();
          composerBox.classList.add("drag-over");
        });
      });
      ["dragleave", "drop"].forEach(ev => {
        composerBox.addEventListener(ev, (e) => {
          e.preventDefault();
          e.stopPropagation();
          if (ev === "dragleave" && e.target !== composerBox) return;
          composerBox.classList.remove("drag-over");
        });
      });
      composerBox.addEventListener("drop", (e) => {
        const files = e.dataTransfer?.files;
        if (files && files.length) handleFiles(files);
      });
      // Also support paste of images
      input.addEventListener("paste", (e) => {
        const items = e.clipboardData?.items;
        if (!items) return;
        const imageFiles = [];
        for (const item of items) {
          if (item.type.startsWith("image/")) {
            const f = item.getAsFile();
            if (f) imageFiles.push(f);
          }
        }
        if (imageFiles.length) { e.preventDefault(); handleFiles(imageFiles); }
      });
    }
  }

  function autoGrow() {
    const t = $("#promptInput"); t.style.height = "auto"; t.style.height = Math.min(t.scrollHeight, 200) + "px";
  }

  function bindKeyboard() {
    document.addEventListener("keydown", (e) => {
      const ctrl = e.ctrlKey || e.metaKey;
      if (ctrl && e.key.toLowerCase() === "n") { e.preventDefault(); newChat(); }
      else if (ctrl && e.key.toLowerCase() === "b") { e.preventDefault(); $("#sidebar").classList.toggle("collapsed"); const col = $("#sidebar").classList.contains("collapsed"); $("#topbarNewChat").classList.toggle("hidden", !col); if (state.config) { state.config.sidebarVisible = !col; call("saveConfig", { config: state.config }); } }
      else if (e.key === "Escape" && state.streaming) { stopGeneration(); }
    });
  }

  async function newChat() {
    // Create a draft chat in-memory only — not persisted until first message is sent
    state.draftChat = {
      id: "draft-" + Date.now().toString(36),
      title: "New Chat",
      model: state.currentModel,
      messages: [],
    };
    state.currentId = state.draftChat.id;
    showView("chat");
    renderChatList();
    $("#messages").innerHTML = "";
    $("#emptyState").classList.remove("hidden");
    $("#viewChat").classList.add("chat-empty");
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
