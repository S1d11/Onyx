# Ollama (1:1 desktop clone)

A native Windows desktop app that recreates the Ollama app one-to-one: the chat
interface, model library, settings, all menus, system-tray behavior, and the
built-in web search. It is **not** Electron — it is a native WPF (.NET 10)
binary that uses the OS's built-in WebView2 to render the chat UI, and it talks
to a local `ollama serve` instance for inference, exactly like the real desktop
app.

> Inference itself is **not** reimplemented. The app connects to a locally
> running Ollama server (`http://localhost:11434` by default) via its REST API.
> Install Ollama from <https://ollama.com> and run `ollama serve` (or just have
> the desktop tray app running) before chatting.

## Features

- **Chat UI** — streaming responses, markdown rendering (headings, lists, task
  lists, blockquotes, tables of links), fenced code blocks with language label
  and copy button, token/time stats per message, copy & regenerate actions.
- **Model picker** — dropdown of installed models from `/api/tags`, pull new
  models from the library with live progress, delete models, manage-models view.
- **Built-in web search** — toggle per-message or globally. When on, the app
  searches the web (DuckDuckGo by default, no API key), shows source cards, and
  injects the results into the model's context so it can ground and cite them —
  replicating Ollama's web-search UX. Brave and Tavily are supported as
  alternative providers (bring your own key).
- **Sidebar** — chat history with search, new chat, rename, delete; persists to
  `%LOCALAPPDATA%\Ollama2\chats.json`.
- **Settings** — General (theme, default model, system prompt, zoom), Model
  (temperature, top-k, top-p, context window), Web Search (provider, key, max
  results), Server (URL + test connection).
- **Native menus** — File (New Chat/Window, Open/Export/Import, Exit), Edit
  (Undo/Redo/Cut/Copy/Paste/Select All, Clear/Delete chat), View (Sidebar,
  Zoom, Theme, Reload, DevTools), Model (Pull/Delete/Refresh/Manage), Settings
  (Preferences, Server), Help (Docs, Library, Shortcuts, Updates, About).
- **System tray** — minimize-to-tray, left-click to show, right-click context
  menu (New Chat, Models, Settings, Show, Quit), balloon notifications.
- **Keyboard shortcuts** — Ctrl+N, Ctrl+B, Ctrl+O, Ctrl+,, Enter, Shift+Enter,
  Esc, Ctrl++/−/0, Ctrl+R, F12.
- **Self-contained single-file `.exe`** + **Inno Setup `.exe` installer**.

## Requirements

- Windows 10/11 (x64)
- WebView2 Runtime (preinstalled on Windows 11; bundled with Edge on Windows 10)
- .NET 10 SDK (only to build — the published `.exe` is self-contained)
- A running Ollama server for inference (`ollama serve` or the Ollama tray app)

## Build & run from source

```powershell
dotnet run --project src\Ollama2\Ollama2.csproj -c Debug
```

## Publish a self-contained single-file `.exe`

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

Produces `publish\Ollama.exe` (~73 MB, no .NET runtime needed on the target).

## Build the `.exe` installer

1. Install [Inno Setup](https://jrsoftware.org/isdl.php).
2. Run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File publish.ps1 -MakeInstaller
   ```

   This produces `installer\Output\OllamaSetup-1.0.0.exe` — a standard Windows
   installer with Start Menu / desktop / startup shortcuts and a clean
   uninstaller.

## Project layout

```
Ollama-2.0/
├─ Ollama2.sln
├─ publish.ps1                  # build .exe (+ optional installer)
├─ make-icon.ps1                # regenerates src/Ollama2/app.ico
├─ installer/setup.iss          # Inno Setup script
├─ src/Ollama2/
│  ├─ Ollama2.csproj            # WPF + WebView2 + WinForms(tray)
│  ├─ App.xaml(.cs)             # app bootstrap, DI of services
│  ├─ MainWindow.xaml(.cs)      # native window, menu bar, tray, WebView2 host
│  ├─ NotifyIconHelper.cs       # native Win32 system-tray icon
│  ├─ Bridge.cs                 # JSON message bridge: WebView2 <-> C# services
│  ├─ GlobalUsings.cs           # WPF/WinForms type-alias resolution
│  ├─ app.manifest / app.ico
│  ├─ Themes/Dark.xaml
│  ├─ Services/
│  │  ├─ OllamaClient.cs        # REST client for /api/chat /tags /pull /show /delete
│  │  ├─ WebSearchService.cs    # DuckDuckGo / Brave / Tavily + page fetch
│  │  ├─ ConfigService.cs       # persisted settings
│  │  ├─ ChatStore.cs           # persisted chat history
│  │  └─ Models.cs              # DTOs
│  └─ Web/                      # chat UI (embedded into the .exe)
│     ├─ index.html  styles.css  md.js  app.js  manifest.txt
└─ publish/                     # build output (Ollama.exe)
```

## How the web search works

When web search is enabled for a message, the app:

1. Shows a "searching the web…" status in the chat.
2. Runs a search for the user's latest message (DuckDuckGo HTML by default —
   no API key, no tracking, works offline-of-Ollama).
3. Renders source cards (title, URL, snippet) above the answer.
4. Injects the results as a system message so the model grounds its answer and
   cites sources inline as `[1]`, `[2]`, … — matching Ollama's behavior.

To use Brave or Tavily instead, open **Settings → Web Search**, pick the
provider, and paste your API key.

## Data location

All user data lives under `%LOCALAPPDATA%\Ollama2\`:
- `config.json` — settings
- `chats.json` — chat history
- `web/` — extracted UI cache (regenerated each launch)

## License

See [LICENSE](LICENSE).
