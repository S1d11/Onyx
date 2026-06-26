# Ollama 2.0 — Project Handoff

## Project Overview

Ollama 2.0 is a sleek, enterprise-grade desktop client for Ollama (local LLM inference). Built as a C# WPF desktop application with an embedded WebView2 frontend, it provides a modern chat interface for interacting with locally-hosted AI models. The app features hardware-aware model recommendations, model management, web search integration, and a polished UI inspired by ChatGPT.

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Backend / Host** | C# .NET 10, WPF, WebView2 (Microsoft Edge) |
| **Frontend** | Vanilla JavaScript (ES modules), Custom CSS |
| **Build** | `dotnet publish` single-file self-contained |
| **Installer** | Inno Setup |
| **Package Manager** | NuGet |

---

## Architecture

### Host App (C#)

- **`MainWindow.xaml.cs`** — Main WPF window hosting WebView2, title bar styling (dark mode via DWM)
- **`Bridge.cs`** — Bidirectional C# ↔ JS communication bridge. Handles:
  - RPC protocol with `_rpcId` counter
  - Initial state hydration (`SendInitialState`)
  - Chat streaming, model pulling, config persistence
  - `CancellationTokenSource` lifecycle (always `Dispose()` before replacing)
  - Hardware info detection exposed to frontend
- **`App.xaml.cs`** — App startup, single-instance enforcement, tray icon
- **`NotifyIconHelper.cs`** — System tray integration
- **`HardwareInfo.cs`** — WMI-based hardware detection (CPU, RAM, GPU/VRAM)
- **`Models.cs`** — Model metadata and library definitions
- **`OllamaClient.cs`** — HTTP client for Ollama API
- **`ChatStore.cs`** — Local chat persistence
- **`ConfigService.cs`** — Settings management
- **`UpdateService.cs`** — Auto-update checking
- **`WebSearchService.cs`** — Web search integration

### Frontend (WebView2)

- **`index.html`** — Single-page app layout (sidebar, main views, modals)
- **`app.js`** — Main application logic:
  - State management (`state` object)
  - View routing (`showView` — chat/launch/settings)
  - Chat streaming UI
  - Model picker with Effort submenu
  - Modal management
  - Event binding
- **`styles.css`** — All styling (dark theme, custom properties)
- **`md.js`** — Custom Markdown renderer (`OllamaMD`)

### RPC Protocol

C# Bridge ↔ JS uses JSON-RPC-like messages:

```
JS → C#: { id: number, method: string, params: object }
C# → JS: { id: number, ok: boolean, data?: any, error?: string }
```

Key rule: `const rpcId` shadows caused a TDZ crash. Fixed by renaming outer `let rpcId` to `_rpcCounter`.

---

## Version History (Chronological)

### v2.7.5 — Sidebar Polish: Icon Swap, Padding, Topbar New Chat
- Replaced hamburger menu with panel/sidebar icon (two-panel design)
- Increased sidebar padding (nav buttons, chat items, header)
- Added "New Chat" button to topbar that appears when sidebar is collapsed

### v2.7.6 — Sidebar Toggle Moved Inside Sidebar
- Moved sidebar toggle button from topbar into the sidebar itself
- Removed visual separator between nav buttons and chat list
- Tightened spacing throughout sidebar

### v2.7.7 — Toggle Stays Visible When Sidebar Collapsed
- Moved toggle OUTSIDE the `<aside>` element as a fixed-position button
- Toggle now visible regardless of sidebar open/closed state

### v2.7.8 — Topbar New Chat Button Position Fix
- Moved topbar New Chat button to the RIGHT side (was overlapping toggle on left)
- Removed `.has-toggle` padding class

### v2.7.9 — Sidebar Toggle Icon Fix
- Replaced broken hollow outline icon with solid two-panel sidebar icon
- Uses `fill=currentColor` instead of stroke for crisp rendering
- Size bumped to 18px

### v2.8.0 — ChatGPT-Inspired Polish
- **Sidebar profile footer**: Circular avatar + model name at bottom-left
- **Empty state welcome text**: "Where should we begin?" below llama logo
- **Prominent New Chat button**: Border + brighter text

### v2.8.1 — New Chat Button Right Alignment
- Fixed overlap: topbar New Chat button now uses `margin-left: auto`

### v2.8.2 — Effort Submenu as Sibling Panel
- Effort settings panel now opens NEXT to the model menu (not inside it)
- Both panels visible simultaneously
- Smart right-edge overflow detection

### v2.8.3 — Sidebar Button Hover Polish
- Removed permanent border from New Chat
- Equal 2px spacing between all nav buttons
- All buttons show border on hover (transparent default → border on hover)
- Consistent hover styling across nav

### v2.8.4 — Smart Viewport-Aware Menu Positioning
- Rewrote `positionMenu()` for context-menu-like behavior
- **Vertical**: detects space above/below button, opens where more room
- **Horizontal**: model menu left-aligned; effort menu right of model, falls back to left
- Uses explicit pixel coordinates from `getBoundingClientRect()`

### v2.8.5 — Menu Positioning Bug Fix
- Changed `.model-menu` and `.effort-menu` from `position: absolute` to `position: fixed`
- Fixes coordinate mismatch between `getBoundingClientRect()` (viewport) and CSS positioning

### v2.8.6 — Sidebar Footer Removal & Cleanup
- Removed user profile footer from sidebar bottom
- Increased nav icons 16px → 18px
- Removed `border-right` from sidebar (no more white separator line)

### v2.9.0 — Fluid Transitions Throughout UI
- **View cross-fades**: chat/launch/settings fade in/out with opacity + visibility transitions (0.18s)
- **Menu animations**: model/effort menus fade + scale-up entrance (`scale 0.98 → 1`, `translateY 8px → 0`)
- **Modal scale**: `scale(0.96) → scale(1)` on open, backdrop fades at 0.2s
- **Empty state**: fadeIn + slideUp animation
- **Composer focus glow**: subtle ring shadow on focus-within
- **Button micro-interactions**:
  - Icon buttons: `scale(1.05)` hover, `scale(0.95)` press
  - Sidebar nav buttons: `translateX(1px)` nudge on hover
  - Chat items: `translateX(1px)` nudge on hover
- **Topbar New Chat**: fades and scales in/out when sidebar toggles

### v2.9.1 — Raised Composer (Centered When Empty)
- When chat is empty:
  - Composer is **centered vertically** (not at bottom)
  - **Larger**: min-height 64px, bigger padding, 16px font
  - `.chat-empty` class on `#viewChat` triggers raised state
- When first message sent:
  - Composer **smoothly drops** to bottom position
  - Shrinks back to normal (52px min-height, 15px font)
  - 0.5s transition with `cubic-bezier(0.16, 1, 0.3, 1)` springy easing

### v2.9.2 — Bigger, More Centered Composer
- **True vertical centering**: empty-state + composer pair centered as a unit using `margin-top:auto` on empty-state and `margin-bottom:auto` on composer
- **Hide `#messages`** when empty so it doesn't push composer down
- **Larger empty composer**:
  - `max-width` increased 760px → 880px
  - `min-height` increased 64px → 84px
  - `padding` increased to `18px 22px`
  - Font size increased 16px → 17px
- Fixed missing closing brace in `openChat()`

---

## Key Features

### Chat System
- Streaming message rendering with cursor blink
- User/assistant message bubbles with markdown support
- Code blocks with copy button
- Message actions (copy, regenerate, delete)
- Sources panel for web search results
- Draft chat support (unsaved new chats)

### Model Management
- Hardware-based model recommendations (VRAM or 70% RAM)
- Model picker with detailed cards (params, context, size, tags, power gauge)
- Pull models from Ollama library
- Manage (delete) installed models
- Model library with metadata: `power`, `params`, `context`, `tags`, `desc`

### Effort Settings (Submenu)
- **Levels**: Low, Medium (default), High, Max
- Each level has description (speed vs thoroughness tradeoff)
- **Thinking toggle**: Can enable/disable thinking capability
- Stored in config and persisted via `saveConfig` RPC

### Web Search
- Toggleable web search button in composer
- Fetches real-time search results
- Displays sources below assistant response

### Settings
- Dark theme (only; no light mode)
- Temperature slider (0–2)
- Web search toggle
- Cloud/remote endpoint toggle
- Effort level and thinking settings (also in model picker)

### Sidebar
- Collapsible with Ctrl+B shortcut
- Fixed-position toggle button (always visible)
- New Chat, Launch, Settings nav buttons
- Chat history list with context menu (rename, delete)
- "Older" section for past chats

### Modals
- Pull model modal (search + list)
- Manage models modal (delete)
- Settings modal
- All modals have scale + fade entrance animations

---

## Known Patterns & Conventions

### CSS
- All colors via CSS custom properties (`var(--bg)`, `var(--surface)`, etc.)
- `.hidden` utility: `display: none !important;` (only used where transitions not needed)
- For animated show/hide: use `opacity + visibility + transform` with `.active` / `.open` classes
- Transitions generally use `ease` or custom `cubic-bezier(0.16, 1, 0.3, 1)` for springy feel
- `position: fixed` for viewport-positioned elements (menus, modals)

### JavaScript
- `$()` is a simple `document.querySelector()` alias
- State is a single global object: `state`
- All async operations use `await call(method, params)` via Bridge
- Event handlers use `e.stopPropagation()` for nested clickable elements (especially in menus)
- `appendMessageEl` creates DOM elements; messages re-render from scratch on chat switch

### C#
- `Bridge.cs` uses `ConcurrentDictionary<string, Action>` for RPC handlers
- `CancellationTokenSource` must be `Dispose()`d before creating a new one
- `ReplyOk`/`ReplyError` use `_rpcId` (not `id`) for protocol consistency
- `SendInitialState()` is `async void` — must be wrapped in try-catch

---

## File Structure

```
Ollama-2.0/
├── src/Ollama2/
│   ├── App.xaml / .cs
│   ├── MainWindow.xaml / .cs
│   ├── Bridge.cs
│   ├── GlobalUsings.cs
│   ├── NotifyIconHelper.cs
│   ├── Ollama2.csproj
│   ├── app.manifest
│   ├── Properties/PublishProfiles/
│   ├── Services/
│   │   ├── ChatStore.cs
│   │   ├── ConfigService.cs
│   │   ├── HardwareInfo.cs
│   │   ├── Models.cs
│   │   ├── OllamaClient.cs
│   │   ├── UpdateService.cs
│   │   └── WebSearchService.cs
│   ├── Themes/Dark.xaml
│   └── Web/
│       ├── index.html
│       ├── app.js
│       ├── styles.css
│       ├── md.js
│       └── manifest.txt
├── installer/
│   └── setup.iss
├── publish.ps1
├── make-icon.ps1
└── README.md
```

---

## Build & Release Process

1. **Build**: `dotnet build src/Ollama2/Ollama2.csproj -c Release`
2. **Publish**: `dotnet publish ... -p:Version=X.Y.Z -o publish`
3. **Test**: Launch `publish/Ollama2.exe`, verify no crash
4. **Installer**: `ISCC.exe "/DAppVersion=X.Y.Z" installer/setup.iss`
5. **Commit**: `git add -A && git commit -m "..."`
6. **Push**: `git push origin main`
7. **Release**: Create GitHub release via API with tag `vX.Y.Z`
8. **Upload**: Upload `installer/Output/Ollama2Setup-X.Y.Z.exe` as release asset

---

## Critical Bugs Fixed (Lessons Learned)

1. **TDZ Shadowing in RPC**: `const rpcId` inside a loop shadowed outer `let rpcId`, breaking all RPC calls. Fix: rename outer to `_rpcCounter`.

2. **Async Void Exception**: `SendInitialState()` is `async void` (event handler). Unhandled exceptions crashed the app. Fix: wrap in `try-catch`.

3. **CancellationTokenSource Leak**: `_chatCts` and `_pullCts` were replaced without `Dispose()`. Fix: `Dispose()` before assignment.

4. **RPC Protocol Mismatch**: `ReplyOk`/`ReplyError` used `id` parameter but JS expected `_rpcId`. Fix: use `_rpcId` consistently.

5. **Menu Position Coordinate Mismatch**: `getBoundingClientRect()` returns viewport coords, but menus used `position: absolute` (relative to parent). Fix: use `position: fixed`.

6. **Modal Visibility Specificity**: Inline `style="visibility:hidden; opacity:0"` on `#modalRoot` overrode `.visible` class. Fix: add `!important` to `.visible`.

7. **Draft Chat Regeneration**: `regenerate()` didn't handle draft chats. Fix: persist draft before regenerating.

---

## Current State (v2.9.1)

The app is feature-complete and polished. The UI has:
- A minimal, modern sidebar with subtle hover borders
- A centered, raised composer when empty that drops down on first message
- Smooth cross-fades between views
- Animated menus that adapt to viewport edges
- Scale + fade modal entrances
- Micro-interactions on all buttons
- Hardware-aware model recommendations
- Full model management (pull, delete, select)
- Effort settings with thinking toggle
- Web search integration
- System tray integration
- Auto-updater

---

## Future Ideas (Not Implemented)

- Light theme toggle
- Keyboard shortcuts cheat sheet
- Multi-model chat (compare responses)
- Custom system prompts per chat
- Export chat to PDF/markdown
- Voice input/output
- Plugin/extension system

---

*Generated by Devin for Ollama 2.0 development handoff.*
