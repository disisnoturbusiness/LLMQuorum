using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Providers;

/// <summary>
/// Asks Claude through the Claude Code CLI in print mode, authenticated by the
/// user's Claude subscription login instead of an API key.
///
/// Why a process rather than HTTP: the Claude API has no free tier and is billed
/// from prepaid credits, while the subscription already covers personal tools
/// that call Claude through the CLI. Usage draws from the plan's limits, not a card.
///
/// Isolation is the whole point of a panel seat, so the process is launched to
/// see the question and nothing else it can avoid seeing:
///   * working directory is an empty sandbox, so no project auto-memory loads
///     (MEASURED: the project memory for C:\Temp\ForClaude holds the withholding
///     answer keys, and a seat that reads them is not an independent vote)
///   * --system-prompt replaces the coding-agent prompt with a neutral one
///   * --tools "" and --strict-mcp-config remove every tool, so it cannot look
///     anything up or read files
///   * --max-turns 1 and --no-session-persistence keep each call stateless
///   * --safe-mode keeps the user-level CLAUDE.md out (MEASURED 2026-09-16: without it
///     the seat received the owner's style rules, including "label confidence ...
///     unverified", and sonnet answered "unverified - not confident enough"; with it the
///     seat reports only the neutral system prompt; subscription auth still works)
/// </summary>
public sealed class ClaudeCliProvider : IQuorumProvider
{
    #region Data Members

    /// <summary>Endpoint value meaning "find the newest CLI the desktop app installed".</summary>
    private const string AUTO_ENDPOINT = "auto";

    /// <summary>Neutral replacement for the coding-agent system prompt.</summary>
    private const string SYSTEM_PROMPT = "Answer the user's question directly and factually from your own knowledge.";

    /// <summary>
    /// System prompt for a web-search seat. MEASURED 2026-09-16: this seat was the only one of 52
    /// to return the current Georgia G-4 statuses, so its wording is kept exactly.
    /// </summary>
    private const string WEB_SYSTEM_PROMPT = "Answer factually. Check the current official source on the web before answering. Output only the answer.";

    /// <summary>Directory the desktop app installs versioned CLI builds into, under %APPDATA%.</summary>
    private const string CLI_INSTALL_SUBDIR = @"Claude\claude-code";

    /// <summary>Error text longer than this is clipped before storage.</summary>
    private const int MAX_ERROR_LENGTH = 1900;

    private readonly ProviderDefinition _definition;
    private readonly string _configDirectory;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the CLI-backed seat.</summary>
    /// <param name="definition">Provider configuration; Endpoint is the CLI path or "auto".</param>
    /// <param name="configDirectory">Directory relative WorkingDirectory values resolve against.</param>
    public ClaudeCliProvider( ProviderDefinition definition, string configDirectory )
    {
        _definition = definition;
        _configDirectory = configDirectory;
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc />
    public ProviderDefinition Definition => _definition;

    /// <inheritdoc />
    public async Task<ProviderAnswer> AskAsync( string prompt, int rank, CancellationToken cancellationToken = default )
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var startInfo = BuildStartInfo( prompt );
            var (exitCode, stdout, stderr, timedOut) = await RunProcessAsync( startInfo, cancellationToken );
            stopwatch.Stop();

            if( timedOut )
            {
                return BuildFailure( _definition, rank, (int)stopwatch.ElapsedMilliseconds, CallErrorClass.Timeout,
                                     $"No response within {_definition.TimeoutSeconds}s.", stdout );
            }

            return ParseCliOutput( _definition, rank, (int)stopwatch.ElapsedMilliseconds, exitCode, stdout, stderr );
        }
        catch( Exception ex ) when( ex is System.ComponentModel.Win32Exception or FileNotFoundException
                                          or InvalidOperationException or DirectoryNotFoundException )
        {
            stopwatch.Stop();
            return BuildFailure( _definition, rank, (int)stopwatch.ElapsedMilliseconds, CallErrorClass.Other,
                                 Clip( $"Could not start Claude CLI: {ex.Message}" ), null );
        }
    }

    /// <summary>
    /// Turns the CLI's JSON result into a panel answer. Public so the parsing
    /// rules are testable without launching a process.
    /// The CLI reports most failures as exit code 0 with is_error true and the
    /// reason in "result", e.g. "Failed to authenticate: OAuth session expired".
    /// Treating that text as an answer would put an error message in a cluster.
    /// </summary>
    /// <param name="definition">Seat that produced the output.</param>
    /// <param name="rank">Consultation order.</param>
    /// <param name="latencyMs">Wall-clock time including process start.</param>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="stdout">Raw standard output, preserved as the record.</param>
    /// <param name="stderr">Standard error, used only for diagnostics.</param>
    /// <returns>A success record, or a bucketed failure carrying the raw bytes.</returns>
    public static ProviderAnswer ParseCliOutput(
        ProviderDefinition definition, int rank, int latencyMs, int exitCode, byte[] stdout, string stderr )
    {
        try
        {
            using var document = JsonDocument.Parse( stdout );
            var root = document.RootElement;

            var isError = root.TryGetProperty( "is_error", out var errorFlag ) && errorFlag.ValueKind == JsonValueKind.True;
            var text = root.TryGetProperty( "result", out var result ) ? result.GetString() : null;

            if( isError || exitCode != 0 )
            {
                // MEASURED 2026-09-16: a web-search seat that ran out of turns returned no "result",
                // subtype "error_max_turns" and the reason only in an "errors" array. Reading "result"
                // alone stored a blank reason and a generic error for what is really a limit hit.
                var subtype = root.TryGetProperty( "subtype", out var st ) ? st.GetString() : null;
                var errors = root.TryGetProperty( "errors", out var errs ) && errs.ValueKind == JsonValueKind.Array
                    ? string.Join( "; ", errs.EnumerateArray().Select( e => e.ToString() ) )
                    : null;

                var reason = !string.IsNullOrWhiteSpace( text ) ? text
                    : !string.IsNullOrWhiteSpace( errors ) ? $"{subtype}: {errors}"
                    : !string.IsNullOrWhiteSpace( subtype ) ? subtype
                    : stderr;

                var errorClass = subtype == "error_max_turns" ? CallErrorClass.Truncated : ClassifyError( reason );
                return BuildFailure( definition, rank, latencyMs, errorClass, Clip( reason ), stdout );
            }

            if( string.IsNullOrWhiteSpace( text ) )
            {
                return BuildFailure( definition, rank, latencyMs, CallErrorClass.Empty, "CLI returned no result text.", stdout );
            }

            var (promptTokens, completionTokens) = ReadUsage( root );

            return new ProviderAnswer
            {
                Provider = definition,
                Rank = rank,
                IsSuccess = true,
                AnswerText = text.Trim(),
                ResponseBytes = stdout,
                LatencyMs = latencyMs,
                FinishReason = root.TryGetProperty( "stop_reason", out var stop ) ? stop.GetString() : null,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };
        }
        catch( JsonException )
        {
            var reason = stdout.Length > 0 ? Encoding.UTF8.GetString( stdout ) : stderr;
            return BuildFailure( definition, rank, latencyMs, ClassifyError( reason ), Clip( reason ), stdout );
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Builds the isolated print-mode invocation. ArgumentList is used rather than
    /// a joined string so the empty --tools value survives intact; a quoted string
    /// is exactly where PowerShell testing dropped it and the CLI rejected the call.
    /// </summary>
    /// <param name="prompt">Question text passed verbatim.</param>
    /// <returns>Configured start info.</returns>
    private ProcessStartInfo BuildStartInfo( string prompt )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(),
            WorkingDirectory = ResolveWorkingDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var tools = _definition.CliTools ?? string.Empty;
        var systemPrompt = tools.Length == 0 ? SYSTEM_PROMPT : WEB_SYSTEM_PROMPT;

        foreach( var argument in new[]
        {
            "-p", prompt,
            "--output-format", "json",
            "--model", _definition.ModelId,
            "--max-turns", Math.Max( 1, _definition.CliMaxTurns ).ToString( System.Globalization.CultureInfo.InvariantCulture ),
            "--no-session-persistence",
            "--strict-mcp-config",
            "--safe-mode",
            "--system-prompt", systemPrompt,
            "--tools", tools
        } )
        {
            startInfo.ArgumentList.Add( argument );
        }

        // Print mode cannot prompt for tool permission, so allowed tools must be pre-approved.
        if( tools.Length > 0 )
        {
            startInfo.ArgumentList.Add( "--allowedTools" );
            startInfo.ArgumentList.Add( tools );
        }

        return startInfo;
    }

    /// <summary>
    /// Runs the process to completion or timeout. Stdin is closed immediately so
    /// the CLI never waits on input, and the whole process tree is killed on
    /// timeout so an orphaned CLI cannot keep drawing on the subscription.
    /// </summary>
    /// <param name="startInfo">Prepared invocation.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>Exit code, raw stdout, stderr text, and whether it timed out.</returns>
    private async Task<(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut)> RunProcessAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken )
    {
        using var process = Process.Start( startInfo )
                            ?? throw new InvalidOperationException( "Process.Start returned null." );

        process.StandardInput.Close();

        using var output = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync( output, cancellationToken );
        var stderrTask = process.StandardError.ReadToEndAsync( cancellationToken );

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( TimeSpan.FromSeconds( _definition.TimeoutSeconds ) );

        try
        {
            await process.WaitForExitAsync( timeout.Token );
        }
        catch( OperationCanceledException ) when( !cancellationToken.IsCancellationRequested )
        {
            process.Kill( entireProcessTree: true );
            return (-1, output.ToArray(), string.Empty, true);
        }

        await Task.WhenAll( stdoutTask, stderrTask );
        return (process.ExitCode, output.ToArray(), await stderrTask, false);
    }

    /// <summary>
    /// Resolves the CLI binary. "auto" picks the highest versioned build under
    /// %APPDATA%\Claude\claude-code, because the desktop app installs each update
    /// into a new folder and a hardcoded version would break on the next update.
    /// MEASURED: a stale copy on PATH was 2.1.78 while the app ran 2.1.271.
    /// </summary>
    /// <returns>Full path to claude.exe.</returns>
    private string ResolveExecutable()
    {
        if( !string.IsNullOrWhiteSpace( _definition.Endpoint ) &&
            !string.Equals( _definition.Endpoint, AUTO_ENDPOINT, StringComparison.OrdinalIgnoreCase ) )
        {
            return _definition.Endpoint;
        }

        var root = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ), CLI_INSTALL_SUBDIR );

        var newest = Directory.Exists( root )
            ? Directory.GetDirectories( root )
                .Select( dir => ( Dir: dir, Parsed: Version.TryParse( Path.GetFileName( dir ), out var v ) ? v : null ) )
                .Where( x => x.Parsed is not null && File.Exists( Path.Combine( x.Dir, "claude.exe" ) ) )
                .OrderByDescending( x => x.Parsed )
                .Select( x => Path.Combine( x.Dir, "claude.exe" ) )
                .FirstOrDefault()
            : null;

        return newest ?? throw new FileNotFoundException( $"No Claude CLI build found under '{root}'." );
    }

    /// <summary>Resolves and creates the sandbox working directory.</summary>
    /// <returns>Absolute path to an existing directory.</returns>
    private string ResolveWorkingDirectory()
    {
        var configured = string.IsNullOrWhiteSpace( _definition.WorkingDirectory )
            ? "seat-sandbox"
            : _definition.WorkingDirectory;

        var full = Path.IsPathRooted( configured )
            ? configured
            : Path.GetFullPath( Path.Combine( _configDirectory, configured ) );

        Directory.CreateDirectory( full );
        return full;
    }

    /// <summary>Reads token usage, counting cached prompt tokens as prompt tokens.</summary>
    /// <param name="root">Root of the CLI JSON result.</param>
    /// <returns>Prompt and completion token counts, null when absent.</returns>
    private static (int? Prompt, int? Completion) ReadUsage( JsonElement root )
    {
        if( !root.TryGetProperty( "usage", out var usage ) )
        {
            return (null, null);
        }

        static int Read( JsonElement element, string name ) =>
            element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

        var prompt = Read( usage, "input_tokens" ) + Read( usage, "cache_read_input_tokens" ) +
                     Read( usage, "cache_creation_input_tokens" );

        return (prompt, Read( usage, "output_tokens" ));
    }

    /// <summary>Buckets CLI failure text the same way HTTP failures are bucketed.</summary>
    /// <param name="reason">Error text from the result field or stderr.</param>
    /// <returns>Failure bucket.</returns>
    private static CallErrorClass ClassifyError( string? reason )
    {
        var text = ( reason ?? string.Empty ).ToLowerInvariant();

        if( text.Contains( "authenticate" ) || text.Contains( "oauth" ) || text.Contains( "log in" ) || text.Contains( "login" ) )
        {
            return CallErrorClass.Auth;
        }

        if( text.Contains( "usage limit" ) || text.Contains( "rate limit" ) || text.Contains( "limit reached" ) || text.Contains( "429" ) )
        {
            return CallErrorClass.RateLimit;
        }

        return text.Contains( "overloaded" ) || text.Contains( "529" ) || text.Contains( "capacity" )
            ? CallErrorClass.Capacity
            : CallErrorClass.Other;
    }

    /// <summary>Builds a failure record.</summary>
    /// <param name="definition">Seat that failed.</param>
    /// <param name="rank">Consultation order.</param>
    /// <param name="latencyMs">Elapsed time.</param>
    /// <param name="errorClass">Failure bucket.</param>
    /// <param name="errorText">Diagnostic detail.</param>
    /// <param name="bytes">Raw output if any.</param>
    /// <returns>A ProviderAnswer with IsSuccess false.</returns>
    private static ProviderAnswer BuildFailure(
        ProviderDefinition definition, int rank, int latencyMs, CallErrorClass errorClass, string errorText, byte[]? bytes )
    {
        return new ProviderAnswer
        {
            Provider = definition,
            Rank = rank,
            IsSuccess = false,
            ResponseBytes = bytes is { Length: > 0 } ? bytes : null,
            LatencyMs = latencyMs,
            ErrorClass = errorClass,
            ErrorText = errorText
        };
    }

    /// <summary>Clips diagnostic text to the storage limit.</summary>
    /// <param name="value">Raw text.</param>
    /// <returns>Clipped text.</returns>
    private static string Clip( string? value )
    {
        value ??= string.Empty;
        return value.Length <= MAX_ERROR_LENGTH ? value : value[..MAX_ERROR_LENGTH];
    }

    #endregion Private Methods
}
