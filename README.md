# Onyx

A native Windows desktop app that provides a chat UI for Ollama models. It is
**not** Electron — it is a native WPF (.NET 10) binary that uses the OS's
built-in WebView2 to render the chat UI, and it talks to a local `ollama serve`
instance for inference.

> Inference itself is **not** reimplemented. The app connects to a locally
> running Ollama server (`http://localhost:11434` by default) via its REST API.
> Install Ollama from <https://ollama.com> and run `ollama serve` (or just have
> the desktop tray app running) before chatting.

## Features

- **Chat UI** — streaming responses, markdown rendering (headings, lists, task
  lists, blockquotes, tables of links), fenced code blocks with language label
  and copy button, token/time stats per message, copy & regenerate actions.
- **Orchestrator** — semantically understands user intent via LLM classification,
  then routes to the appropriate tools/agents automatically. See the pipeline
  diagram below.
- **Model picker** — dropdown of installed models from `/api/tags`, pull new
  models from the library with live progress, delete models, manage-models view.
- **Built-in web search** — toggle per-message or globally. When on, the app
  searches the web (DuckDuckGo by default, no API key), shows source cards, and
  injects the results into the model's context so it can ground and cite them.
  Brave and Tavily are supported as alternative providers (bring your own key).
- **Sidebar** — chat history with search, new chat, rename, delete; persists to
  `%LOCALAPPDATA%\Onyx\chats.json`.
- **Settings** — General (theme, default model, system prompt, zoom), Model
  (temperature, top-k, top-p, context window), Web Search (provider, key, max
  results), Server (URL + test connection), Startup (launch on login, start
  minimized).
- **System tray** — minimize-to-tray, left-click to show, right-click context
  menu, balloon notifications.
- **Self-contained single-file `.exe`** + **Inno Setup `.exe` installer**.

## How it works — the orchestrator pipeline

Every user message flows through the orchestrator before reaching the LLM. The
orchestrator uses the LLM itself to **semantically understand** the message
(not keyword matching), then decides which tools/agents to invoke.

```
┌─────────┐     ┌──────────────┐     ┌─────────────────┐     ┌──────────┐
│  Input   │────▶│  Orchestrator │────▶│  Tools / Agents  │────▶│  Output   │
│ (user    │     │              │     │                 │     │          │
│  message)│     │ 1. Extract   │     │ • webSearch     │     │ LLM      │
│          │     │    intent    │     │ • codeExecutor  │     │ generates│
│          │     │    (LLM)     │     │ • (future)      │     │ response │
│          │     │              │     │                 │     │ with all │
│          │     │ 2. Plan      │     │ Results injected │     │ context  │
│          │     │    routing   │     │ as context      │     │          │
└─────────┘     └──────────────┘     └─────────────────┘     └──────────┘
                       │                      ▲
                       ▼                      │
              ┌─────────────────┐             │
              │  Intent         │             │
              │  • type         │  plan +     │
              │  • confidence   │──context────┘
              │  • summary      │  blocks
              │  • entities     │
              │  • suggestedTools│
              │  • shouldExecute│
              └─────────────────┘
```

### Step-by-step

1. **Input** — user sends a message in the chat UI.
2. **Intent extraction** — the orchestrator sends a classification prompt to the
   LLM with low temperature (0.1) and small context (2048 tokens). The LLM
   returns structured JSON:
   - `intent` — one of: `chat`, `code`, `webSearch`, `reasoning`, `creative`,
     `summarize`, `translate`, `toolUse`
   - `confidence` — 0.0 to 1.0
   - `summary` — what the user wants
   - `entities` — key topics/technologies detected
   - `language` — detected language code
   - `suggestedTools` — which tools would help answer this
   - `shouldExecuteTools` — whether to auto-run tools before responding
3. **Planning** — the orchestrator decides which tools to run based on the
   extracted intent and tool availability. It also selects an intent-specific
   system prompt to guide the LLM's response style.
4. **Tool execution** — each selected tool runs and produces context (search
   results, code output, API responses, etc). Results are collected as context
   blocks.
5. **Output** — context blocks + the intent-specific system prompt are injected
   into the main LLM conversation. The LLM generates the final response with all
   the gathered context, streamed back to the user.

### Adding new tools/agents

The orchestrator is designed to be extensible. To add a new tool or agent:

```csharp
// 1. Implement the ITool interface
public class MyTool : ITool
{
    public string Name => "myTool";

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        // Do your work here (API call, file op, code execution, etc)
        return new ToolResult { ToolName = Name, Success = true, Output = "result" };
    }
}

// 2. Register it with the orchestrator
AppContext.Current.Orchestrator.Tools.Register(
    new ToolDefinition
    {
        Name = "myTool",
        Description = "What this tool does (used in the LLM classification prompt)",
        Category = "custom",
        Triggers = new() { "keyword1", "keyword2" },
        Enabled = true,
    },
    new MyTool()
);

// 3. Done — the orchestrator will automatically route to it based on intent
```

The tool's description is included in the LLM's classification prompt, so the
intent extractor knows when to suggest it.

## Requirements

- Windows 10/11 (x64)
- WebView2 Runtime (preinstalled on Windows 11; bundled with Edge on Windows 10)
- .NET 10 SDK (only to build — the published `.exe` is self-contained)
- A running Ollama server for inference (`ollama serve` or the Ollama tray app)

## Build & run from source

```powershell
dotnet run --project src\Onyx.Windows\Onyx.Windows.csproj -c Debug
```

## Publish a self-contained single-file `.exe`

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

Produces `publish\Onyx.exe` (~73 MB, no .NET runtime needed on the target).

## Build the `.exe` installer

1. Install [Inno Setup](https://jrsoftware.org/isdl.php).
2. Run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File publish.ps1 -MakeInstaller
   ```

   This produces `installer\Output\OnyxSetup-<version>.exe` — a standard Windows
   installer with Start Menu / desktop / startup shortcuts and a clean
   uninstaller.

## Project layout

```
Onyx-2.0/
├─ Onyx.sln
├─ publish.ps1                  # build .exe (+ optional installer)
├─ installer/setup.iss          # Inno Setup script
├─ src/
│  ├─ Onyx.Core/                # shared library (platform-agnostic)
│  │  ├─ AppContext.cs          # service container / DI
│  │  ├─ Bridge.cs              # JSON message bridge: WebView <-> C# services
│  │  ├─ Orchestrator/          # intent extraction + tool routing
│  │  │  ├─ Models.cs           # Intent, ToolDefinition, OrchestratorPlan
│  │  │  ├─ ITool.cs            # ITool interface + ToolRegistry
│  │  │  ├─ IntentExtractor.cs  # LLM-based semantic classification
│  │  │  ├─ OrchestratorService.cs
│  │  │  └─ WebSearchTool.cs    # built-in tool
│  │  ├─ Services/
│  │  │  ├─ OllamaClient.cs     # REST client for /api/chat /tags /pull /show
│  │  │  ├─ WebSearchService.cs # DuckDuckGo / Brave / Tavily + page fetch
│  │  │  ├─ ConfigService.cs    # persisted settings
│  │  │  ├─ ChatStore.cs        # persisted chat history
│  │  │  └─ Models.cs           # DTOs
│  │  └─ Web/                   # chat UI (embedded into the .exe)
│  │     ├─ index.html  styles.css  md.js  app.js
│  ├─ Onyx.Windows/             # WPF + WebView2 + WinForms(tray)
│  │  ├─ Onyx.Windows.csproj
│  │  ├─ App.xaml(.cs)          # app bootstrap, single-instance, tray
│  │  ├─ MainWindow.xaml(.cs)   # native window, menu bar, WebView2 host
│  │  └─ WindowsStartupRegistration.cs
│  └─ Onyx.Mac/                 # .NET MAUI skeleton (macOS, in progress)
└─ publish/                     # build output (Onyx.exe)
```

## How the web search works

When web search is enabled for a message (or the orchestrator determines the
intent is `webSearch`), the app:

1. Shows a "searching the web…" status in the chat.
2. Runs a search for the user's latest message (DuckDuckGo HTML by default —
   no API key, no tracking).
3. Renders source cards (title, URL, snippet) above the answer.
4. Injects the results as a system message so the model grounds its answer and
   cites sources inline as `[1]`, `[2]`, ….

To use Brave or Tavily instead, open **Settings → Web Search**, pick the
provider, and paste your API key.

## Data location

All user data lives under `%LOCALAPPDATA%\Onyx\`:
- `config.json` — settings
- `chats.json` — chat history
- `web/` — extracted UI cache (regenerated each launch)

## License

See [LICENSE](LICENSE).
