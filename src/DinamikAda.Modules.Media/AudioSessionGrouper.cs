using System.Collections.Immutable;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Audio;

namespace DinamikAda.Modules.Media;

public sealed record RawAudioSessionEntry(
    string GroupKey,
    int ProcessId,
    string ProcessName,
    string DisplayName,
    float Volume01,
    bool IsMuted,
    float PeakLevel01,
    string? IconPath,
    string? RoutedOutputDeviceId = null);

public static class AudioSessionGrouper
{
    /// <summary>
    /// Normalizes a process name for consistent case-insensitive matching across
    /// CoreAudio sessions (<see cref="System.Diagnostics.Process.ProcessName"/>) and persisted routing rules.
    /// </summary>
    public static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return string.Empty;
        string trimmed = processName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = trimmed[..^4].TrimEnd();
        }
        return trimmed;
    }

    /// <summary>
    /// Resolves a raw persisted AudioPolicyConfig endpoint identifier against known output devices
    /// (or an exact/token-matching configured rule) using endpoint GUID token matching.
    /// </summary>
    public static string? ResolveRoutedOutputDeviceId(
        string? policyEndpointId,
        IReadOnlyList<AudioEndpointSnapshot> outputs,
        string? configuredRuleDeviceId = null)
    {
        if (string.IsNullOrWhiteSpace(policyEndpointId))
        {
            return null;
        }

        string trimmedRaw = policyEndpointId.Trim();
        string? rawToken = AudioPolicyConfigClient.ExtractEndpointGuidToken(trimmedRaw);

        for (int i = 0; i < outputs.Count; i++)
        {
            var dev = outputs[i];
            if (string.Equals(dev.Id, trimmedRaw, StringComparison.OrdinalIgnoreCase))
            {
                return dev.Id;
            }

            if (!string.IsNullOrWhiteSpace(rawToken))
            {
                string? devToken = AudioPolicyConfigClient.ExtractEndpointGuidToken(dev.Id);
                if (!string.IsNullOrWhiteSpace(devToken) &&
                    string.Equals(devToken, rawToken, StringComparison.OrdinalIgnoreCase))
                {
                    return dev.Id;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredRuleDeviceId))
        {
            string trimmedRule = configuredRuleDeviceId.Trim();
            if (string.Equals(trimmedRule, trimmedRaw, StringComparison.OrdinalIgnoreCase))
            {
                return trimmedRule;
            }

            if (!string.IsNullOrWhiteSpace(rawToken))
            {
                string? ruleToken = AudioPolicyConfigClient.ExtractEndpointGuidToken(trimmedRule);
                if (!string.IsNullOrWhiteSpace(ruleToken) &&
                    string.Equals(ruleToken, rawToken, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmedRule;
                }
            }
        }

        return null;
    }

    public static ImmutableArray<AudioAppSessionSnapshot> GroupSessions(IReadOnlyList<RawAudioSessionEntry> entries)
    {
        if (entries.Count == 0) return ImmutableArray<AudioAppSessionSnapshot>.Empty;

        var grouped = entries
            .GroupBy(e => e.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                string normalizedProc = NormalizeProcessName(first.ProcessName);
                string effectiveProc = string.IsNullOrEmpty(normalizedProc) ? first.ProcessName : normalizedProc;
                string bestName = g.Select(x => x.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? effectiveProc;
                string? bestIcon = g.Select(x => x.IconPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                string? routedOutputDeviceId = g.Select(x => x.RoutedOutputDeviceId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                float maxVol = g.Max(x => x.Volume01);
                bool allMuted = g.All(x => x.IsMuted);
                float maxPeak = g.Max(x => x.PeakLevel01);
                return new AudioAppSessionSnapshot(
                    g.Key,
                    first.ProcessId,
                    effectiveProc,
                    bestName,
                    Math.Clamp(maxVol, 0f, 1f),
                    allMuted,
                    Math.Clamp(maxPeak, 0f, 1f),
                    bestIcon,
                    routedOutputDeviceId);
            })
            .OrderByDescending(s => s.PeakLevel01 > 0.005f)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return grouped;
    }
}
