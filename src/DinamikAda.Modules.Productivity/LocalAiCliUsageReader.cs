using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DinamikAda.Modules.Productivity;

public sealed record LocalAiCliPaths(
    string CodexRoot,
    string ClaudeConfigJsonPath,
    string ClaudeProjectsRoot,
    string GeminiAccountsJsonPath,
    string GeminiTmpRoot)
{
    public static LocalAiCliPaths CreateDefault()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LocalAiCliPaths(
            CodexRoot: Path.Combine(home, ".codex"),
            ClaudeConfigJsonPath: Path.Combine(home, ".claude.json"),
            ClaudeProjectsRoot: Path.Combine(home, ".claude", "projects"),
            GeminiAccountsJsonPath: Path.Combine(home, ".gemini", "google_accounts.json"),
            GeminiTmpRoot: Path.Combine(home, ".gemini", "tmp"));
    }

    public static string GetIsolatedProfilesRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DinamikAda",
            "ai_profiles");
}

public sealed record LocalCodexTelemetry(
    bool Detected,
    bool IsAuthenticated,
    string? AccountEmail,
    string? PlanTier,
    string? ActiveModel,
    double? PrimaryUsedPercent,
    int? PrimaryWindowMinutes,
    DateTimeOffset? PrimaryResetsAtUtc,
    double? SecondaryUsedPercent,
    int? SecondaryWindowMinutes,
    DateTimeOffset? SecondaryResetsAtUtc,
    int AvailableResetCredits,
    long LastSessionTotalTokens,
    DateTimeOffset? LastEventUtc,
    bool HasLiveQuota = false,
    bool AccountReadSucceeded = false);

public sealed record LocalClaudeTelemetry(
    bool Detected,
    bool IsAuthenticated,
    string? AccountEmail,
    string? DisplayName,
    string? PlanTier,
    int TurnsLast5Hours,
    long TokensLast5Hours,
    DateTimeOffset? Next5HourResetUtc,
    int TurnsLast7Days,
    long TokensLast7Days,
    double? SessionUsedPercent = null,
    DateTimeOffset? SessionQuotaResetUtc = null,
    double? WeeklyUsedPercent = null,
    DateTimeOffset? WeeklyQuotaResetUtc = null)
{
    public bool HasReportedQuota => SessionUsedPercent.HasValue || WeeklyUsedPercent.HasValue;
}

public sealed record LocalGeminiTelemetry(
    bool Detected,
    bool IsAuthenticated,
    string? ActiveAccountEmail,
    int TurnsLast24Hours,
    DateTimeOffset? Next24HourResetUtc);

public sealed record LocalAntigravityBucket(
    string BucketKey,
    string DisplayName,
    string? ModelsSummary,
    double WeeklyRemainingPercent,
    DateTimeOffset? WeeklyResetUtc,
    double? FiveHourRemainingPercent,
    DateTimeOffset? FiveHourResetUtc,
    string AccentHex);

public sealed record LocalAntigravityTelemetry(
    bool Detected,
    bool IsAuthenticated,
    string? AccountEmail,
    string? PlanTier,
    string? ActiveModel,
    int TurnsLast5Hours,
    int TurnsLast7Days,
    DateTimeOffset? Next5HourResetUtc,
    System.Collections.Immutable.ImmutableArray<LocalAntigravityBucket> ModelBuckets = default,
    bool HasLiveQuota = false);

public sealed record LiveApiAccountTelemetry(
    bool Success,
    string? AccountLabel,
    string? PlanTier,
    double? TotalQuota,
    double? UsedQuota,
    string QuotaUnit,
    string? SecondarySummary,
    string? ErrorMessage);

public sealed record LocalAiCliUsageSnapshot(
    DateTimeOffset CapturedAtUtc,
    LocalCodexTelemetry Codex,
    LocalClaudeTelemetry Claude,
    LocalGeminiTelemetry Gemini);

/// <summary>
/// Connects to official local AI CLI/app-server protocols (Codex app-server JSON-RPC, Claude Code CLI, Gemini CLI)
/// and official REST APIs using isolated per-account profile directories or DPAPI-encrypted user API keys.
/// </summary>
public static class LocalAiCliUsageReader
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static LocalAiCliUsageSnapshot ReadAll(LocalAiCliPaths? paths = null, DateTimeOffset? nowUtc = null)
    {
        var p = paths ?? LocalAiCliPaths.CreateDefault();
        var utc = nowUtc ?? DateTimeOffset.UtcNow;

        return new LocalAiCliUsageSnapshot(
            CapturedAtUtc: utc,
            Codex: ReadCodex(p.CodexRoot, utc),
            Claude: ReadClaude(p.ClaudeConfigJsonPath, p.ClaudeProjectsRoot, utc),
            Gemini: ReadGemini(p.GeminiAccountsJsonPath, p.GeminiTmpRoot, utc));
    }

    public static string? ResolveCodexExecutablePath()
    {
        try
        {
            using var packagesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packagesKey is not null)
            {
                foreach (string subName in packagesKey.GetSubKeyNames()
                    .Where(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    using var sub = packagesKey.OpenSubKey(subName);
                    string? root = sub?.GetValue("PackageRootFolder") as string;
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        string exe = Path.Combine(root, "app", "resources", "codex.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
        }
        catch
        {
            // Ignore registry lookup errors.
        }

        try
        {
            string localWa = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "codex.exe");
            if (File.Exists(localWa)) return localWa;

            string winApps = @"C:\Program Files\WindowsApps";
            if (Directory.Exists(winApps))
            {
                var candidates = Directory.GetDirectories(winApps, "OpenAI.Codex*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "app", "resources", "codex.exe"))
                    .Where(File.Exists)
                    .ToList();
                if (candidates.Count > 0) return candidates[0];
            }
        }
        catch
        {
            // Ignore directory enumeration permission errors.
        }
        return null;
    }

    public static string? ResolveClaudeExecutablePath()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localExe = Path.Combine(home, ".local", "bin", "claude.exe");
            if (File.Exists(localExe)) return localExe;

            string? nodeRoot = Environment.GetEnvironmentVariable("FNM_MULTISHELL_PATH");
            if (!string.IsNullOrWhiteSpace(nodeRoot))
            {
                string activeExe = Path.Combine(nodeRoot, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                if (File.Exists(activeExe)) return activeExe;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string nativeExe = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (File.Exists(nativeExe)) return nativeExe;

            string npmCmd = Path.Combine(appData, "npm", "claude.cmd");
            if (File.Exists(npmCmd)) return npmCmd;
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    public static string? ResolveAntigravityExecutablePath()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string agyExe = Path.Combine(home, ".gemini", "bin", "agy.exe");
            if (File.Exists(agyExe)) return agyExe;
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    public static async Task<LocalCodexTelemetry> QueryCodexAccountLiveAsync(
        string? codexHomeDir = null,
        DateTimeOffset? nowUtc = null,
        CancellationToken ct = default)
    {
        var utc = nowUtc ?? DateTimeOffset.UtcNow;
        string defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string effectiveHome = string.IsNullOrWhiteSpace(codexHomeDir) || string.Equals(codexHomeDir, "default", StringComparison.OrdinalIgnoreCase)
            ? defaultHome
            : codexHomeDir;

        var fileFallback = ReadCodex(effectiveHome, utc);
        string? codexExe = ResolveCodexExecutablePath();
        if (string.IsNullOrWhiteSpace(codexExe) || !File.Exists(codexExe))
        {
            return fileFallback;
        }

        Process? proc = null;
        try
        {
            Directory.CreateDirectory(effectiveHome);
            var psi = CodexProfileProcess(codexExe, effectiveHome, defaultHome);
            proc = Process.Start(psi);
            if (proc is null) return fileFallback;
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6.5));
            var token = timeoutCts.Token;

            await SendJsonRpcAsync(proc, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { clientInfo = new { name = "dinamikada", version = "1.0.0" } }
            }, token).ConfigureAwait(false);

            bool gotAccount = false;
            bool gotLimits = false;
            bool receivedQuota = false;
            bool accountReadSucceeded = false;
            bool isAuthenticated = fileFallback.IsAuthenticated;
            string? email = fileFallback.AccountEmail;
            string? plan = fileFallback.PlanTier;
            double? priUsed = fileFallback.PrimaryUsedPercent;
            int? priWin = fileFallback.PrimaryWindowMinutes;
            DateTimeOffset? priReset = fileFallback.PrimaryResetsAtUtc;
            double? secUsed = fileFallback.SecondaryUsedPercent;
            int? secWin = fileFallback.SecondaryWindowMinutes;
            DateTimeOffset? secReset = fileFallback.SecondaryResetsAtUtc;
            int resetCredits = 0;

            while (!token.IsCancellationRequested && (!gotAccount || !gotLimits))
            {
                string? line;
                try { line = await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (accountReadSucceeded && !ct.IsCancellationRequested) { break; }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) continue;

                    if (id == 1)
                    {
                        await SendJsonRpcAsync(proc, new { jsonrpc = "2.0", method = "initialized", @params = new { } }, token).ConfigureAwait(false);
                        await SendJsonRpcAsync(proc, new { jsonrpc = "2.0", id = 2, method = "account/read", @params = new { refreshToken = false } }, token).ConfigureAwait(false);
                        await SendJsonRpcAsync(proc, new { jsonrpc = "2.0", id = 3, method = "account/rateLimits/read", @params = new { excludeResetCreditDetails = false } }, token).ConfigureAwait(false);
                    }
                    else if (id == 2)
                    {
                        gotAccount = true;
                        if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                        {
                            accountReadSucceeded = true;
                            if (res.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
                            {
                                isAuthenticated = true;
                                if (acc.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String)
                                    email = emEl.GetString();
                                if (acc.TryGetProperty("planType", out var plEl) && plEl.ValueKind == JsonValueKind.String)
                                    plan = FormatPlanName(plEl.GetString());
                            }
                            else
                            {
                                isAuthenticated = false;
                            }
                        }
                    }
                    else if (id == 3)
                    {
                        gotLimits = true;
                        if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                        {
                            if (res.TryGetProperty("rateLimitResetCredits", out var rrc) && rrc.ValueKind == JsonValueKind.Object &&
                                rrc.TryGetProperty("availableCount", out var acEl) && acEl.TryGetInt32(out int ac))
                            {
                                resetCredits = ac;
                            }

                            if (res.TryGetProperty("rateLimits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                            {
                                if (rl.TryGetProperty("planType", out var plEl) && plEl.ValueKind == JsonValueKind.String)
                                    plan = FormatPlanName(plEl.GetString()) ?? plan;

                                (priUsed, priWin, priReset) = rl.TryGetProperty("primary", out var pri) && pri.ValueKind == JsonValueKind.Object
                                    ? ParseAppServerRateWindow(pri)
                                    : (null, null, null);
                                (secUsed, secWin, secReset) = rl.TryGetProperty("secondary", out var sec) && sec.ValueKind == JsonValueKind.Object
                                    ? ParseAppServerRateWindow(sec)
                                    : (null, null, null);
                                // Some plans return only one window in the primary field.
                                if (secUsed is null && priWin == 7 * 24 * 60)
                                {
                                    (secUsed, secWin, secReset) = (priUsed, priWin, priReset);
                                    (priUsed, priWin, priReset) = (null, null, null);
                                }
                                receivedQuota = priUsed.HasValue || secUsed.HasValue;
                            }
                        }
                    }
                }
                catch
                {
                    // Continue reading JSON-RPC lines.
                }
            }

            return new LocalCodexTelemetry(
                Detected: true,
                IsAuthenticated: isAuthenticated,
                AccountEmail: email,
                PlanTier: plan,
                ActiveModel: fileFallback.ActiveModel,
                PrimaryUsedPercent: priUsed,
                PrimaryWindowMinutes: priWin,
                PrimaryResetsAtUtc: priReset,
                SecondaryUsedPercent: secUsed,
                SecondaryWindowMinutes: secWin,
                SecondaryResetsAtUtc: secReset,
                AvailableResetCredits: resetCredits,
                LastSessionTotalTokens: fileFallback.LastSessionTotalTokens,
                LastEventUtc: receivedQuota ? utc : fileFallback.LastEventUtc,
                HasLiveQuota: receivedQuota && accountReadSucceeded && isAuthenticated && !string.IsNullOrWhiteSpace(email),
                AccountReadSucceeded: accountReadSucceeded);
        }
        catch
        {
            return fileFallback;
        }
        finally
        {
            TryKillProcess(proc);
        }
    }

    private static ProcessStartInfo CodexProfileProcess(string executable, string profile, string defaultHome)
    {
        var psi = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.Equals(profile, defaultHome, StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        }
        psi.ArgumentList.Add("app-server");
        psi.Environment["CODEX_HOME"] = profile;
        psi.Environment.Remove("CODEX_API_KEY");
        psi.Environment.Remove("OPENAI_API_KEY");
        psi.Environment.Remove("CODEX_AUTH_TOKEN");
        return psi;
    }

    public static async Task<LocalCodexTelemetry?> RunCodexOAuthLoginAsync(
        string codexHomeDir,
        Action<string>? onAuthUrlOpened = null,
        CancellationToken ct = default)
    {
        string? codexExe = ResolveCodexExecutablePath();
        if (string.IsNullOrWhiteSpace(codexExe) || !File.Exists(codexExe))
        {
            return null;
        }

        string defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string effectiveHome = string.IsNullOrWhiteSpace(codexHomeDir) || string.Equals(codexHomeDir, "default", StringComparison.OrdinalIgnoreCase)
            ? defaultHome
            : codexHomeDir;

        Process? proc = null;
        try
        {
            Directory.CreateDirectory(effectiveHome);
            var psi = CodexProfileProcess(codexExe, effectiveHome, defaultHome);
            proc = Process.Start(psi);
            if (proc is null) return null;
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            var token = timeoutCts.Token;

            await SendJsonRpcAsync(proc, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { clientInfo = new { name = "dinamikada", version = "1.0.0" } }
            }, token).ConfigureAwait(false);

            bool loginCompleted = false;
            string? loginId = null;
            while (!token.IsCancellationRequested && !loginCompleted)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int id))
                    {
                        if (id == 1)
                        {
                            await SendJsonRpcAsync(proc, new { jsonrpc = "2.0", method = "initialized", @params = new { } }, token).ConfigureAwait(false);
                            await SendJsonRpcAsync(proc, new
                            {
                                jsonrpc = "2.0",
                                id = 10,
                                method = "account/login/start",
                                @params = new { type = "chatgpt" }
                            }, token).ConfigureAwait(false);
                        }
                        else if (id == 10)
                        {
                            if (!root.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object ||
                                !res.TryGetProperty("loginId", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                                !res.TryGetProperty("authUrl", out var url) || url.ValueKind != JsonValueKind.String ||
                                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var authUrl) ||
                                authUrl.Scheme != Uri.UriSchemeHttps ||
                                authUrl.Host is not ("auth.openai.com" or "chatgpt.com")) break;
                            loginId = idValue.GetString();
                            if (string.IsNullOrWhiteSpace(loginId)) break;
                            Process.Start(new ProcessStartInfo(authUrl.AbsoluteUri) { UseShellExecute = true });
                            onAuthUrlOpened?.Invoke(authUrl.AbsoluteUri);
                        }
                    }
                    else if (root.TryGetProperty("method", out var mEl) &&
                             string.Equals(mEl.GetString(), "account/login/completed", StringComparison.Ordinal))
                    {
                        if (loginId is null || !root.TryGetProperty("params", out var pEl) || pEl.ValueKind != JsonValueKind.Object ||
                            !pEl.TryGetProperty("loginId", out var completedId) || completedId.ValueKind != JsonValueKind.String ||
                            completedId.GetString() != loginId) continue;
                        loginCompleted = pEl.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.True;
                        break;
                    }
                }
                catch
                {
                    // Ignore non-matching line.
                }
            }

            TryKillProcess(proc);
            proc = null;

            if (loginCompleted)
            {
                return await QueryCodexAccountLiveAsync(effectiveHome, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            TryKillProcess(proc);
        }
    }

    public static async Task<LocalClaudeTelemetry> QueryClaudeAccountLiveAsync(
        string? claudeConfigDir = null,
        DateTimeOffset? nowUtc = null,
        CancellationToken ct = default,
        bool includeUsage = true)
    {
        var utc = nowUtc ?? DateTimeOffset.UtcNow;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultDir = Path.Combine(home, ".claude");
        string effectiveDir = string.IsNullOrWhiteSpace(claudeConfigDir) || string.Equals(claudeConfigDir, "default", StringComparison.OrdinalIgnoreCase)
            ? defaultDir
            : claudeConfigDir;

        string cfgJson = string.Equals(effectiveDir, defaultDir, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(home, ".claude.json")
            : Path.Combine(effectiveDir, ".claude.json");
        string projRoot = Path.Combine(effectiveDir, "projects");
        var fileData = ReadClaude(cfgJson, projRoot, utc);

        string? claudeExe = ResolveClaudeExecutablePath();
        if (string.IsNullOrWhiteSpace(claudeExe) || !File.Exists(claudeExe))
        {
            return fileData;
        }

        Process? proc = null;
        try
        {
            Directory.CreateDirectory(effectiveDir);
            bool isCmd = claudeExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = isCmd
                ? new ProcessStartInfo("cmd.exe", $"/c \"\"{claudeExe}\" auth status --json\"")
                : new ProcessStartInfo(claudeExe, "auth status --json");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            string? isolatedProfile = string.Equals(effectiveDir, defaultDir, StringComparison.OrdinalIgnoreCase) ? null : effectiveDir;
            if (isolatedProfile is null) psi.Environment.Remove("CLAUDE_CONFIG_DIR");
            else psi.Environment["CLAUDE_CONFIG_DIR"] = isolatedProfile;

            proc = Process.Start(psi);
            if (proc is null) return fileData;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            string stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            int braceStart = stdout.IndexOf('{');
            int braceEnd = stdout.LastIndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart)
            {
                string json = stdout[braceStart..(braceEnd + 1)];
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool loggedIn = root.TryGetProperty("loggedIn", out var liEl) && liEl.ValueKind == JsonValueKind.True;
                if (!loggedIn)
                {
                    return new LocalClaudeTelemetry(
                        Detected: true,
                        IsAuthenticated: false,
                        AccountEmail: null,
                        DisplayName: null,
                        PlanTier: fileData.PlanTier,
                        TurnsLast5Hours: fileData.TurnsLast5Hours,
                        TokensLast5Hours: fileData.TokensLast5Hours,
                        Next5HourResetUtc: fileData.Next5HourResetUtc,
                        TurnsLast7Days: fileData.TurnsLast7Days,
                        TokensLast7Days: fileData.TokensLast7Days);
                }

                string? email = root.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String
                    ? emEl.GetString()
                    : fileData.AccountEmail;
                string? subType = root.TryGetProperty("subscriptionType", out var stEl) && stEl.ValueKind == JsonValueKind.String
                    ? NormalizeClaudeOrgType(stEl.GetString())
                    : fileData.PlanTier;

                ClaudeReportedUsage? usage = null;
                if (includeUsage && !string.IsNullOrWhiteSpace(email))
                {
                    try
                    {
                        usage = await ClaudeUsageTerminal.ReadAsync(claudeExe, isolatedProfile, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                        UnauthorizedAccessException or ExternalException)
                    {
                        // Keep the authenticated account even if its usage screen is unavailable.
                    }
                }

                return new LocalClaudeTelemetry(
                    Detected: true,
                    IsAuthenticated: true,
                    AccountEmail: email,
                    DisplayName: fileData.DisplayName,
                    PlanTier: subType ?? "Pro",
                    TurnsLast5Hours: fileData.TurnsLast5Hours,
                    TokensLast5Hours: fileData.TokensLast5Hours,
                    Next5HourResetUtc: fileData.Next5HourResetUtc,
                    TurnsLast7Days: fileData.TurnsLast7Days,
                    TokensLast7Days: fileData.TokensLast7Days,
                    SessionUsedPercent: usage?.Session?.UsedPercent,
                    SessionQuotaResetUtc: usage?.Session?.ResetUtc,
                    WeeklyUsedPercent: usage?.Weekly?.UsedPercent,
                    WeeklyQuotaResetUtc: usage?.Weekly?.ResetUtc);
            }
        }
        catch
        {
            // Fall back to file telemetry if CLI status check times out.
        }
        finally
        {
            TryKillProcess(proc);
        }

        return fileData;
    }

    public static async Task<LocalClaudeTelemetry> RunClaudeOAuthLoginAsync(
        string? claudeConfigDir = null,
        string? preferredEmail = null,
        CancellationToken ct = default)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultDir = Path.Combine(home, ".claude");
        string effectiveDir = string.IsNullOrWhiteSpace(claudeConfigDir) || string.Equals(claudeConfigDir, "default", StringComparison.OrdinalIgnoreCase)
            ? defaultDir
            : claudeConfigDir;

        string? claudeExe = ResolveClaudeExecutablePath();
        if (!string.IsNullOrWhiteSpace(claudeExe) && File.Exists(claudeExe))
        {
            try
            {
                Directory.CreateDirectory(effectiveDir);
                string emailArg = !string.IsNullOrWhiteSpace(preferredEmail) && preferredEmail.Contains('@')
                    ? $" --email \"{preferredEmail.Trim()}\""
                    : string.Empty;
                bool isCmd = claudeExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
                var psi = isCmd
                    ? new ProcessStartInfo("cmd.exe", $"/c \"\"{claudeExe}\" auth login --claudeai{emailArg}\"")
                    : new ProcessStartInfo(claudeExe, $"auth login --claudeai{emailArg}");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false;
                if (!string.Equals(effectiveDir, defaultDir, StringComparison.OrdinalIgnoreCase))
                {
                    psi.Environment["CLAUDE_CONFIG_DIR"] = effectiveDir;
                }

                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore if cancelled.
            }
        }

        return await QueryClaudeAccountLiveAsync(effectiveDir, DateTimeOffset.UtcNow, ct, includeUsage: false).ConfigureAwait(false);
    }

    public static LocalAntigravityTelemetry? TryReadAntigravityToolsAccount(
        string? targetEmail,
        DateTimeOffset nowUtc,
        out string? validAccessToken,
        out string? projectId,
        string? accountsDirOverride = null)
    {
        validAccessToken = null;
        projectId = null;
        if (string.IsNullOrWhiteSpace(targetEmail) && string.IsNullOrWhiteSpace(accountsDirOverride))
        {
            return null;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string accountsDir = accountsDirOverride ?? Path.Combine(home, ".antigravity_tools", "accounts");
        if (!Directory.Exists(accountsDir))
        {
            return null;
        }

        try
        {
            string wantedEmail = targetEmail?.Trim() ?? string.Empty;
            var jsonFiles = Directory.EnumerateFiles(accountsDir, "*.json", SearchOption.TopDirectoryOnly).ToList();
            if (jsonFiles.Count == 0) return null;

            foreach (string file in jsonFiles)
            {
                try
                {
                    string raw = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    string? fileEmail = root.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String
                        ? emEl.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(fileEmail)) continue;

                    if (!string.IsNullOrWhiteSpace(wantedEmail) &&
                        !string.Equals(fileEmail, wantedEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(wantedEmail) && jsonFiles.Count > 1)
                    {
                        continue;
                    }

                    string? tierName = null;
                    var buckets = ImmutableArray<LocalAntigravityBucket>.Empty;
                    if (root.TryGetProperty("quota", out var quotaEl) && quotaEl.ValueKind == JsonValueKind.Object)
                    {
                        if (quotaEl.TryGetProperty("subscription_tier", out var stEl) && stEl.ValueKind == JsonValueKind.String)
                        {
                            tierName = NormalizeAntigravityTier(stEl.GetString());
                        }
                        if (!quotaEl.TryGetProperty("is_forbidden", out var forbidden) || forbidden.ValueKind != JsonValueKind.True)
                        {
                            buckets = ParseAntigravityToolsQuota(quotaEl, nowUtc);
                        }
                    }

                    if (root.TryGetProperty("token", out var tokEl) && tokEl.ValueKind == JsonValueKind.Object)
                    {
                        long expSec = tokEl.TryGetProperty("expiry_timestamp", out var expEl) && expEl.TryGetInt64(out long ev)
                            ? ev
                            : 0L;
                        if (expSec > nowUtc.ToUnixTimeSeconds() + 45 &&
                            tokEl.TryGetProperty("access_token", out var atEl) && atEl.ValueKind == JsonValueKind.String)
                        {
                            string? tok = atEl.GetString();
                            if (!string.IsNullOrWhiteSpace(tok))
                            {
                                validAccessToken = tok;
                                if (tokEl.TryGetProperty("project_id", out var pjEl) && pjEl.ValueKind == JsonValueKind.String)
                                {
                                    projectId = pjEl.GetString();
                                }
                            }
                        }
                    }

                    var (turns5h, turns7d, nextReset5h) = ReadAntigravityBrainUsage(nowUtc);
                    return new LocalAntigravityTelemetry(
                        Detected: true,
                        IsAuthenticated: true,
                        AccountEmail: fileEmail,
                        PlanTier: tierName ?? "AI Pro",
                        ActiveModel: null,
                        TurnsLast5Hours: turns5h,
                        TurnsLast7Days: turns7d,
                        Next5HourResetUtc: nextReset5h,
                        ModelBuckets: buckets);
                }
                catch
                {
                    // Skip malformed account cache file.
                }
            }
        }
        catch
        {
            // Ignore filesystem access errors.
        }

        return null;
    }

    public static ImmutableArray<LocalAntigravityBucket> ParseAntigravityToolsQuota(JsonElement quotaEl, DateTimeOffset nowUtc)
    {
        var list = new List<LocalAntigravityBucket>(4);
        DateTimeOffset? observedAt = quotaEl.TryGetProperty("last_updated", out var updated) &&
                                     updated.ValueKind == JsonValueKind.Number && updated.TryGetInt64(out long epoch) &&
                                     epoch is > 0 and <= 253402300799L
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;

        if (quotaEl.TryGetProperty("quota_groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var grp in groupsEl.EnumerateArray())
            {
                if (grp.ValueKind != JsonValueKind.Object) continue;
                string rawDisp = grp.TryGetProperty("display_name", out var dnEl) && dnEl.ValueKind == JsonValueKind.String
                    ? dnEl.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
                string rawDesc = grp.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                    ? descEl.GetString()?.Trim() ?? string.Empty
                    : string.Empty;

                double? weeklyPct = null;
                DateTimeOffset? weeklyReset = null;
                double? fiveHourPct = null;
                DateTimeOffset? fiveHourReset = null;
                string? firstBucketId = null;

                if (grp.TryGetProperty("buckets", out var bArr) && bArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in bArr.EnumerateArray())
                    {
                        if (b.ValueKind != JsonValueKind.Object) continue;
                        firstBucketId ??= b.TryGetProperty("bucket_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString()
                            : null;
                        string win = b.TryGetProperty("window", out var wEl) && wEl.ValueKind == JsonValueKind.String
                            ? wEl.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                            : string.Empty;
                        double? frac = b.TryGetProperty("remaining_fraction", out var fEl) && fEl.TryGetDouble(out double fVal)
                            ? Math.Clamp(fVal * 100.0, 0.0, 100.0)
                            : null;
                        DateTimeOffset? resetUtc = null;
                        if (b.TryGetProperty("reset_time", out var rEl) && rEl.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(rEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedReset))
                        {
                            resetUtc = parsedReset;
                        }

                        if (!frac.HasValue) continue;
                        // The cache can project a full window from the observation time.
                        // Such a timestamp does not represent an active cooldown.
                        if (frac.Value == 100 && resetUtc is { } reset && observedAt is { } observed)
                        {
                            var window = win == "5h" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
                            if (((reset - observed) - window).Duration() <= TimeSpan.FromSeconds(2)) resetUtc = null;
                        }
                        if (win == "5h")
                        {
                            fiveHourPct = frac.Value;
                            fiveHourReset = resetUtc;
                        }
                        else
                        {
                            weeklyPct = frac.Value;
                            weeklyReset = resetUtc;
                        }
                    }
                }

                if (!weeklyPct.HasValue && !fiveHourPct.HasValue) continue;
                var (key, friendlyName, modelsSummary, accentHex) = ClassifyAntigravityGroup(firstBucketId, rawDisp, rawDesc);
                list.Add(new LocalAntigravityBucket(
                    BucketKey: key,
                    DisplayName: friendlyName,
                    ModelsSummary: modelsSummary,
                    WeeklyRemainingPercent: weeklyPct ?? fiveHourPct ?? 100.0,
                    WeeklyResetUtc: weeklyReset,
                    FiveHourRemainingPercent: fiveHourPct,
                    FiveHourResetUtc: fiveHourReset,
                    AccentHex: accentHex));
            }
        }

        if (list.Count == 0 && quotaEl.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
        {
            var agg = new Dictionary<string, (double MinPct, DateTimeOffset? Reset)>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in modelsArr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string mName = m.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString() ?? string.Empty
                    : string.Empty;
                if (!TryMapModelIdToBucketKey(mName, out string key)) continue;
                if (!m.TryGetProperty("percentage", out var pEl) || !pEl.TryGetDouble(out double pct)) continue;
                pct = Math.Clamp(pct, 0.0, 100.0);

                DateTimeOffset? rUtc = null;
                if (m.TryGetProperty("reset_time", out var rtEl) && rtEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(rtEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) &&
                    dto > nowUtc)
                {
                    rUtc = dto;
                }

                if (!agg.TryGetValue(key, out var existing) || pct < existing.MinPct)
                {
                    agg[key] = (pct, rUtc);
                }
            }

            foreach (var kv in agg)
            {
                var (friendly, summary, accent) = GetDefaultBucketMeta(kv.Key);
                list.Add(new LocalAntigravityBucket(kv.Key, friendly, summary, kv.Value.MinPct, kv.Value.Reset, null, null, accent));
            }
        }

        return OrderAntigravityBuckets(list);
    }

    public static ImmutableArray<LocalAntigravityBucket> MergeCloudCodeAvailableModels(
        ImmutableArray<LocalAntigravityBucket> baseBuckets,
        string cloudCodeJson,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(cloudCodeJson))
        {
            return baseBuckets;
        }

        var bucketMap = new Dictionary<string, LocalAntigravityBucket>(StringComparer.OrdinalIgnoreCase);
        if (!baseBuckets.IsDefaultOrEmpty)
        {
            foreach (var b in baseBuckets)
            {
                bucketMap[b.BucketKey] = b;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(cloudCodeJson);
            if (!doc.RootElement.TryGetProperty("models", out var modelsObj) || modelsObj.ValueKind != JsonValueKind.Object)
            {
                return baseBuckets;
            }

            var liveAgg = new Dictionary<string, (double MinPct, DateTimeOffset? ResetUtc, bool IsShortWindow)>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in modelsObj.EnumerateObject())
            {
                if (!TryMapModelIdToBucketKey(prop.Name, out string bucketKey)) continue;
                if (!prop.Value.TryGetProperty("quotaInfo", out var qi) || qi.ValueKind != JsonValueKind.Object) continue;
                if (!qi.TryGetProperty("remainingFraction", out var rfEl) || !rfEl.TryGetDouble(out double rf)) continue;

                double pct = Math.Clamp(rf * 100.0, 0.0, 100.0);
                DateTimeOffset? resetUtc = null;
                bool isShort = false;
                if (qi.TryGetProperty("resetTime", out var rtEl) && rtEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(rtEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) &&
                    parsed > nowUtc)
                {
                    resetUtc = parsed;
                    isShort = (parsed - nowUtc) <= TimeSpan.FromHours(6);
                }

                if (!liveAgg.TryGetValue(bucketKey, out var cur) || pct < cur.MinPct)
                {
                    liveAgg[bucketKey] = (pct, resetUtc, isShort);
                }
            }

            if (liveAgg.Count == 0) return baseBuckets;
            foreach (var kv in liveAgg)
            {
                if (bucketMap.TryGetValue(kv.Key, out var existing))
                {
                    bucketMap[kv.Key] = kv.Value.IsShortWindow
                        ? existing with
                        {
                            FiveHourRemainingPercent = kv.Value.MinPct,
                            FiveHourResetUtc = kv.Value.ResetUtc
                        }
                        : existing with
                        {
                            WeeklyRemainingPercent = kv.Value.MinPct,
                            WeeklyResetUtc = kv.Value.ResetUtc
                        };
                }
                else
                {
                    var (friendly, summary, accent) = GetDefaultBucketMeta(kv.Key);
                    bucketMap[kv.Key] = kv.Value.IsShortWindow
                        ? new LocalAntigravityBucket(kv.Key, friendly, summary, 100.0, null, kv.Value.MinPct, kv.Value.ResetUtc, accent)
                        : new LocalAntigravityBucket(kv.Key, friendly, summary, kv.Value.MinPct, kv.Value.ResetUtc, null, null, accent);
                }
            }
        }
        catch
        {
            return baseBuckets;
        }

        return OrderAntigravityBuckets(bucketMap.Values);
    }

    private static async Task<ImmutableArray<LocalAntigravityBucket>> QueryCloudCodeBucketsAsync(
        ImmutableArray<LocalAntigravityBucket> baseBuckets,
        string accessToken,
        string? projectId,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.32.1 windows/amd64");

            string payload = !string.IsNullOrWhiteSpace(projectId)
                ? JsonSerializer.Serialize(new { project = projectId })
                : "{}";
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return baseBuckets;
            }

            string json = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return MergeCloudCodeAvailableModels(baseBuckets, json, nowUtc);
        }
        catch
        {
            return baseBuckets;
        }
    }

    private static bool TryMapModelIdToBucketKey(string modelId, out string bucketKey)
    {
        bucketKey = string.Empty;
        if (string.IsNullOrWhiteSpace(modelId)) return false;

        if (modelId.Contains("argon", StringComparison.OrdinalIgnoreCase))
        {
            bucketKey = "gemini_argon";
            return true;
        }
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            bucketKey = "claude_gpt";
            return true;
        }
        if (modelId.StartsWith("gemini-4-", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("gemini-3.8-", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("gemini-3-", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("gemini-2.5-pro", StringComparison.OrdinalIgnoreCase))
        {
            bucketKey = "gemini";
            return true;
        }
        return false;
    }

    private static (string BucketKey, string DisplayName, string ModelsSummary, string AccentHex) ClassifyAntigravityGroup(
        string? bucketId,
        string displayName,
        string description)
    {
        string cleanSummary = description;
        const string prefix = "Models within this group:";
        int idx = cleanSummary.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            cleanSummary = cleanSummary[(idx + prefix.Length)..].Trim();
        }

        string idLower = (bucketId ?? string.Empty).ToLowerInvariant();
        string nameLower = displayName.ToLowerInvariant();

        if (idLower.Contains("argon") || nameLower.Contains("argon"))
        {
            var meta = GetDefaultBucketMeta("gemini_argon");
            return ("gemini_argon", meta.DisplayName, string.IsNullOrWhiteSpace(cleanSummary) ? meta.Summary : cleanSummary, meta.AccentHex);
        }
        if (idLower.StartsWith("3p") || nameLower.Contains("claude") || nameLower.Contains("gpt"))
        {
            var meta = GetDefaultBucketMeta("claude_gpt");
            return ("claude_gpt", meta.DisplayName, string.IsNullOrWhiteSpace(cleanSummary) ? meta.Summary : cleanSummary, meta.AccentHex);
        }
        if (idLower.StartsWith("gemini") || nameLower.Contains("gemini"))
        {
            var meta = GetDefaultBucketMeta("gemini");
            return ("gemini", meta.DisplayName, string.IsNullOrWhiteSpace(cleanSummary) ? meta.Summary : cleanSummary, meta.AccentHex);
        }

        string fallbackKey = string.IsNullOrWhiteSpace(bucketId) ? displayName : bucketId;
        return (fallbackKey, string.IsNullOrWhiteSpace(displayName) ? fallbackKey : displayName, cleanSummary, "#38BDF8");
    }

    private static (string DisplayName, string Summary, string AccentHex) GetDefaultBucketMeta(string bucketKey) => bucketKey switch
    {
        "gemini" => ("Gemini Models", "Gemini 4 Hydrogen, Gemini 3.8 Flash", "#4E8DF5"),
        "claude_gpt" => ("Claude & GPT", "Claude Opus 5.5, Claude Sonnet 5.5, GPT-OSS 120B", "#D97757"),
        "gemini_argon" => ("Gemini 4 Argon", "Gemini 4 Argon (Pro)", "#A78BFA"),
        _ => (bucketKey, bucketKey, "#38BDF8")
    };

    private static ImmutableArray<LocalAntigravityBucket> OrderAntigravityBuckets(IEnumerable<LocalAntigravityBucket> buckets)
    {
        string[] order = ["gemini", "claude_gpt", "gemini_argon"];
        return buckets
            .OrderBy(b =>
            {
                int idx = Array.IndexOf(order, b.BucketKey);
                return idx >= 0 ? idx : 99;
            })
            .ThenBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static string NormalizeAntigravityTier(string? raw) => (raw ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "PRO" => "AI Pro",
        "ULTRA" => "AI Ultra",
        "FREE" => "Free",
        "" => "AI Pro",
        _ => FormatPlanName(raw) ?? "AI Pro"
    };

    public static async Task<LocalAntigravityTelemetry> QueryAntigravityAccountLiveAsync(
        string? profileDir = null,
        DateTimeOffset? nowUtc = null,
        CancellationToken ct = default,
        string? targetEmail = null)
    {
        var utc = nowUtc ?? DateTimeOffset.UtcNow;
        var toolsSnap = TryReadAntigravityToolsAccount(targetEmail, utc, out string? accessToken, out string? projectId);
        if (toolsSnap is not null)
        {
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var updatedBuckets = await QueryCloudCodeBucketsAsync(toolsSnap.ModelBuckets, accessToken, projectId, utc, ct).ConfigureAwait(false);
                toolsSnap = toolsSnap with
                {
                    ModelBuckets = updatedBuckets,
                    HasLiveQuota = updatedBuckets != toolsSnap.ModelBuckets
                };
            }
            return toolsSnap;
        }

        var (turns5h, turns7d, nextReset5h) = ReadAntigravityBrainUsage(utc);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultGeminiDir = Path.Combine(home, ".gemini");
        string effectiveDir = string.IsNullOrWhiteSpace(profileDir) || string.Equals(profileDir, "default", StringComparison.OrdinalIgnoreCase)
            ? defaultGeminiDir
            : profileDir;
        bool isIsolatedProfile = !string.Equals(Path.GetFullPath(effectiveDir), Path.GetFullPath(defaultGeminiDir), StringComparison.OrdinalIgnoreCase);

        string? agyExe = ResolveAntigravityExecutablePath();
        if (string.IsNullOrWhiteSpace(agyExe) || !File.Exists(agyExe))
        {
            return new LocalAntigravityTelemetry(false, false, null, null, null, turns5h, turns7d, nextReset5h);
        }

        string tempLogPath = Path.Combine(Path.GetTempPath(), $"da_agy_auth_{Guid.NewGuid():N}.log");
        Process? proc = null;
        string? tierName = null;
        string? activeModel = null;

        try
        {
            string geminiDirArg = isIsolatedProfile ? $" --gemini-dir \"{effectiveDir}\"" : string.Empty;
            var psi = new ProcessStartInfo(agyExe, $"acp{geminiDirArg} --cli_log_file \"{tempLogPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            proc = Process.Start(psi);
            if (proc is null)
            {
                return new LocalAntigravityTelemetry(true, false, null, null, null, turns5h, turns7d, nextReset5h);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
            var token = timeoutCts.Token;

            await SendJsonRpcAsync(proc, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = 1,
                    clientCapabilities = new { },
                    clientInfo = new { name = "dinamikada", version = "1.0.0" }
                }
            }, token).ConfigureAwait(false);

            bool gotAuth = false;
            while (!token.IsCancellationRequested && !gotAuth)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) continue;

                    if (id == 1)
                    {
                        await SendJsonRpcAsync(proc, new
                        {
                            jsonrpc = "2.0",
                            id = 2,
                            method = "authenticate",
                            @params = new { methodId = "oauth-personal" }
                        }, token).ConfigureAwait(false);
                    }
                    else if (id == 2)
                    {
                        gotAuth = true;
                        if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object &&
                            res.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                            meta.TryGetProperty("antigravity", out var agy) && agy.ValueKind == JsonValueKind.Object &&
                            agy.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.Object &&
                            tier.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String)
                        {
                            string? rawTier = tn.GetString();
                            if (!string.IsNullOrWhiteSpace(rawTier))
                            {
                                tierName = rawTier.Replace("Google ", "", StringComparison.OrdinalIgnoreCase).Trim();
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore non-JSON lines.
                }
            }
        }
        catch
        {
            // Ignore timeout and inspect log file.
        }
        finally
        {
            TryKillProcess(proc);
        }

        string? email = null;
        try
        {
            await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);
            if (File.Exists(tempLogPath))
            {
                foreach (var logLine in ReadLinesSafe(tempLogPath))
                {
                    int idx = logLine.IndexOf("applyAuthResult: email=", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        string rest = logLine[(idx + "applyAuthResult: email=".Length)..];
                        int comma = rest.IndexOf(',');
                        string candidate = (comma >= 0 ? rest[..comma] : rest).Trim();
                        if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains('@'))
                        {
                            email = candidate;
                        }
                    }
                    else
                    {
                        int idx2 = logLine.IndexOf("authenticated successfully as ", StringComparison.Ordinal);
                        if (idx2 >= 0)
                        {
                            string candidate = logLine[(idx2 + "authenticated successfully as ".Length)..].Trim();
                            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains('@'))
                            {
                                email = candidate;
                            }
                        }
                    }
                }
                try { File.Delete(tempLogPath); } catch { }
            }
        }
        catch
        {
            // Ignore temp log cleanup errors.
        }

        bool isAuth = !string.IsNullOrWhiteSpace(email);
        var modelBuckets = ImmutableArray<LocalAntigravityBucket>.Empty;
        if (isAuth && !string.IsNullOrWhiteSpace(email))
        {
            var toolsEnriched = TryReadAntigravityToolsAccount(email, utc, out string? tok, out string? proj);
            if (toolsEnriched is not null)
            {
                modelBuckets = toolsEnriched.ModelBuckets;
                if (!string.IsNullOrWhiteSpace(tok))
                {
                    modelBuckets = await QueryCloudCodeBucketsAsync(modelBuckets, tok, proj, utc, ct).ConfigureAwait(false);
                }
                tierName ??= toolsEnriched.PlanTier;
            }
            else
            {
                string oauthPath = Path.Combine(effectiveDir, "oauth_creds.json");
                if (File.Exists(oauthPath))
                {
                    try
                    {
                        using var oauthDoc = JsonDocument.Parse(File.ReadAllText(oauthPath));
                        if (oauthDoc.RootElement.TryGetProperty("access_token", out var atEl) && atEl.ValueKind == JsonValueKind.String)
                        {
                            string? tok2 = atEl.GetString();
                            if (!string.IsNullOrWhiteSpace(tok2))
                            {
                                modelBuckets = await QueryCloudCodeBucketsAsync(modelBuckets, tok2, null, utc, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore optional OAuth token read errors.
                    }
                }
            }
        }

        return new LocalAntigravityTelemetry(
            Detected: true,
            IsAuthenticated: isAuth,
            AccountEmail: email,
            PlanTier: tierName ?? (isAuth ? "AI Pro" : null),
            ActiveModel: activeModel,
            TurnsLast5Hours: turns5h,
            TurnsLast7Days: turns7d,
            Next5HourResetUtc: nextReset5h,
            ModelBuckets: modelBuckets);
    }

    public static async Task<LocalAntigravityTelemetry> RunGoogleOAuthLoginAsync(
        string? profileDir,
        CancellationToken ct = default)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultGeminiDir = Path.Combine(home, ".gemini");
        string effectiveDir = string.IsNullOrWhiteSpace(profileDir) || string.Equals(profileDir, "default", StringComparison.OrdinalIgnoreCase)
            ? defaultGeminiDir
            : profileDir;

        string? agyExe = ResolveAntigravityExecutablePath();
        if (!string.IsNullOrWhiteSpace(agyExe) && File.Exists(agyExe))
        {
            string tempPs1 = Path.Combine(Path.GetTempPath(), $"da_google_oauth_{Guid.NewGuid():N}.ps1");
            try
            {
                Directory.CreateDirectory(effectiveDir);
                string escapedAgy = agyExe.Replace("'", "''");
                bool isIsolatedProfile = !string.Equals(Path.GetFullPath(effectiveDir), Path.GetFullPath(defaultGeminiDir), StringComparison.OrdinalIgnoreCase);
                string escapedGeminiArg = isIsolatedProfile
                    ? $"acp --login --gemini-dir \"{effectiveDir.Replace("'", "''")}\""
                    : "acp --login";
                string psScript = $@"
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = 'DinamikAda - Google (Antigravity / Gemini) Hesap Baglama'
Write-Host '=== DinamikAda · Google (Antigravity / Gemini) Hesap Baglama ===' -ForegroundColor Cyan
Write-Host 'Tarayicida Google giris sayfasi otomatik aciliyor...' -ForegroundColor Gray
Write-Host 'Ipucu: Giris yaptiktan sonra ekranda cikan yetki kodunu KOPYALAMANIZ yeterlidir (otomatik algilanir)' -ForegroundColor Yellow
Write-Host 'veya kodu bu pencereye yapistirip Enter tusuna basabilirsiniz.' -ForegroundColor Yellow
Write-Host ''

$initialClip = ''
try {{ $initialClip = Get-Clipboard -Raw }} catch {{}}

$psi = [System.Diagnostics.ProcessStartInfo]::new('{escapedAgy}', '{escapedGeminiArg}')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$p = [System.Diagnostics.Process]::Start($psi)

$urlOpened = $false
$codeSent = $false
$readTask = $p.StandardOutput.ReadLineAsync()

while (-not $p.HasExited) {{
    if ($readTask -ne $null -and $readTask.IsCompleted) {{
        $line = $readTask.Result
        if ($line -ne $null) {{
            Write-Host $line
            if (-not $urlOpened -and $line -match '(https://accounts\.google\.com/o/oauth2/auth\S+)') {{
                $urlOpened = $true
                Start-Process $matches[1]
            }}
            $readTask = $p.StandardOutput.ReadLineAsync()
        }} else {{
            $readTask = $null
        }}
    }}

    if (-not $codeSent) {{
        try {{
            $clip = Get-Clipboard -Raw
            if ($clip -ne $null -and $clip -ne $initialClip) {{
                $trimmed = $clip.Trim()
                if ($trimmed -match '^4/[0-9A-Za-z_-]+' -or $trimmed -match 'antigravity\.google/oauth-callback\?.*code=4/') {{
                    $codeSent = $true
                    Write-Host ''
                    Write-Host 'Kopyalanan Google yetki kodu otomatik algilandi! Giris tamamlaniyor...' -ForegroundColor Green
                    $p.StandardInput.WriteLine($trimmed)
                    $p.StandardInput.Flush()
                }}
            }}
        }} catch {{}}

        if (-not $codeSent -and [Console]::KeyAvailable) {{
            $userInput = [Console]::ReadLine()
            if (-not [string]::IsNullOrWhiteSpace($userInput)) {{
                $codeSent = $true
                $p.StandardInput.WriteLine($userInput.Trim())
                $p.StandardInput.Flush()
            }}
        }}
    }}

    Start-Sleep -Milliseconds 120
}}
";
                await File.WriteAllTextAsync(tempPs1, psScript, Encoding.UTF8, ct).ConfigureAwait(false);

                var psi = new ProcessStartInfo("powershell.exe", $"-STA -NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore if user cancelled browser OAuth.
            }
            finally
            {
                try { if (File.Exists(tempPs1)) File.Delete(tempPs1); } catch { }
            }
        }

        var liveAgy = await QueryAntigravityAccountLiveAsync(effectiveDir, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (liveAgy.IsAuthenticated && !string.IsNullOrWhiteSpace(liveAgy.AccountEmail))
        {
            try
            {
                string accountsJson = Path.Combine(effectiveDir, "google_accounts.json");
                string updatedJson = JsonSerializer.Serialize(new { active = liveAgy.AccountEmail, old = Array.Empty<string>() });
                File.WriteAllText(accountsJson, updatedJson, Encoding.UTF8);
            }
            catch
            {
                // Ignore optional google_accounts.json sync error.
            }
        }
        return liveAgy;
    }

    public static async Task<LiveApiAccountTelemetry> QueryApiAccountLiveAsync(
        string provider,
        string plainApiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plainApiKey))
        {
            return new LiveApiAccountTelemetry(false, null, null, null, null, "credit", null, "Missing API key");
        }

        string norm = provider.Trim().ToLowerInvariant();
        try
        {
            if (norm == "openrouter" || plainApiKey.StartsWith("sk-or-", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainApiKey.Trim());
                using var resp = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new LiveApiAccountTelemetry(false, null, null, null, null, "credit", null, $"HTTP {(int)resp.StatusCode}");
                }

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    double usage = data.TryGetProperty("usage", out var uEl) && uEl.TryGetDouble(out double u) ? u : 0.0;
                    double? limit = data.TryGetProperty("limit", out var lEl) && lEl.ValueKind == JsonValueKind.Number && lEl.TryGetDouble(out double l) ? l : null;
                    string? label = data.TryGetProperty("label", out var lbEl) && lbEl.ValueKind == JsonValueKind.String ? lbEl.GetString() : null;
                    bool isFree = data.TryGetProperty("is_free_tier", out var fEl) && fEl.ValueKind == JsonValueKind.True;
                    double total = limit ?? Math.Max(10.0, Math.Ceiling(usage + 10.0));
                    return new LiveApiAccountTelemetry(
                        Success: true,
                        AccountLabel: label,
                        PlanTier: isFree ? "Free" : "Credits",
                        TotalQuota: Math.Round(total, 2),
                        UsedQuota: Math.Round(usage, 2),
                        QuotaUnit: "credit",
                        SecondarySummary: $"OpenRouter · ${usage:F2} kullanıldı",
                        ErrorMessage: null);
                }
            }
            else if (norm == "deepseek")
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainApiKey.Trim());
                using var resp = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new LiveApiAccountTelemetry(false, null, null, null, null, "credit", null, $"HTTP {(int)resp.StatusCode}");
                }

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("balance_infos", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var b0 = arr[0];
                    string? balStr = b0.TryGetProperty("total_balance", out var tb) ? tb.GetString() : null;
                    string? curr = b0.TryGetProperty("currency", out var cEl) ? cEl.GetString() : "USD";
                    if (double.TryParse(balStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double bal))
                    {
                        double cap = Math.Max(10.0, Math.Ceiling(bal));
                        return new LiveApiAccountTelemetry(
                            Success: true,
                            AccountLabel: null,
                            PlanTier: "API",
                            TotalQuota: cap,
                            UsedQuota: Math.Max(0.0, cap - bal),
                            QuotaUnit: "credit",
                            SecondarySummary: $"Bakiye: {bal:F2} {curr}",
                            ErrorMessage: null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new LiveApiAccountTelemetry(false, null, null, null, null, "credit", null, ex.Message);
        }

        return new LiveApiAccountTelemetry(false, null, null, null, null, "credit", null, "Unsupported endpoint");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string ProtectSecret(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;
        byte[] bytes = Encoding.UTF8.GetBytes(plainText.Trim());
        IntPtr inPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, inPtr, bytes.Length);
            var inBlob = new DataBlob { cbData = bytes.Length, pbData = inPtr };
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
            {
                return string.Empty;
            }
            try
            {
                byte[] encrypted = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);
                return Convert.ToBase64String(encrypted);
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }

    public static string? UnprotectSecret(string? encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64)) return null;
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(encryptedBase64);
        }
        catch
        {
            return null;
        }

        IntPtr inPtr = Marshal.AllocHGlobal(encrypted.Length);
        try
        {
            Marshal.Copy(encrypted, 0, inPtr, encrypted.Length);
            var inBlob = new DataBlob { cbData = encrypted.Length, pbData = inPtr };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
            {
                return null;
            }
            try
            {
                byte[] plainBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }

    public static LocalCodexTelemetry ReadCodex(string codexRoot, DateTimeOffset nowUtc)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(codexRoot) || !Directory.Exists(codexRoot))
            {
                return new LocalCodexTelemetry(false, false, null, null, null, null, null, null, null, null, null, 0, 0, null);
            }

            string? activeModel = ReadCodexModel(Path.Combine(codexRoot, "config.toml"));
            bool hasAuthFile = File.Exists(Path.Combine(codexRoot, "auth.json"));
            string sessionsDir = Path.Combine(codexRoot, "sessions");
            if (!Directory.Exists(sessionsDir))
            {
                return new LocalCodexTelemetry(activeModel is not null || hasAuthFile, hasAuthFile, null, null, activeModel, null, null, null, null, null, null, 0, 0, null);
            }

            var minWrite = nowUtc.UtcDateTime.AddDays(-14);
            var sessionFiles = EnumerateRecentFiles(sessionsDir, "*.jsonl", minWrite, maxCount: 8);
            if (sessionFiles.Count == 0)
            {
                return new LocalCodexTelemetry(activeModel is not null || hasAuthFile, hasAuthFile, null, null, activeModel, null, null, null, null, null, null, 0, 0, null);
            }

            string? bestPlan = null;
            double? bestPriUsed = null;
            int? bestPriWin = null;
            DateTimeOffset? bestPriReset = null;
            double? bestSecUsed = null;
            int? bestSecWin = null;
            DateTimeOffset? bestSecReset = null;
            long bestTokens = 0;
            DateTimeOffset? bestEventUtc = null;
            bool foundPrimaryLimits = false;

            foreach (var file in sessionFiles)
            {
                foreach (var line in ReadTailLinesSafe(file, maxTailBytes: 524_288))
                {
                    if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                        if (!payload.TryGetProperty("type", out var pt) || !string.Equals(pt.GetString(), "token_count", StringComparison.Ordinal)) continue;

                        DateTimeOffset? eventTs = TryReadTimestamp(root);
                        long totalTokens = bestTokens;
                        if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                            info.TryGetProperty("total_token_usage", out var ttu) && ttu.ValueKind == JsonValueKind.Object &&
                            ttu.TryGetProperty("total_tokens", out var ttVal) && ttVal.TryGetInt64(out long parsedTokens))
                        {
                            totalTokens = parsedTokens;
                        }

                        if (!payload.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) continue;
                        if (rl.TryGetProperty("limit_id", out var limitId) && limitId.ValueKind == JsonValueKind.String &&
                            !string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase)) continue;

                        string? plan = rl.TryGetProperty("plan_type", out var plEl) && plEl.ValueKind == JsonValueKind.String
                            ? FormatPlanName(plEl.GetString())
                            : bestPlan;

                        bool hasPrimary = rl.TryGetProperty("primary", out var pri) && pri.ValueKind == JsonValueKind.Object;
                        if (hasPrimary)
                        {
                            if (bestEventUtc is null || eventTs is null || eventTs >= bestEventUtc)
                            {
                                bestEventUtc = eventTs ?? bestEventUtc;
                                bestPlan = plan;
                                bestTokens = totalTokens;
                                (bestPriUsed, bestPriWin, bestPriReset) = ParseRateWindow(pri);
                                if (rl.TryGetProperty("secondary", out var sec) && sec.ValueKind == JsonValueKind.Object)
                                {
                                    (bestSecUsed, bestSecWin, bestSecReset) = ParseRateWindow(sec);
                                }
                                else
                                {
                                    (bestSecUsed, bestSecWin, bestSecReset) = (null, null, null);
                                }
                                foundPrimaryLimits = true;
                            }
                        }
                        else if (!foundPrimaryLimits && plan is not null)
                        {
                            bestPlan = plan;
                            bestTokens = totalTokens;
                            bestEventUtc ??= eventTs;
                        }
                    }
                    catch
                    {
                        // Ignore truncated/in-progress JSONL lines.
                    }
                }

                if (foundPrimaryLimits) break;
            }

            bool detected = activeModel is not null || bestPlan is not null || foundPrimaryLimits || hasAuthFile;
            return new LocalCodexTelemetry(
                Detected: detected,
                IsAuthenticated: hasAuthFile || foundPrimaryLimits,
                AccountEmail: null,
                PlanTier: bestPlan,
                ActiveModel: activeModel,
                PrimaryUsedPercent: bestPriUsed,
                PrimaryWindowMinutes: bestPriWin,
                PrimaryResetsAtUtc: bestPriReset,
                SecondaryUsedPercent: bestSecUsed,
                SecondaryWindowMinutes: bestSecWin,
                SecondaryResetsAtUtc: bestSecReset,
                AvailableResetCredits: 0,
                LastSessionTotalTokens: bestTokens,
                LastEventUtc: bestEventUtc);
        }
        catch
        {
            return new LocalCodexTelemetry(false, false, null, null, null, null, null, null, null, null, null, 0, 0, null);
        }
    }

    public static LocalClaudeTelemetry ReadClaude(string claudeConfigJsonPath, string claudeProjectsRoot, DateTimeOffset nowUtc)
    {
        try
        {
            string? email = null;
            string? display = null;
            string? planTier = null;
            bool hasConfigMeta = false;

            if (!string.IsNullOrWhiteSpace(claudeConfigJsonPath) && File.Exists(claudeConfigJsonPath))
            {
                try
                {
                    using var fs = new FileStream(claudeConfigJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(fs);
                    if (doc.RootElement.TryGetProperty("oauthAccount", out var oa) && oa.ValueKind == JsonValueKind.Object)
                    {
                        if (oa.TryGetProperty("emailAddress", out var em) && em.ValueKind == JsonValueKind.String)
                            email = em.GetString();
                        if (oa.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                            display = dn.GetString();
                        if (oa.TryGetProperty("organizationType", out var ot) && ot.ValueKind == JsonValueKind.String)
                            planTier = NormalizeClaudeOrgType(ot.GetString());
                        hasConfigMeta = !string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(planTier);
                    }
                }
                catch
                {
                    // Skip malformed or locked metadata file gracefully.
                }
            }

            if (string.IsNullOrWhiteSpace(claudeProjectsRoot) || !Directory.Exists(claudeProjectsRoot))
            {
                return new LocalClaudeTelemetry(hasConfigMeta, hasConfigMeta, email, display, planTier, 0, 0, null, 0, 0);
            }

            DateTimeOffset cutoff7d = nowUtc.AddDays(-7);
            DateTimeOffset cutoff5h = nowUtc.AddHours(-5);
            var files = EnumerateRecentFiles(claudeProjectsRoot, "*.jsonl", cutoff7d.UtcDateTime, maxCount: 80);

            var seen5hMsgIds = new HashSet<string>(StringComparer.Ordinal);
            var seen7dMsgIds = new HashSet<string>(StringComparer.Ordinal);
            long tokens5h = 0;
            long tokens7d = 0;
            int turns5h = 0;
            int turns7d = 0;
            DateTimeOffset? oldest5h = null;
            int syntheticIndex = 0;

            foreach (var file in files)
            {
                foreach (var line in ReadLinesSafe(file))
                {
                    if (!line.Contains("\"assistant\"", StringComparison.Ordinal) || !line.Contains("\"usage\"", StringComparison.Ordinal))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var typeEl) || !string.Equals(typeEl.GetString(), "assistant", StringComparison.Ordinal))
                            continue;

                        DateTimeOffset? ts = TryReadTimestamp(root);
                        if (ts is null || ts.Value < cutoff7d || ts.Value > nowUtc.AddMinutes(5)) continue;

                        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                        string? model = msg.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(model) || string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

                        string msgId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? $"anon_{syntheticIndex++}"
                            : $"anon_{syntheticIndex++}";

                        long turnTokens = ReadUsageTokenSum(usage);
                        if (seen7dMsgIds.Add(msgId))
                        {
                            turns7d++;
                            tokens7d += turnTokens;
                        }

                        if (ts.Value >= cutoff5h && seen5hMsgIds.Add(msgId))
                        {
                            turns5h++;
                            tokens5h += turnTokens;
                            if (oldest5h is null || ts.Value < oldest5h.Value)
                            {
                                oldest5h = ts.Value;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore partial line.
                    }
                }
            }

            bool detected = hasConfigMeta || turns7d > 0;
            DateTimeOffset? nextReset5h = oldest5h?.AddHours(5);
            if (nextReset5h <= nowUtc) nextReset5h = null;

            return new LocalClaudeTelemetry(
                Detected: detected,
                IsAuthenticated: hasConfigMeta,
                AccountEmail: email,
                DisplayName: display,
                PlanTier: planTier,
                TurnsLast5Hours: turns5h,
                TokensLast5Hours: tokens5h,
                Next5HourResetUtc: nextReset5h,
                TurnsLast7Days: turns7d,
                TokensLast7Days: tokens7d);
        }
        catch
        {
            return new LocalClaudeTelemetry(false, false, null, null, null, 0, 0, null, 0, 0);
        }
    }

    public static LocalGeminiTelemetry ReadGemini(string geminiAccountsJsonPath, string geminiTmpRoot, DateTimeOffset nowUtc)
    {
        try
        {
            string? activeEmail = null;
            if (!string.IsNullOrWhiteSpace(geminiAccountsJsonPath) && File.Exists(geminiAccountsJsonPath))
            {
                try
                {
                    using var fs = new FileStream(geminiAccountsJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(fs);
                    if (doc.RootElement.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.String)
                    {
                        activeEmail = act.GetString();
                    }
                }
                catch
                {
                    // Skip unreadable file cleanly.
                }
            }

            bool hasEmail = !string.IsNullOrWhiteSpace(activeEmail);
            if (string.IsNullOrWhiteSpace(geminiTmpRoot) || !Directory.Exists(geminiTmpRoot))
            {
                return new LocalGeminiTelemetry(hasEmail, hasEmail, activeEmail, 0, null);
            }

            DateTimeOffset cutoff24h = nowUtc.AddHours(-24);
            var files = EnumerateRecentFiles(geminiTmpRoot, "*.jsonl", cutoff24h.UtcDateTime, maxCount: 60, requiredParentFolderName: "chats");
            int modelTurns = 0;
            int userTurns = 0;
            DateTimeOffset? oldestModelTurn = null;
            DateTimeOffset? oldestUserTurn = null;

            foreach (var file in files)
            {
                foreach (var line in ReadLinesSafe(file))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String) continue;
                        string type = tEl.GetString() ?? string.Empty;
                        DateTimeOffset? ts = TryReadTimestamp(root);
                        if (ts is null || ts.Value < cutoff24h || ts.Value > nowUtc.AddMinutes(5)) continue;

                        if (type is "gemini" or "model" or "assistant")
                        {
                            modelTurns++;
                            if (oldestModelTurn is null || ts.Value < oldestModelTurn.Value) oldestModelTurn = ts.Value;
                        }
                        else if (type == "user")
                        {
                            if (line.Contains("<session_context>", StringComparison.Ordinal)) continue;
                            userTurns++;
                            if (oldestUserTurn is null || ts.Value < oldestUserTurn.Value) oldestUserTurn = ts.Value;
                        }
                    }
                    catch
                    {
                        // Ignore malformed line.
                    }
                }
            }

            int turns = modelTurns > 0 ? modelTurns : userTurns;
            DateTimeOffset? oldest = modelTurns > 0 ? oldestModelTurn : oldestUserTurn;
            DateTimeOffset? nextReset = oldest?.AddHours(24);
            if (nextReset <= nowUtc) nextReset = null;

            bool detected = hasEmail || turns > 0;
            return new LocalGeminiTelemetry(detected, hasEmail, activeEmail, turns, nextReset);
        }
        catch
        {
            return new LocalGeminiTelemetry(false, false, null, 0, null);
        }
    }

    public static (int Turns5h, int Turns7d, DateTimeOffset? NextReset5h) ReadAntigravityBrainUsage(DateTimeOffset nowUtc)
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string brainRoot = Path.Combine(home, ".gemini", "antigravity", "brain");
            if (!Directory.Exists(brainRoot))
            {
                return (0, 0, null);
            }

            DateTimeOffset cutoff7d = nowUtc.AddDays(-7);
            DateTimeOffset cutoff5h = nowUtc.AddHours(-5);
            var candidates = new List<(DateTime LastWriteUtc, string Path)>();

            foreach (var dir in Directory.EnumerateDirectories(brainRoot))
            {
                try
                {
                    string logFile = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                    if (!File.Exists(logFile)) continue;
                    var writeUtc = File.GetLastWriteTimeUtc(logFile);
                    if (writeUtc >= cutoff7d.UtcDateTime)
                    {
                        candidates.Add((writeUtc, logFile));
                    }
                }
                catch
                {
                    // Ignore inaccessible conversation folder.
                }
            }

            candidates.Sort(static (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
            int take = Math.Min(24, candidates.Count);
            int turns5h = 0;
            int turns7d = 0;
            DateTimeOffset? oldest5h = null;

            for (int i = 0; i < take; i++)
            {
                foreach (var line in ReadTailLinesSafe(candidates[i].Path, maxTailBytes: 262_144))
                {
                    if (!line.Contains("\"USER_INPUT\"", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var tEl) || !string.Equals(tEl.GetString(), "USER_INPUT", StringComparison.Ordinal))
                            continue;

                        DateTimeOffset? ts = TryReadTimestamp(root);
                        if (ts is null || ts.Value < cutoff7d || ts.Value > nowUtc.AddMinutes(5)) continue;
                        turns7d++;
                        if (ts.Value >= cutoff5h)
                        {
                            turns5h++;
                            if (oldest5h is null || ts.Value < oldest5h.Value)
                            {
                                oldest5h = ts.Value;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore malformed JSONL line.
                    }
                }
            }

            DateTimeOffset? nextReset = oldest5h?.AddHours(5);
            if (nextReset <= nowUtc) nextReset = null;
            return (turns5h, turns7d, nextReset);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    private static async Task SendJsonRpcAsync(Process proc, object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload);
        await proc.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void TryKillProcess(Process? proc)
    {
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore process exit race.
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static (double? UsedPct, int? WindowMin, DateTimeOffset? ResetsUtc) ParseAppServerRateWindow(JsonElement el)
    {
        double? used = el.TryGetProperty("usedPercent", out var uEl) && uEl.ValueKind == JsonValueKind.Number && uEl.TryGetDouble(out double uVal)
            ? Math.Clamp(uVal, 0.0, 100.0)
            : null;
        int? winMin = el.TryGetProperty("windowDurationMins", out var wEl) && wEl.ValueKind == JsonValueKind.Number && wEl.TryGetInt32(out int wVal) && wVal > 0
            ? wVal
            : null;
        DateTimeOffset? resetsUtc = null;
        if (el.TryGetProperty("resetsAt", out var rEl) && rEl.ValueKind == JsonValueKind.Number &&
            rEl.TryGetInt64(out long epochSec) && epochSec is > 0 and <= 253402300799L)
        {
            resetsUtc = DateTimeOffset.FromUnixTimeSeconds(epochSec);
        }
        return (used, winMin, resetsUtc);
    }

    private static (double? UsedPct, int? WindowMin, DateTimeOffset? ResetsUtc) ParseRateWindow(JsonElement el)
    {
        double? used = el.TryGetProperty("used_percent", out var uEl) && uEl.ValueKind == JsonValueKind.Number && uEl.TryGetDouble(out double uVal)
            ? Math.Clamp(uVal, 0.0, 100.0)
            : null;
        int? winMin = el.TryGetProperty("window_minutes", out var wEl) && wEl.ValueKind == JsonValueKind.Number && wEl.TryGetInt32(out int wVal) && wVal > 0
            ? wVal
            : null;
        DateTimeOffset? resetsUtc = null;
        if (el.TryGetProperty("resets_at", out var rEl) && rEl.ValueKind == JsonValueKind.Number &&
            rEl.TryGetInt64(out long epochSec) && epochSec is > 0 and <= 253402300799L)
        {
            resetsUtc = DateTimeOffset.FromUnixTimeSeconds(epochSec);
        }
        return (used, winMin, resetsUtc);
    }

    private static string? ReadCodexModel(string configTomlPath)
    {
        if (!File.Exists(configTomlPath)) return null;
        foreach (var raw in ReadLinesSafe(configTomlPath))
        {
            string line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith('[')) continue;
            if (!line.StartsWith("model", StringComparison.OrdinalIgnoreCase)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            if (!string.Equals(key, "model", StringComparison.OrdinalIgnoreCase)) continue;
            string val = line[(eq + 1)..].Trim().Trim('"', '\'');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    private static string NormalizeClaudeOrgType(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "claude_pro" => "Pro",
        "claude_max" => "Max",
        "claude_team" or "team" => "Team",
        "claude_enterprise" or "enterprise" => "Enterprise",
        _ => FormatPlanName(raw) ?? "Pro"
    };

    private static string? FormatPlanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static long ReadUsageTokenSum(JsonElement usage)
    {
        long sum = 0;
        if (usage.TryGetProperty("input_tokens", out var inp) && inp.TryGetInt64(out long iv)) sum += iv;
        if (usage.TryGetProperty("output_tokens", out var outp) && outp.TryGetInt64(out long ov)) sum += ov;
        if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt64(out long cv)) sum += cv;
        if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out long rv)) sum += rv;
        return sum;
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root)
    {
        string? raw = null;
        if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String)
        {
            raw = tsEl.GetString();
        }
        else if (root.TryGetProperty("created_at", out var caEl) && caEl.ValueKind == JsonValueKind.String)
        {
            raw = caEl.GetString();
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto
            : null;
    }

    private static List<string> EnumerateRecentFiles(
        string rootDir,
        string searchPattern,
        DateTime minWriteUtc,
        int maxCount,
        string? requiredParentFolderName = null)
    {
        var matches = new List<(DateTime LastWriteUtc, string Path)>();
        var dirs = new Stack<string>();
        dirs.Push(rootDir);

        while (dirs.Count > 0)
        {
            string current = dirs.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    var attrs = File.GetAttributes(sub);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    dirs.Push(sub);
                }

                if (requiredParentFolderName is not null &&
                    !string.Equals(Path.GetFileName(current), requiredParentFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(current, searchPattern))
                {
                    var writeUtc = File.GetLastWriteTimeUtc(file);
                    if (writeUtc >= minWriteUtc)
                    {
                        matches.Add((writeUtc, file));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible directories or transient locks.
            }
        }

        matches.Sort(static (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
        int take = Math.Min(maxCount, matches.Count);
        var result = new List<string>(take);
        for (int i = 0; i < take; i++)
        {
            result.Add(matches[i].Path);
        }
        return result;
    }

    private static IEnumerable<string> ReadLinesSafe(string path) =>
        ReadTailLinesSafe(path, maxTailBytes: long.MaxValue);

    private static IEnumerable<string> ReadTailLinesSafe(string path, long maxTailBytes = 524_288)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (maxTailBytes < long.MaxValue && fs.Length > maxTailBytes)
            {
                fs.Seek(fs.Length - maxTailBytes, SeekOrigin.Begin);
            }
        }
        catch
        {
            fs?.Dispose();
            yield break;
        }

        using (fs)
        using (var sr = new StreamReader(fs))
        {
            if (fs.Position > 0)
            {
                try { sr.ReadLine(); } catch { yield break; }
            }

            while (true)
            {
                string? line;
                try
                {
                    line = sr.ReadLine();
                }
                catch
                {
                    yield break;
                }

                if (line is null) yield break;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line;
                }
            }
        }
    }
}
