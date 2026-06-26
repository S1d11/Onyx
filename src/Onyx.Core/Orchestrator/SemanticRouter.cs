using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Routes user messages to the best-matching tool using semantic search (embeddings + cosine similarity).
///
/// Instead of keyword matching, this class:
///   1. Pre-computes embedding vectors for each tool's description + example prompts
///   2. Embeds the user's message at runtime
///   3. Computes cosine similarity between the user message and each tool
///   4. Returns the best-matching tool (if above a confidence threshold)
///
/// This is model-agnostic — works with any Ollama model that supports /api/embeddings.
/// For best results, a dedicated embedding model like nomic-embed-text is preferred,
/// but the default chat model works as a fallback.
/// </summary>
public class SemanticRouter
{
    private readonly OllamaClient _ollama;
    private readonly Func<string> _getDefaultModel;
    private readonly ToolRegistry _tools;
    private string? _embeddingModel;
    private bool _initialized;

    // Cached tool embeddings: tool name → embedding vector
    private readonly Dictionary<string, float[]> _toolEmbeddings = new();

    // Rich descriptions for each tool — these get embedded and used for semantic matching.
    // The more descriptive and example-rich, the better the semantic match.
    private static readonly Dictionary<string, string> ToolCorpus = new()
    {
        ["filesystem"] = @"
            Filesystem operations: read, write, create, delete, list, move, copy files and directories on the local computer.
            Examples: 'create a file called hello.txt in my downloads', 'list the files in my documents folder',
            'read the contents of config.json', 'delete the file temp.log from my desktop',
            'make a new folder called projects', 'copy file.txt to my documents', 'move file from downloads to desktop',
            'show me what's in my downloads folder', 'write hello world to a file called greeting.txt'
        ",

        ["system"] = @"
            System and OS operations: run shell commands, execute scripts, manage registry (Windows), environment variables,
            PATH, processes, system information, kill processes, install software, check running processes.
            Examples: 'run a powershell command', 'execute echo hello', 'kill process 1234',
            'set environment variable JAVA_HOME', 'add C:\tools to my PATH', 'show me system info',
            'what processes are running', 'open notepad', 'check my IP address', 'run ipconfig',
            'set a registry key', 'list environment variables', 'show CPU usage'
        ",

        ["gmail"] = @"
            Gmail and email operations: list, read, search, send emails, check inbox, compose messages, reply to emails.
            Examples: 'send an email to john@example.com about the project update', 'list my recent emails',
            'check my inbox', 'search for emails about invoices', 'read my latest email',
            'compose an email to mom about thanksgiving', 'show unread emails', 'find emails from amazon',
            'write an email to my boss requesting time off', 'check if I got any emails today'
        ",

        ["gdrive"] = @"
            Google Drive and cloud file operations: list, search, read, upload, download, manage files in Google Drive.
            Examples: 'list my Google Drive files', 'search for files about quarterly report in my drive',
            'upload a file to Drive called notes.txt', 'read the file called budget.xlsx from my Drive',
            'show me what's in my Google Drive', 'find the document called meeting notes',
            'upload my resume to Google Drive', 'get info about the file called presentation.pptx',
            'search my drive for files containing budget'
        ",

        ["github"] = @"
            GitHub operations: search repositories, list issues, pull requests, commits, read files, create issues,
            search code, get user profiles, browse repos.
            Examples: 'search GitHub repos about llama cpp', 'list issues in S1d11/Onyx',
            'show me the pull requests for microsoft/vscode', 'search for code containing AuthenticateAsync',
            'get the GitHub profile for user torvalds', 'create an issue in my repo about a bug',
            'list recent commits in my repository', 'read the README.md from a repo',
            'find repositories about react components'
        ",

        ["webSearch"] = @"
            Web search: find current information, news, real-time data, look up facts, research topics online.
            Examples: 'search the web for latest news about AI', 'what's the weather today',
            'find information about the new iPhone', 'search for recent papers on transformers',
            'look up the current price of Bitcoin', 'what happened in the news today'
        ",

        ["codeExecutor"] = @"
            Code execution: run code, execute scripts, compile programs, test code.
            Examples: 'run this python script', 'execute this JavaScript code', 'compile this C program',
            'test this function', 'run this SQL query'
        ",
    };

    public SemanticRouter(OllamaClient ollama, Func<string> getDefaultModel, ToolRegistry tools)
    {
        _ollama = ollama;
        _getDefaultModel = getDefaultModel;
        _tools = tools;
    }

    /// <summary>
    /// Initialize the router by computing embeddings for all registered tools.
    /// Call this once at startup (or lazily on first use).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        // Pick the best embedding model: prefer nomic-embed-text if installed, fall back to default
        _embeddingModel = await PickEmbeddingModelAsync(ct);

        if (string.IsNullOrEmpty(_embeddingModel))
        {
            _initialized = true; // Can't initialize without a model — will fall back
            return;
        }

        // Compute embeddings for each registered tool
        foreach (var def in _tools.Definitions)
        {
            if (!def.Enabled) continue;
            var corpus = ToolCorpus.TryGetValue(def.Name, out var c) ? c : def.Description;
            var embedding = await _ollama.EmbedAsync(corpus, _embeddingModel, ct);
            if (embedding != null)
                _toolEmbeddings[def.Name] = embedding;
        }

        _initialized = true;
    }

    /// <summary>
    /// Find the best-matching tool for the given user message using semantic similarity.
    /// Returns (toolName, confidence) or (null, 0) if no good match.
    /// </summary>
    public async Task<(string? toolName, double confidence)> MatchAsync(string userMessage, CancellationToken ct = default)
    {
        if (!_initialized)
            await InitializeAsync(ct);

        if (_toolEmbeddings.Count == 0 || string.IsNullOrEmpty(_embeddingModel))
            return (null, 0);

        var msgEmbedding = await _ollama.EmbedAsync(userMessage, _embeddingModel, ct);
        if (msgEmbedding == null) return (null, 0);

        string? bestTool = null;
        var bestScore = double.MinValue;

        foreach (var (toolName, toolEmb) in _toolEmbeddings)
        {
            var score = CosineSimilarity(msgEmbedding, toolEmb);
            if (score > bestScore)
            {
                bestScore = score;
                bestTool = toolName;
            }
        }

        return (bestTool, bestScore);
    }

    /// <summary>
    /// Find the best-matching tool, but only return it if it's above the confidence threshold
    /// AND the tool is available + connected.
    /// </summary>
    public async Task<string?> MatchToolAsync(string userMessage, double threshold = 0.35, CancellationToken ct = default)
    {
        var (toolName, confidence) = await MatchAsync(userMessage, ct);
        if (string.IsNullOrEmpty(toolName) || confidence < threshold) return null;
        if (!_tools.IsAvailable(toolName)) return null;
        return toolName;
    }

    /// <summary>Pick the best available embedding model.</summary>
    private async Task<string?> PickEmbeddingModelAsync(CancellationToken ct)
    {
        // Prefer dedicated embedding models
        var embeddingModels = new[] { "nomic-embed-text", "mxbai-embed-large", "all-minilm", "bge-m3" };
        foreach (var m in embeddingModels)
        {
            if (await _ollama.IsModelInstalledAsync(m, ct))
                return m;
        }

        // Fall back to the default chat model (Ollama supports embeddings with any model)
        var defaultModel = _getDefaultModel();
        return string.IsNullOrEmpty(defaultModel) ? null : defaultModel;
    }

    /// <summary>Compute cosine similarity between two vectors.</summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    /// <summary>Force re-initialization (e.g. after tools are registered or model changes).</summary>
    public void Reset() => _initialized = false;
}
