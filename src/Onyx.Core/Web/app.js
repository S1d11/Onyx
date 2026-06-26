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
    view: "chat",
    appVersion: "",
    hardware: null,
    connectors: [],
    modelVisionCache: {},   // modelName -> bool (supports vision)
    modelVisionLoading: {}, // modelName -> bool (currently checking)
    pendingImages: [],      // [{ dataUrl, name }] — base64 data URLs to send
    // Per-chat streaming state: chatId -> { el, text, sources, searchStatusEl }
    streaming: new Map(),
  };

  function isStreaming(chatId) {
    return state.streaming.has(chatId);
  }

  function isCurrentChatStreaming() {
    return state.currentId && isStreaming(state.currentId);
  }

  // Extract thinking/reasoning content wrapped in <think>...</think> tags
  function extractThinking(text) {
    const openTags = ["<think>", "<thinking>"];
    const closeTags = ["</think>", "</thinking>"];
    let openIdx = -1, openTag = "";
    for (const tag of openTags) {
      const idx = text.indexOf(tag);
      if (idx !== -1 && (openIdx === -1 || idx < openIdx)) { openIdx = idx; openTag = tag; }
    }
    if (openIdx === -1) return { thinking: null, main: text, done: true };
    let closeIdx = -1, closeTag = "";
    for (const tag of closeTags) {
      const idx = text.indexOf(tag, openIdx + openTag.length);
      if (idx !== -1 && (closeIdx === -1 || idx < closeIdx)) { closeIdx = idx; closeTag = tag; }
    }
    const before = text.slice(0, openIdx);
    if (closeIdx === -1) {
      const thinking = text.slice(openIdx + openTag.length);
      return { thinking, main: before, done: false };
    }
    const thinking = text.slice(openIdx + openTag.length, closeIdx);
    const after = text.slice(closeIdx + closeTag.length);
    return { thinking, main: before + after, done: true };
  }

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
      case "orchestratorStage": onOrchestratorStage(msg); break;
      case "intent": onIntent(msg); break;
      case "toolExecuting": onToolExecuting(msg); break;
      case "toolExecuted": onToolExecuted(msg); break;
      case "systemConfirmation": onSystemConfirmation(msg); break;
      case "chatTitle": onChatTitle(msg); break;
      case "pullProgress": onPullProgress(msg); break;
      case "pullDone": onPullDone(msg); break;
      case "pullCancelled": onPullCancelled(msg); break;
      case "pullError": toast("Pull failed: " + msg.message); break;
      case "updateStatus": onUpdateStatus(msg); break;
      case "updateReady": onUpdateReady(msg); break;
      case "ollamaStarting": toast("Starting Ollama…"); break;
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
    state.connectors = data.connectors || [];
    state.appVersion = data.appVersion || "";
    state.hardware = data.hardware || null;
    if (state.config) {
      state.currentModel = state.config.defaultModel;
      $("#composerModelLabel").textContent = state.currentModel || "Select";
      updateComposerModel();
      if (!state.config.sidebarVisible) { $("#sidebar").classList.add("collapsed"); $("#topbarNewChat").classList.remove("hidden"); }
      updateWebSearchToggleUI(state.config.webSearchMode || (state.config.webSearchEnabled ? "auto" : "off"));
      const spn2 = $("#sidebarProfileName"); if (spn2) spn2.textContent = state.config.defaultModel || "User";
      const spa2 = $("#sidebarProfileAvatar"); if (spa2) spa2.textContent = (state.config.defaultModel || "U").charAt(0).toUpperCase();
      updateAttachButtonVisibility();
    }
    renderChatList();
    // On first start, launch into a new chat instead of the last one
    newChat();
    if (!data.serverReachable) toast("Server not reachable. Is `ollama serve` running?");
  }

  // ---- View routing ----
  function showView(name) {
    state.view = name;
    $("#viewChat").classList.toggle("active", name === "chat");
    $("#viewLaunch").classList.toggle("active", name === "launch");
    $("#viewConnections").classList.toggle("active", name === "connections");
    $("#viewSettings").classList.toggle("active", name === "settings");
    $("#viewReleaseNotes").classList.toggle("active", name === "releaseNotes");
    // Hide sidebar only on settings and its submenus (release notes)
    const hideSidebar = name === "settings" || name === "releaseNotes";
    $("#sidebar").classList.toggle("collapsed", hideSidebar);
    const toggle = $("#sidebarToggle");
    if (toggle) toggle.style.display = hideSidebar ? "none" : "";
    if (name === "chat") {
      if (state.config?.sidebarVisible) $("#sidebar").classList.remove("collapsed");
      $("#promptInput").focus();
    }
  }

  // ---- chat list ----
  function renderChatList() {
    const list = $("#chatList");
    // If a rename is in progress, preserve the input element
    const renameInput = list.querySelector(".ci-rename-input");
    if (renameInput) return;
    list.innerHTML = "";
    if (state.chats.length === 0) return;
    const label = document.createElement("div");
    label.className = "chat-list-label";
    label.textContent = "Chats";
    list.appendChild(label);
    state.chats.forEach(c => {
      const el = document.createElement("div");
      el.className = "chat-item" + (isStreaming(c.id) ? " streaming" : "");
      el.textContent = c.title;
      el.addEventListener("click", () => openChat(c.id));
      el.addEventListener("contextmenu", (ev) => {
        ev.preventDefault();
        showContextMenu(ev.clientX, ev.clientY, c, el);
      });
      list.appendChild(el);
    });
  }

  // ---- Connections ----
  function renderConnections() {
    const grid = $("#connectionsGrid");
    if (!grid) return;
    grid.innerHTML = "";

    // SVG icons keyed by connector id — uses actual brand logos
    const ICONS = {
      filesystem: `<svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>`,
      system: `<svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>`,
      github: `<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"/></svg>`,
      gmail: `<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M24 5.457v13.909c0 .904-.732 1.636-1.636 1.636h-3.819V11.73L12 16.64l-6.545-4.91v9.273H1.636A1.636 1.636 0 0 1 0 19.366V5.457c0-2.023 2.309-3.178 3.927-1.964L5.455 4.64 12 9.548l6.545-4.91 1.528-1.145C19.69 2.28 22 3.434 22 5.457z"/></svg>`,
      gdrive: `<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M7.71 3.5L1.15 15l3.42 6 6.56-11.5L7.71 3.5zm10.58 0H8.84l6.56 11.5h9.45L18.29 3.5zM7.16 16.5L4.01 22h11.16l3.15-5.5H7.16z"/></svg>`,
    };

    const connectors = state.connectors && state.connectors.length > 0 ? state.connectors : [
      { id: "filesystem", name: "Filesystem", description: "Read, write, delete, and list files and directories on your computer.", color: "#6366f1", connected: true },
      { id: "system", name: "System", description: "Run shell commands, manage registry, environment variables, PATH, and processes.", color: "#f59e0b", connected: true },
      { id: "github", name: "GitHub", description: "Access your repositories, issues, pull requests, and code. Search repos, create issues, read files, and more.", color: "#181717", connected: false },
      { id: "gmail", name: "Gmail", description: "List, search, read, and send emails from your Gmail account.", color: "#ea4335", connected: false },
      { id: "gdrive", name: "Google Drive", description: "List, search, read, upload, and manage files in Google Drive.", color: "#0f9d58", connected: false },
    ];

    connectors.forEach(conn => {
      const iconSvg = ICONS[conn.id] || ICONS.filesystem;
      const card = document.createElement("div");
      card.className = "connection-card";
      card.innerHTML = `
        <div class="connection-icon" style="background:${conn.color}">${iconSvg}</div>
        <div class="connection-body">
          <div class="connection-name">${OllamaMD.escape(conn.name)}</div>
          <div class="connection-desc">${OllamaMD.escape(conn.description)}</div>
        </div>
        <div class="connection-status ${conn.connected ? '' : 'offline'}">
          <span class="connection-status-dot"></span>
          ${conn.connected ? 'Connected' : 'Disconnected'}
        </div>
      `;
      card.addEventListener("click", () => {
        if (conn.id === "github" && !conn.connected) {
          showGitHubConnectDialog();
        } else if ((conn.id === "gmail" || conn.id === "gdrive") && !conn.connected) {
          showGoogleConnectDialog();
        }
      });
      grid.appendChild(card);
    });
  }

  function showGitHubConnectDialog() {
    const existing = document.querySelector(".github-connect-dialog");
    if (existing) existing.remove();

    const dialog = document.createElement("div");
    dialog.className = "github-connect-dialog";
    dialog.style.cssText = "position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.6);display:flex;align-items:center;justify-content:center;z-index:300;";
    dialog.innerHTML = `
      <div style="background:var(--surface);border:1px solid var(--border);border-radius:var(--radius-lg);padding:32px;width:420px;max-width:90vw;text-align:center;">
        <div style="width:56px;height:56px;border-radius:14px;background:#181717;display:flex;align-items:center;justify-content:center;margin:0 auto 16px;">
          <svg viewBox="0 0 24 24" width="28" height="28" fill="white"><path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"/></svg>
        </div>

        <!-- Step 1: Initial sign-in button -->
        <div id="githubStep1">
          <h3 style="margin:0 0 8px;font-size:18px;color:var(--text);">Connect GitHub</h3>
          <p style="margin:0 0 24px;font-size:13px;color:var(--text-muted);">Sign in with your GitHub account to access repositories, issues, pull requests, and code search.</p>
          <div style="display:flex;gap:8px;justify-content:center;">
            <button id="githubCancel" style="padding:10px 20px;border-radius:var(--radius-md);border:1px solid var(--border);background:var(--surface-hover);color:var(--text);font-size:14px;cursor:pointer;">Cancel</button>
            <button id="githubSignIn" style="padding:10px 20px;border-radius:var(--radius-md);border:none;background:#181717;color:white;font-size:14px;cursor:pointer;display:flex;align-items:center;gap:8px;">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"/></svg>
              Sign in with GitHub
            </button>
          </div>
        </div>

        <!-- Step 2: Show the code (hidden initially) -->
        <div id="githubStep2" style="display:none;">
          <h3 style="margin:0 0 8px;font-size:18px;color:var(--text);">Enter this code on GitHub</h3>
          <p style="margin:0 0 16px;font-size:13px;color:var(--text-muted);">A browser tab has opened. Enter the code below to authorize Onyx.</p>
          <div id="githubUserCode" style="font-family:'SF Mono','Cascadia Code','Consolas',monospace;font-size:36px;font-weight:700;letter-spacing:4px;color:var(--text);background:var(--bg);border:2px solid var(--border);border-radius:var(--radius-md);padding:16px 24px;margin:0 auto 16px;display:inline-block;">------</div>
          <div id="githubConnectStatus" style="margin-bottom:16px;font-size:13px;color:var(--text-muted);min-height:20px;">Waiting for you to authorize on GitHub...</div>
          <div style="display:flex;gap:8px;justify-content:center;">
            <button id="githubCancel2" style="padding:10px 20px;border-radius:var(--radius-md);border:1px solid var(--border);background:var(--surface-hover);color:var(--text);font-size:14px;cursor:pointer;">Cancel</button>
          </div>
        </div>
      </div>
    `;
    document.body.appendChild(dialog);

    const step1 = dialog.querySelector("#githubStep1");
    const step2 = dialog.querySelector("#githubStep2");
    const codeEl = dialog.querySelector("#githubUserCode");
    const statusEl = dialog.querySelector("#githubConnectStatus");

    const closeDialog = () => {
      call("cancelGitHubAuth", {}).catch(() => {});
      dialog.remove();
    };

    dialog.querySelector("#githubCancel").addEventListener("click", closeDialog);
    dialog.querySelector("#githubCancel2").addEventListener("click", closeDialog);

    dialog.querySelector("#githubSignIn").addEventListener("click", async () => {
      const btn = dialog.querySelector("#githubSignIn");
      btn.disabled = true;
      btn.style.opacity = "0.6";

      try {
        // Step 1: Request device code from GitHub
        const result = await call("connectGitHub", {});
        if (!result || !result.success) {
          statusEl.textContent = result?.error || "Failed to start. Please try again.";
          btn.disabled = false;
          btn.style.opacity = "1";
          return;
        }

        // Show the user code
        step1.style.display = "none";
        step2.style.display = "block";
        codeEl.textContent = result.userCode;

        // Step 2: Poll for the token (user enters code on GitHub)
        const pollResult = await call("completeGitHubAuth", {});
        if (pollResult && pollResult.success) {
          state.config.githubToken = "connected";
          dialog.remove();
          renderConnections();
          toast("GitHub connected");
        } else {
          statusEl.textContent = "Authorization timed out or was denied. Please try again.";
          // Go back to step 1 so user can retry
          step2.style.display = "none";
          step1.style.display = "block";
          btn.disabled = false;
          btn.style.opacity = "1";
        }
      } catch (e) {
        statusEl.textContent = "Error: " + (e.message || e);
        step2.style.display = "none";
        step1.style.display = "block";
        btn.disabled = false;
        btn.style.opacity = "1";
      }
    });
  }

  function showGoogleConnectDialog() {
    const existing = document.querySelector(".google-connect-dialog");
    if (existing) existing.remove();

    const dialog = document.createElement("div");
    dialog.className = "google-connect-dialog";
    dialog.style.cssText = "position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.6);display:flex;align-items:center;justify-content:center;z-index:300;";
    dialog.innerHTML = `
      <div style="background:var(--surface);border:1px solid var(--border);border-radius:var(--radius-lg);padding:32px;width:400px;max-width:90vw;text-align:center;">
        <div style="display:flex;align-items:center;justify-content:center;gap:8px;margin:0 auto 16px;">
          <svg viewBox="0 0 24 24" width="28" height="28" fill="#4285F4"><path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"/><path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/><path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05"/><path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84C6.71 7.31 9.14 5.38 12 5.38z" fill="#EA4335"/></svg>
        </div>
        <h3 style="margin:0 0 8px;font-size:18px;color:var(--text);">Connect Google</h3>
        <p style="margin:0 0 24px;font-size:13px;color:var(--text-muted);">Sign in with your Google account to connect both Gmail and Google Drive.</p>
        <div id="googleConnectStatus" style="margin-bottom:16px;font-size:13px;color:var(--text-muted);min-height:20px;"></div>
        <div style="display:flex;gap:8px;justify-content:center;">
          <button id="googleCancel" style="padding:10px 20px;border-radius:var(--radius-md);border:1px solid var(--border);background:var(--surface-hover);color:var(--text);font-size:14px;cursor:pointer;">Cancel</button>
          <button id="googleSignIn" style="padding:10px 20px;border-radius:var(--radius-md);border:none;background:white;color:#757575;font-size:14px;cursor:pointer;display:flex;align-items:center;gap:8px;border:1px solid #dadce0;">
            <svg viewBox="0 0 24 24" width="18" height="18"><path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z" fill="#4285F4"/><path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/><path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05"/><path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84C6.71 7.31 9.14 5.38 12 5.38z" fill="#EA4335"/></svg>
            Sign in with Google
          </button>
        </div>
      </div>
    `;
    document.body.appendChild(dialog);

    dialog.querySelector("#googleCancel").addEventListener("click", () => dialog.remove());
    dialog.querySelector("#googleSignIn").addEventListener("click", async () => {
      const statusEl = dialog.querySelector("#googleConnectStatus");
      const btn = dialog.querySelector("#googleSignIn");
      btn.disabled = true;
      btn.style.opacity = "0.6";
      statusEl.textContent = "Opening browser... Sign in with Google and authorize Onyx.";

      try {
        const result = await call("connectGoogle", {});
        if (result && result.success) {
          state.config.googleRefreshToken = "connected";
          dialog.remove();
          renderConnections();
          toast("Google connected — Gmail + Drive ready");
        } else {
          statusEl.textContent = result?.error || "Failed to connect. Please try again.";
          btn.disabled = false;
          btn.style.opacity = "1";
        }
      } catch (e) {
        statusEl.textContent = "Error: " + (e.message || e);
        btn.disabled = false;
        btn.style.opacity = "1";
      }
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
    // Account for chat-item padding so input fits inside content box
    input.style.width = Math.max(80, el.offsetWidth - 24) + "px";
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
    const empty = c.messages.length === 0 && !isStreaming(id);
    $("#emptyState").classList.toggle("hidden", !empty);
    $("#viewChat").classList.toggle("chat-empty", empty);
    // Update streaming UI to reflect this chat's state
    setStreamingUI(isCurrentChatStreaming());
  }

  function renderMessages(c) {
    const m = $("#messages");
    m.innerHTML = "";
    c.messages.forEach(msg => appendMessageEl(msg.role, msg.content, {
      sources: msg.sources, error: msg.error, evalCount: msg.evalCount, totalMs: msg.totalMs,
      images: msg.images, thinking: msg.thinking, thinkingMs: msg.thinkingMs
    }));
    // If this chat is currently streaming, re-create the in-progress assistant element
    const stream = state.streaming.get(c.id);
    if (stream) {
      const el = appendMessageEl("assistant", "");
      stream.el = el;
      const parsed = extractThinking(stream.text);
      const opts = {
        thinking: parsed.thinking,
        thinkingMs: stream.thinkingMs,
        thinkingExpanded: stream.thinkingExpanded,
        sources: stream.sources,
        isStreaming: true
      };
      if (parsed.thinking && !parsed.done) {
        // Still in thinking mode: show thinking indicator
        renderThinkingInProgress(el.querySelector(".msg-body"), parsed.thinking, parsed.main);
      } else {
        renderAssistantBody(el.querySelector(".msg-body"), parsed.main || "", opts);
        if (!parsed.done) {
          el.querySelector(".msg-main-content").innerHTML += '<span class="cursor"></span>';
        }
      }
      // Re-attach search status if present
      if (stream.searchStatusEl) {
        m.appendChild(stream.searchStatusEl);
      }
      m.scrollTop = m.scrollHeight;
    }
  }

  function renderThinkingInProgress(body, thinkingText, mainText) {
    body.innerHTML = "";
    const thoughtSection = document.createElement("div");
    thoughtSection.className = "msg-thinking";
    const header = document.createElement("div");
    header.className = "msg-thinking-header in-progress";
    header.innerHTML = `<span class="thinking-dot"></span> Thinking…`;
    const content = document.createElement("div");
    content.className = "msg-thinking-content";
    content.innerHTML = OllamaMD.render(thinkingText);
    thoughtSection.appendChild(header);
    thoughtSection.appendChild(content);
    body.appendChild(thoughtSection);
    const mainDiv = document.createElement("div");
    mainDiv.className = "msg-main-content";
    mainDiv.innerHTML = mainText ? OllamaMD.render(mainText) : '<span class="cursor"></span>';
    body.appendChild(mainDiv);
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
    const body = document.createElement("div");
    body.className = "msg-body";
    wrap.innerHTML = `<div class="msg-role">${role}</div>`;
    wrap.appendChild(body);

    if (opts.error) {
      body.innerHTML = `<div class="msg-error">${OllamaMD.escape(opts.error)}</div>`;
    } else if (role === "user") {
      body.innerHTML = imagesHtml + OllamaMD.escape(content).replace(/\n/g, "<br>");
    } else {
      // Assistant message: build body dynamically with optional thinking section
      renderAssistantBody(body, content, opts);
    }

    m.appendChild(wrap);
    m.scrollTop = m.scrollHeight;
    return wrap;
  }

  function renderAssistantBody(body, content, opts = {}) {
    body.innerHTML = "";
    if (opts.thinking) {
      const thoughtSection = document.createElement("div");
      thoughtSection.className = "msg-thinking" + (opts.thinkingExpanded ? "" : " collapsed");
      const header = document.createElement("div");
      header.className = "msg-thinking-header";
      const duration = opts.thinkingMs ? ` for ${(opts.thinkingMs / 1000).toFixed(1)}s` : "";
      header.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="6 9 12 15 18 9"/></svg> Thought${duration}`;
      header.addEventListener("click", () => { thoughtSection.classList.toggle("collapsed"); });
      const thinkingContent = document.createElement("div");
      thinkingContent.className = "msg-thinking-content";
      thinkingContent.innerHTML = OllamaMD.render(opts.thinking);
      thoughtSection.appendChild(header);
      thoughtSection.appendChild(thinkingContent);
      body.appendChild(thoughtSection);
    }
    const mainDiv = document.createElement("div");
    mainDiv.className = "msg-main-content";
    mainDiv.innerHTML = OllamaMD.render(content);
    body.appendChild(mainDiv);
    if (opts.sources && opts.sources.length) body.appendChild(buildSourcesEl(opts.sources, true));
    if (!opts.error) appendMsgActions(body.closest(".msg"), content, opts);
  }

  function buildSourcesEl(sources, compact) {
    const el = document.createElement("div");
    el.className = "sources collapsed";
    const header = document.createElement("div");
    header.className = "sources-header";
    header.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="6 9 12 15 18 9"/></svg> Sources (${sources.length})`;
    const list = document.createElement("div");
    list.className = "sources-list";
    list.innerHTML = sources.map((s, i) =>
      `<a class="source-card" href="${OllamaMD.escape(s.url)}" target="_blank" rel="noopener">
        <div class="sc-title">[${i + 1}] ${OllamaMD.escape(s.title)}</div>
        <div class="sc-url">${OllamaMD.escape(s.url)}</div>
        ${compact ? "" : `<div class="sc-snippet">${OllamaMD.escape(s.snippet)}</div>`}
      </a>`).join("");
    el.appendChild(header);
    el.appendChild(list);
    header.addEventListener("click", () => el.classList.toggle("collapsed"));
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

    requestAnimationFrame(() => requestAnimationFrame(() => positionMenu()));
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

    // Use actual rendered width/height after DOM has settled
    const menuW = Math.min(320, Math.max(280, menu.offsetWidth || 280));
    const effortW = 260;
    const gap = 8;
    const pad = 16;

    // ---- Vertical positioning ----
    // Measure how much space we actually need
    const neededH = Math.min(380, menu.scrollHeight || 380);
    const spaceAbove = rect.top - pad;
    const spaceBelow = vh - rect.bottom - pad;
    let menuH, openAbove;
    if (spaceAbove >= neededH) {
      menuH = Math.min(neededH, spaceAbove); openAbove = true;
    } else if (spaceBelow >= neededH) {
      menuH = Math.min(neededH, spaceBelow); openAbove = false;
    } else if (spaceAbove >= spaceBelow) {
      menuH = Math.max(200, spaceAbove); openAbove = true;
    } else {
      menuH = Math.max(200, spaceBelow); openAbove = false;
    }
    menu.style.maxHeight = menuH + "px";
    if (em && em.classList.contains("open")) em.style.maxHeight = menuH + "px";

    // Clamp menuTop so it never goes above the viewport
    let menuTop = openAbove ? (rect.top - menuH - gap) : (rect.bottom + gap);
    menuTop = Math.max(pad, Math.min(menuTop, vh - menuH - pad));
    menu.style.top = menuTop + "px";
    menu.style.bottom = "auto";
    if (em && em.classList.contains("open")) em.style.top = menuTop + "px";

    // ---- Horizontal positioning ----
    // Anchor the menu to the button: try right-aligned with the button first,
    // then left-aligned, then clamp to viewport
    let menuLeft = rect.right - menuW; // right-align with button
    if (menuLeft < pad) menuLeft = rect.left; // fallback to left-align
    if (menuLeft + menuW > vw - pad) menuLeft = vw - menuW - pad;
    if (menuLeft < pad) menuLeft = pad;
    menu.style.left = menuLeft + "px";
    menu.style.right = "auto";

    // Effort menu: try right of model menu, fallback to left
    if (em && em.classList.contains("open")) {
      let effortLeft = menuLeft + menuW + gap;
      if (effortLeft + effortW > vw - pad) {
        effortLeft = menuLeft - effortW - gap;
      }
      if (effortLeft < pad) {
        effortLeft = pad;
      }
      em.style.left = effortLeft + "px";
      em.style.right = "auto";
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
    if ((!text && !images.length) || isCurrentChatStreaming()) return;
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

    const webSearchMode = state.config?.webSearchMode || (state.config?.webSearchEnabled ? "auto" : "off");
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
    // Register per-chat streaming state
    state.streaming.set(chat.id, { el, text: "", sources: null, searchStatusEl: null, tempSourcesEl: null, thinkingStartTime: 0, thinkingEndTime: 0, thinkingMs: 0, thinkingExpanded: false });
    setStreamingUI(true);
    renderChatList(); // show streaming indicator

    // Build history — include images on the latest user message
    // Reconstruct thinking tags for assistant messages so the model has full context
    const history = chat.messages
      .filter(m => m.role === "user" || (m.role === "assistant" && m.content))
      .map((m, i) => {
        let content = m.content;
        if (m.role === "assistant" && m.thinking) {
          content = `\n\n${m.content}`;
        }
        const msg = { role: m.role, content };
        // Attach images to the last user message
        if (m.role === "user" && m.images && m.images.length && i === chat.messages.length - 1) {
          msg.images = m.images.map(dataUrl => dataUrlToBase64(dataUrl));
        }
        return msg;
      });

    try { await call("sendMessage", { chatId: chat.id, model: state.currentModel, messages: history, webSearchMode }); }
    catch (err) { onChatError({ chatId: chat.id, message: err.message }); }
  }

  function onChatChunk(msg) {
    const stream = state.streaming.get(msg.chatId);
    if (!stream) return;
    stream.text += msg.content;

    const parsed = extractThinking(stream.text);
    // Track thinking timing
    if (parsed.thinking && !stream.thinkingStartTime) {
      stream.thinkingStartTime = Date.now();
    }
    if (parsed.done && stream.thinkingStartTime && !stream.thinkingEndTime) {
      stream.thinkingEndTime = Date.now();
      stream.thinkingMs = stream.thinkingEndTime - stream.thinkingStartTime;
    }

    if (stream.el) {
      const body = stream.el.querySelector(".msg-body");
      if (parsed.thinking && !parsed.done) {
        // Still thinking: show in-progress indicator
        renderThinkingInProgress(body, parsed.thinking, parsed.main);
      } else {
        renderAssistantBody(body, parsed.main || "", {
          thinking: parsed.thinking,
          thinkingMs: stream.thinkingMs,
          thinkingExpanded: stream.thinkingExpanded,
          isStreaming: true
        });
        if (!parsed.done) {
          const mainDiv = body.querySelector(".msg-main-content");
          if (mainDiv) mainDiv.innerHTML += '<span class="cursor"></span>';
        }
      }
    }
    // Only auto-scroll if we're viewing this chat
    if (msg.chatId === state.currentId) {
      $("#messages").scrollTop = $("#messages").scrollHeight;
    }
  }

  function onChatTitle(msg) {
    const c = state.chats.find(x => x.id === msg.chatId);
    if (c) {
      c.title = msg.title;
      renderChatList();
    }
  }

  function onChatDone(msg) {
    const stream = state.streaming.get(msg.chatId);
    state.streaming.delete(msg.chatId);
    if (msg.chatId === state.currentId) setStreamingUI(false);
    renderChatList(); // remove streaming indicator
    // Parse thinking from the full raw text
    const parsed = stream ? extractThinking(stream.text) : { thinking: null, main: "", done: true };
    const mainText = parsed.main;
    const thinkingText = parsed.thinking;
    let thinkingMs = stream?.thinkingMs || 0;
    if (thinkingText && !thinkingMs && stream?.thinkingStartTime) {
      thinkingMs = Date.now() - stream.thinkingStartTime;
    }

    if (!stream) {
      // Stream was not visible (chat not open). Still need to store the message.
      const c = state.chats.find(x => x.id === msg.chatId);
      if (c && !msg.cancelled) {
        c.messages.push({ role: "assistant", content: mainText, thinking: thinkingText, thinkingMs, sources: msg.sources, evalCount: msg.evalCount, totalMs: msg.totalMs });
      }
      return;
    }

    // Remove temporary sources block from onSearchResults before rendering final message
    if (stream.tempSourcesEl) { stream.tempSourcesEl.remove(); stream.tempSourcesEl = null; }

    if (stream.el) {
      renderAssistantBody(stream.el.querySelector(".msg-body"), mainText, {
        thinking: thinkingText,
        thinkingMs,
        thinkingExpanded: stream.thinkingExpanded,
        sources: msg.sources,
        evalCount: msg.evalCount,
        totalMs: msg.totalMs
      });
    }
    const c = state.chats.find(x => x.id === msg.chatId);
    if (c) {
      if (!msg.cancelled) {
        c.messages.push({ role: "assistant", content: mainText, thinking: thinkingText, thinkingMs, sources: msg.sources, evalCount: msg.evalCount, totalMs: msg.totalMs });
      }
      if (c.title === "New Chat") {
        const firstUser = c.messages.find(m => m.role === "user");
        c.title = generateTitle(firstUser?.content || mainText);
        call("renameChat", { id: c.id, title: c.title }).catch(() => {});
        renderChatList();
      }
    }
  }

  function onChatError(msg) {
    const stream = state.streaming.get(msg.chatId);
    state.streaming.delete(msg.chatId);
    if (msg.chatId === state.currentId) setStreamingUI(false);
    renderChatList(); // remove streaming indicator
    if (stream && stream.tempSourcesEl) { stream.tempSourcesEl.remove(); stream.tempSourcesEl = null; }
    if (stream && stream.el) {
      stream.el.querySelector(".msg-body").innerHTML = `<div class="msg-error">${OllamaMD.escape(msg.message)}</div>`;
    } else {
      toast(msg.message);
    }
  }

  function onSearching(msg) {
    const stream = state.streaming.get(msg.chatId);
    if (!stream) return;
    // Only show search status if viewing this chat
    if (msg.chatId !== state.currentId) return;
    let s = $("#messages").querySelector(".search-status");
    if (!s) { s = document.createElement("div"); s.className = "search-status"; $("#messages").appendChild(s); }
    stream.searchStatusEl = s;
    s.innerHTML = `<div class="spinner"></div> Searching the web for "${OllamaMD.escape(msg.query)}"…`;
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  // ---- Orchestrator events ----
  function onOrchestratorStage(msg) {
    if (msg.chatId !== state.currentId) return;
    let s = $("#messages").querySelector(".orchestrator-status");
    if (!s) {
      s = document.createElement("div");
      s.className = "orchestrator-status";
      s.style.cssText = "padding:8px 16px;font-size:12px;color:var(--text-muted);display:flex;align-items:center;gap:8px;";
      $("#messages").appendChild(s);
    }
    const spinner = msg.stage !== "generating" ? '<div class="spinner" style="width:14px;height:14px"></div>' : "";
    s.innerHTML = `${spinner} ${OllamaMD.escape(msg.message)}`;
    if (msg.stage === "generating") s.remove();
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  function onIntent(msg) {
    if (msg.chatId !== state.currentId) return;
    // Show a subtle intent badge above the response
    let badge = $("#messages").querySelector(".intent-badge");
    if (!badge) {
      badge = document.createElement("div");
      badge.className = "intent-badge";
      badge.style.cssText = "padding:4px 12px;font-size:11px;color:var(--text-muted);opacity:0.7;";
      $("#messages").appendChild(badge);
    }
    const intentLabel = msg.intentType.replace(/([A-Z])/g, ' $1').trim();
    const tools = msg.suggestedTools && msg.suggestedTools.length ? ` → ${msg.suggestedTools.join(", ")}` : "";
    badge.textContent = `Intent: ${intentLabel} (${Math.round(msg.confidence * 100)}%)${tools}`;
  }

  function onToolExecuting(msg) {
    if (msg.chatId !== state.currentId) return;
    let s = $("#messages").querySelector(".orchestrator-status");
    if (!s) {
      s = document.createElement("div");
      s.className = "orchestrator-status";
      s.style.cssText = "padding:8px 16px;font-size:12px;color:var(--text-muted);display:flex;align-items:center;gap:8px;";
      $("#messages").appendChild(s);
    }
    s.innerHTML = `<div class="spinner" style="width:14px;height:14px"></div> Running ${OllamaMD.escape(msg.tool)}…`;
    $("#messages").scrollTop = $("#messages").scrollHeight;
  }

  function onToolExecuted(msg) {
    if (msg.chatId !== state.currentId) return;
    let s = $("#messages").querySelector(".orchestrator-status");
    if (s && !msg.success) {
      s.innerHTML = `⚠ ${OllamaMD.escape(msg.tool)} failed`;
      setTimeout(() => s.remove(), 2000);
    }
  }

  // ---- System tool confirmation dialog ----
  function onSystemConfirmation(msg) {
    if (msg.chatId !== state.currentId) return;

    // Remove any existing confirmation dialog
    const existing = $("#messages").querySelector(".system-confirm");
    if (existing) existing.remove();

    const dialog = document.createElement("div");
    dialog.className = "system-confirm";
    dialog.innerHTML = `
      <div class="system-confirm-card">
        <div class="system-confirm-header">
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>
          <span>System Action Confirmation</span>
        </div>
        <div class="system-confirm-body">
          <div class="system-confirm-action">${OllamaMD.escape(msg.action)}</div>
          <div class="system-confirm-desc">${OllamaMD.escape(msg.description)}</div>
          ${msg.params && Object.keys(msg.params).length > 0 ? `
            <div class="system-confirm-params">
              ${Object.entries(msg.params).map(([k, v]) => `<div><span class="param-key">${OllamaMD.escape(k)}:</span> <span class="param-val">${OllamaMD.escape(String(v))}</span></div>`).join("")}
            </div>
          ` : ""}
        </div>
        <div class="system-confirm-buttons">
          <button class="confirm-deny" data-confirm-id="${msg.confirmId}">Deny</button>
          <button class="confirm-approve" data-confirm-id="${msg.confirmId}">Approve</button>
        </div>
      </div>
    `;
    $("#messages").appendChild(dialog);
    $("#messages").scrollTop = $("#messages").scrollHeight;

    dialog.querySelector(".confirm-approve").addEventListener("click", () => {
      call("confirmSystemAction", { confirmId: msg.confirmId, approved: true });
      dialog.remove();
    });
    dialog.querySelector(".confirm-deny").addEventListener("click", () => {
      call("confirmSystemAction", { confirmId: msg.confirmId, approved: false });
      dialog.remove();
    });
  }

  function onSearchResults(msg) {
    const stream = state.streaming.get(msg.chatId);
    if (stream && stream.searchStatusEl) {
      stream.searchStatusEl.remove();
      stream.searchStatusEl = null;
    } else if (msg.chatId === state.currentId) {
      const s = $("#messages").querySelector(".search-status");
      if (s) s.remove();
    }
    // Store sources on the stream for later use
    if (stream && msg.results) {
      stream.sources = msg.results;
    }
    // Show temporary sources block below the streaming message; will be removed when chat completes
    if (msg.chatId === state.currentId && msg.results && msg.results.length) {
      const tempSources = buildSourcesEl(msg.results, false);
      $("#messages").appendChild(tempSources);
      $("#messages").scrollTop = $("#messages").scrollHeight;
      if (stream) stream.tempSourcesEl = tempSources;
    }
  }

  async function regenerate() {
    if (isCurrentChatStreaming()) return;
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
      let content = m.content;
      if (m.role === "assistant" && m.thinking) {
        content = `\n\n${m.content}`;
      }
      const msg = { role: m.role, content };
      if (m.role === "user" && m.images && m.images.length) {
        msg.images = m.images.map(dataUrl => dataUrlToBase64(dataUrl));
      }
      return msg;
    });
    const el = appendMessageEl("assistant", "");
    el.querySelector(".msg-body").innerHTML = '<span class="cursor"></span>';
    state.streaming.set(c.id, { el, text: "", sources: null, searchStatusEl: null, tempSourcesEl: null, thinkingStartTime: 0, thinkingEndTime: 0, thinkingMs: 0, thinkingExpanded: false });
    setStreamingUI(true);
    renderChatList();
    call("sendMessage", { chatId: c.id, model: state.currentModel, messages: history, webSearchMode: state.config?.webSearchMode || (state.config?.webSearchEnabled ? "auto" : "off") });
  }

  function stopGeneration() {
    if (state.currentId) {
      emit("stopGeneration", { chatId: state.currentId });
    }
  }

  function setStreamingUI(on) {
    $("#sendBtn").classList.toggle("hidden", on);
    $("#stopBtn").classList.toggle("hidden", !on);
  }

  function updateWebSearchToggleUI(mode) {
    const btn = $("#webSearchToggle");
    btn.classList.remove("active", "auto");
    if (mode === "on") btn.classList.add("active");
    else if (mode === "auto") btn.classList.add("auto");
    btn.title = mode === "on" ? "Web search: On" : mode === "auto" ? "Web search: Auto" : "Web search: Off";
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
    // Stop any streaming for this chat
    if (isStreaming(id)) {
      emit("stopGeneration", { chatId: id });
      state.streaming.delete(id);
    }
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
      setStreamingUI(false);
    }
    renderChatList();
  }

  // ---- Launch page ----
  const LAUNCH_APPS = [
    { name: "Claude Code", desc: "Anthropic's coding tool with autogrants.", cmd: "claude", svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="currentColor"><path d="m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z"/></svg>`, color: "#d97757" },
    { name: "Codex App", desc: "An AI agent you can delegate real work to, by OpenAI.", cmd: "codex-app", svg: `<svg viewBox="0 0 16 16" width="22" height="22" fill="currentColor"><path d="M14.949 6.547a3.94 3.94 0 0 0-.348-3.273 4.11 4.11 0 0 0-4.4-1.934A4.1 4.1 0 0 0 8.423.2 4.15 4.15 0 0 0 6.305.086a4.1 4.1 0 0 0-1.891.948 4.04 4.04 0 0 0-1.158 1.753 4.1 4.1 0 0 0-1.563.679A4 4 0 0 0 .554 4.72a3.99 3.99 0 0 0 .502 4.731 3.94 3.94 0 0 0 .346 3.274 4.11 4.11 0 0 0 4.402 1.933c.382.425.852.764 1.377.995.526.231 1.095.35 1.67.346 1.78.002 3.358-1.132 3.901-2.804a4.1 4.1 0 0 0 1.563-.68 4 4 0 0 0 1.14-1.253 3.99 3.99 0 0 0-.506-4.716m-6.097 8.406a3.05 3.05 0 0 1-1.945-.694l.096-.054 3.23-1.838a.53.53 0 0 0 .265-.455v-4.49l1.366.778q.02.011.025.035v3.722c-.003 1.653-1.361 2.992-3.037 2.996m-6.53-2.75a2.95 2.95 0 0 1-.36-2.01l.095.057L5.29 12.09a.53.53 0 0 0 .527 0l3.949-2.246v1.555a.05.05 0 0 1-.022.041L6.473 13.3c-1.454.826-3.311.335-4.15-1.098m-.85-6.94A3.02 3.02 0 0 1 3.07 3.949v3.785a.51.51 0 0 0 .262.451l3.93 2.237-1.366.779a.05.05 0 0 1-.048 0L2.585 9.342a2.98 2.98 0 0 1-1.113-4.094zm11.216 2.571L8.747 5.576l1.362-.776a.05.05 0 0 1 .048 0l3.265 1.86a3 3 0 0 1 1.173 1.207 2.96 2.96 0 0 1-.27 3.2 3.05 3.05 0 0 1-1.36.997V8.279a.52.52 0 0 0-.276-.445m1.36-2.015-.097-.057-3.226-1.855a.53.53 0 0 0-.53 0L6.249 6.153V4.598a.04.04 0 0 1 .019-.04L9.533 2.7a3.07 3.07 0 0 1 3.257.139c.474.325.843.778 1.066 1.303.223.526.289 1.103.191 1.664zM5.503 8.575 4.139 7.8a.05.05 0 0 1-.026-.037V4.049c0-.57.166-1.127.476-1.607s.752-.864 1.275-1.105a3.08 3.08 0 0 1 3.234.41l-.096.054-3.23 1.838a.53.53 0 0 0-.265.455zm.742-1.577 1.758-1 1.762 1v2l-1.755 1-1.762-1z"/></svg>`, color: "#10a37f" },
    { name: "Hermes Agent", desc: "Self-improving AI agent built by Nous Research.", cmd: "hermes", svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="currentColor"><path d="M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>`, color: "#1a1a1a" },
    { name: "OpenClaw", desc: "Personal AI with 100+ skills.", cmd: "openclaw", svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 8v8M8 12h8"/></svg>`, color: "#ef4444" },
    { name: "OpenCode", desc: "Anomaly's open-source coding agent.", cmd: "opencode", svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>`, color: "#6366f1" },
    { name: "Codex", desc: "OpenAI's open source coding agent.", cmd: "codex", svg: `<svg viewBox="0 0 16 16" width="22" height="22" fill="currentColor"><path d="M14.949 6.547a3.94 3.94 0 0 0-.348-3.273 4.11 4.11 0 0 0-4.4-1.934A4.1 4.1 0 0 0 8.423.2 4.15 4.15 0 0 0 6.305.086a4.1 4.1 0 0 0-1.891.948 4.04 4.04 0 0 0-1.158 1.753 4.1 4.1 0 0 0-1.563.679A4 4 0 0 0 .554 4.72a3.99 3.99 0 0 0 .502 4.731 3.94 3.94 0 0 0 .346 3.274 4.11 4.11 0 0 0 4.402 1.933c.382.425.852.764 1.377.995.526.231 1.095.35 1.67.346 1.78.002 3.358-1.132 3.901-2.804a4.1 4.1 0 0 0 1.563-.68 4 4 0 0 0 1.14-1.253 3.99 3.99 0 0 0-.506-4.716m-6.097 8.406a3.05 3.05 0 0 1-1.945-.694l.096-.054 3.23-1.838a.53.53 0 0 0 .265-.455v-4.49l1.366.778q.02.011.025.035v3.722c-.003 1.653-1.361 2.992-3.037 2.996m-6.53-2.75a2.95 2.95 0 0 1-.36-2.01l.095.057L5.29 12.09a.53.53 0 0 0 .527 0l3.949-2.246v1.555a.05.05 0 0 1-.022.041L6.473 13.3c-1.454.826-3.311.335-4.15-1.098m-.85-6.94A3.02 3.02 0 0 1 3.07 3.949v3.785a.51.51 0 0 0 .262.451l3.93 2.237-1.366.779a.05.05 0 0 1-.048 0L2.585 9.342a2.98 2.98 0 0 1-1.113-4.094zm11.216 2.571L8.747 5.576l1.362-.776a.05.05 0 0 1 .048 0l3.265 1.86a3 3 0 0 1 1.173 1.207 2.96 2.96 0 0 1-.27 3.2 3.05 3.05 0 0 1-1.36.997V8.279a.52.52 0 0 0-.276-.445m1.36-2.015-.097-.057-3.226-1.855a.53.53 0 0 0-.53 0L6.249 6.153V4.598a.04.04 0 0 1 .019-.04L9.533 2.7a3.07 3.07 0 0 1 3.257.139c.474.325.843.778 1.066 1.303.223.526.289 1.103.191 1.664zM5.503 8.575 4.139 7.8a.05.05 0 0 1-.026-.037V4.049c0-.57.166-1.127.476-1.607s.752-.864 1.275-1.105a3.08 3.08 0 0 1 3.234.41l-.096.054-3.23 1.838a.53.53 0 0 0-.265.455zm.742-1.577 1.758-1 1.762 1v2l-1.755 1-1.762-1z"/></svg>`, color: "#8b5cf6" },
    { name: "Copilot CLI", desc: "GitHub's AI coding agent for the terminal.", cmd: "copilot", svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="currentColor"><path d="M23.922 16.997C23.061 18.492 18.063 22.02 12 22.02 5.937 22.02.939 18.492.078 16.997A.641.641 0 0 1 0 16.741v-2.869a.883.883 0 0 1 .053-.22c.372-.935 1.347-2.292 2.605-2.656.167-.429.414-1.055.644-1.517a10.098 10.098 0 0 1-.052-1.086c0-1.331.282-2.499 1.132-3.368.397-.406.89-.717 1.474-.952C7.255 2.937 9.248 1.98 11.978 1.98c2.731 0 4.767.957 6.166 2.093.584.235 1.077.546 1.474.952.85.869 1.132 2.037 1.132 3.368 0 .368-.014.733-.052 1.086.23.462.477 1.088.644 1.517 1.258.364 2.233 1.721 2.605 2.656a.841.841 0 0 1 .053.22v2.869a.641.641 0 0 1-.078.256Zm-11.75-5.992h-.344a4.359 4.359 0 0 1-.355.508c-.77.947-1.918 1.492-3.508 1.492-1.725 0-2.989-.359-3.782-1.259a2.137 2.137 0 0 1-.085-.104L4 11.746v6.585c1.435.779 4.514 2.179 8 2.179 3.486 0 6.565-1.4 8-2.179v-6.585l-.098-.104s-.033.045-.085.104c-.793.9-2.057 1.259-3.782 1.259-1.59 0-2.738-.545-3.508-1.492a4.359 4.359 0 0 1-.355-.508Zm2.328 3.25c.549 0 1 .451 1 1v2c0 .549-.451 1-1 1-.549 0-1-.451-1-1v-2c0-.549.451-1 1-1Zm-5 0c.549 0 1 .451 1 1v2c0 .549-.451 1-1 1-.549 0-1-.451-1-1v-2c0-.549.451-1 1-1Zm3.313-6.185c.136 1.057.403 1.913.878 2.497.442.544 1.134.938 2.344.938 1.573 0 2.292-.337 2.657-.751.384-.435.558-1.15.558-2.361 0-1.14-.243-1.847-.705-2.319-.477-.488-1.319-.862-2.824-1.025-1.487-.161-2.192.138-2.533.529-.269.307-.437.808-.438 1.578v.021c0 .265.021.562.063.893Zm-1.626 0c.042-.331.063-.628.063-.894v-.02c-.001-.77-.169-1.271-.438-1.578-.341-.391-1.046-.69-2.533-.529-1.505.163-2.347.537-2.824 1.025-.462.472-.705 1.179-.705 2.319 0 1.211.175 1.926.558 2.361.365.414 1.084.751 2.657.751 1.21 0 1.902-.394 2.344-.938.475-.584.742-1.44.878-2.497Z"/></svg>`, color: "#24292e" },
    { name: "Droid", desc: "Factory's coding agent across terminal and IDEs.", cmd: "droid", svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="8" width="14" height="12" rx="3"/><path d="M12 8V4"/><circle cx="12" cy="3" r="1.5" fill="currentColor"/><circle cx="9.5" cy="13" r="1.2" fill="currentColor"/><circle cx="14.5" cy="13" r="1.2" fill="currentColor"/><path d="M9.5 17h5"/><path d="M3 12v4M21 12v4"/></svg>`, color: "#ff913c" },
  ];

  function initLaunchList() {
    const list = $("#launchList");
    list.innerHTML = "";
    LAUNCH_APPS.forEach(app => {
      const item = document.createElement("div");
      item.className = "launch-item";
      item.innerHTML = `
        <div class="launch-icon" style="background:${app.color || 'var(--surface-hover)'};color:white">${app.svg || ''}</div>
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
  async function loadReleaseNotes() {
    const content = $("#releaseNotesContent");
    const version = $("#releaseNotesVersion");
    version.textContent = "v" + state.appVersion;
    content.innerHTML = '<div style="text-align:center;padding:40px;color:var(--text-muted)">Loading…</div>';
    try {
      const releases = await call("getReleaseNotes");
      if (!releases || releases.length === 0) {
        content.innerHTML = '<div style="text-align:center;padding:40px;color:var(--text-muted)">No release notes found.</div>';
        return;
      }
      const html = releases.map((rel, idx) => {
        const isCurrent = idx === 0;
        const badgeClass = isCurrent ? "current" : "previous";
        const badgeText = isCurrent ? "Current" : "Previous";
        const cardClass = isCurrent ? "current" : "previous";
        const bodyHtml = OllamaMD.render(rel.body || "_No notes provided._");
        return `
          <div class="rn-card ${cardClass}">
            <div class="rn-card-header">
              <span class="rn-card-title">${OllamaMD.escape(rel.tag_name)}</span>
              <span class="rn-card-badge ${badgeClass}">${badgeText}</span>
            </div>
            <div class="rn-card-body">${bodyHtml}</div>
          </div>
        `;
      }).join("");
      const divider = releases.length > 1 ? '<div class="rn-divider">Previous version</div>' : "";
      content.innerHTML = html + divider;
    } catch (err) {
      content.innerHTML = `<div style="text-align:center;padding:40px;color:var(--text-muted)">Failed to load release notes.<br><span style="font-size:12px">${OllamaMD.escape(err.message)}</span></div>`;
    }
  }

  function initSettings() {
    const cfg = state.config || {};
    $("#profileName").textContent = cfg.defaultModel || "User";
    $("#profileAvatar").textContent = (cfg.defaultModel || "U").charAt(0).toUpperCase();
    const spn = $("#sidebarProfileName"); if (spn) spn.textContent = cfg.defaultModel || "User";
    const spa = $("#sidebarProfileAvatar"); if (spa) spa.textContent = (cfg.defaultModel || "U").charAt(0).toUpperCase();
    $("#appVersion").textContent = "v" + state.appVersion;

    // Default model dropdown
    const modelSelect = $("#defaultModelSelect");
    if (modelSelect) {
      modelSelect.innerHTML = "";
      if (!state.models.length) {
        const opt = document.createElement("option");
        opt.textContent = "No models installed";
        opt.value = "";
        modelSelect.appendChild(opt);
      } else {
        state.models.forEach(mo => {
          const opt = document.createElement("option");
          opt.value = mo.name;
          opt.textContent = mo.name;
          if (mo.name === cfg.defaultModel) opt.selected = true;
          modelSelect.appendChild(opt);
        });
        if (!state.models.some(mo => mo.name === cfg.defaultModel) && cfg.defaultModel) {
          const opt = document.createElement("option");
          opt.value = cfg.defaultModel;
          opt.textContent = cfg.defaultModel + " (not installed)";
          opt.selected = true;
          modelSelect.insertBefore(opt, modelSelect.firstChild);
        }
      }
      modelSelect.onchange = () => {
        const name = modelSelect.value;
        if (!name) return;
        if (state.config) {
          state.config.defaultModel = name;
          state.currentModel = name;
          $("#composerModelLabel").textContent = name;
          updateComposerModel();
          call("saveConfig", { config: state.config });
          initSettings(); // refresh profile name/avatar
        }
      };
    }

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

    let dragging = false;
    thumb.onmousedown = (e) => { dragging = true; e.preventDefault(); };
    wrap.onmousedown = (e) => {
      const idx = snapToIndex(e.clientX);
      ctxIdx = idx;
      setSlider(idx);
      if (state.config) { state.config.numCtx = ctxValues[idx]; call("saveConfig", { config: state.config }); }
    };
    document.onmousemove = (e) => {
      if (!dragging) return;
      const idx = snapToIndex(e.clientX);
      ctxIdx = idx;
      setSlider(idx);
    };
    document.onmouseup = () => {
      if (dragging && state.config) {
        state.config.numCtx = ctxValues[ctxIdx];
        call("saveConfig", { config: state.config });
      }
      dragging = false;
    };

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

    // Start minimized toggle
    const minToggle = $("#toggleStartMinimized");
    minToggle.classList.toggle("on", cfg.startMinimized === true);
    minToggle.onclick = () => {
      const on = minToggle.classList.toggle("on");
      if (state.config) { state.config.startMinimized = on; call("saveConfig", { config: state.config }); }
      toast(on ? "App will start minimized" : "App will show window on start");
    };

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

    // Launch on startup toggle
    const startupToggle = $("#toggleLaunchOnStartup");
    startupToggle.classList.toggle("on", cfg.launchOnStartup !== false);
    startupToggle.onclick = () => {
      const on = startupToggle.classList.toggle("on");
      if (state.config) {
        state.config.launchOnStartup = on;
        call("saveConfig", { config: state.config });
        call("setLaunchOnStartup", { enabled: on });
      }
      toast(on ? "Onyx will launch on startup" : "Launch on startup disabled");
    };

    // Reset to defaults
    $("#resetDefaults").onclick = () => {
      if (!confirm("Reset all settings to defaults?")) return;
      const defaults = {
        serverUrl: "http://localhost:11434", defaultModel: "llama3.2",
        webSearchEnabled: true, webSearchMode: "auto", webSearchProvider: "duckduckgo",
        webSearchApiKey: "", theme: "dark", sidebarVisible: true, zoom: 1,
        temperature: 0.8, topK: 40, topP: 0.9, numCtx: 4096,
        systemPrompt: "", maxSearchResults: 5, closeBehavior: "tray",
        checkUpdatesOnStartup: true, exposeToNetwork: false, modelPath: "", stream: true,
        effort: "medium", thinkingEnabled: false, startMinimized: false, launchOnStartup: true,
      };
      state.config = { ...state.config, ...defaults };
      call("saveConfig", { config: state.config });
      initSettings(); // re-render
      updateWebSearchToggleUI(state.config.webSearchMode);
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
    $("#connectionsBtn").addEventListener("click", () => { showView("connections"); renderConnections(); });
    $("#settingsBtn").addEventListener("click", () => { showView("settings"); initSettings(); });
    $("#backFromConnections").addEventListener("click", () => showView("chat"));
    $("#backFromSettings").addEventListener("click", () => showView("chat"));
    $("#backFromReleaseNotes").addEventListener("click", () => showView("settings"));
    $("#releaseNotesRow").addEventListener("click", () => { showView("releaseNotes"); loadReleaseNotes(); });

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
      if (wasClosed) {
        renderModelMenu();
        // Wait for DOM to settle so we can measure actual dimensions
        requestAnimationFrame(() => requestAnimationFrame(() => positionMenu()));
      }
      else { hideEffortMenu(); }
    });
    document.addEventListener("click", (e) => { if (!e.target.closest(".model-picker")) { closeModelMenu(); } });
    window.addEventListener("resize", () => { positionMenu(); });

    $("#webSearchToggle").addEventListener("click", () => {
      const modes = ["off", "auto", "on"];
      const current = state.config?.webSearchMode || (state.config?.webSearchEnabled ? "auto" : "off");
      const next = modes[(modes.indexOf(current) + 1) % modes.length];
      if (state.config) { state.config.webSearchMode = next; call("saveConfig", { config: state.config }); }
      updateWebSearchToggleUI(next);
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
      else if (e.key === "Escape" && isCurrentChatStreaming()) { stopGeneration(); }
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
