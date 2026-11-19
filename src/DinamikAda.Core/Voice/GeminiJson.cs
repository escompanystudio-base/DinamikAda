using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DinamikAda.Core.Voice;

/// <summary>One part of a Gemini request: text, or inline bytes (audio/wav, image/png…).</summary>
public sealed record GeminiPart(string? Text, string? MimeType = null, byte[]? Data = null)
{
    public static GeminiPart FromText(string text) => new(text);
    public static GeminiPart FromBytes(string mimeType, byte[] data) => new(null, mimeType, data);
}

/// <summary>A Gemini failure with a message that can be shown to the user.</summary>
public sealed class GeminiException(string message) : Exception(message);

/// <summary>Builds generateContent request bodies and reads responses (pure, unit tested).</summary>
public static class GeminiJson
{
    /// <summary>
    /// Body for POST /v1beta/models/{model}:generateContent. With <paramref name="responseSchemaJson"/> the reply is
    /// constrained JSON (responseMimeType application/json).
    /// </summary>
    /// <param name="thinkingBudget">
    /// Thinking tokens the model may spend before answering; 0 turns thinking off (fastest, cheapest). Null leaves
    /// the model default. Only Gemini 2.5 Flash models accept 0; see <see cref="ThinkingBudgetFor"/>.
    /// </param>
    public static string BuildRequest(string? systemInstruction, IReadOnlyList<GeminiPart> parts,
        string? responseSchemaJson = null, double temperature = 0.2, int maxOutputTokens = 2048, int? thinkingBudget = null)
    {
        var partArray = new JsonArray();
        foreach (var p in parts)
        {
            if (p.Data is not null)
            {
                partArray.Add(new JsonObject
                {
                    ["inline_data"] = new JsonObject
                    {
                        ["mime_type"] = p.MimeType ?? "application/octet-stream",
                        ["data"] = Convert.ToBase64String(p.Data)
                    }
                });
            }
            else if (!string.IsNullOrEmpty(p.Text))
            {
                partArray.Add(new JsonObject { ["text"] = p.Text });
            }
        }

        var generation = new JsonObject
        {
            ["temperature"] = temperature,
            ["maxOutputTokens"] = maxOutputTokens
        };
        if (thinkingBudget is { } budget)
        {
            generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget };
        }
        if (!string.IsNullOrWhiteSpace(responseSchemaJson))
        {
            generation["responseMimeType"] = "application/json";
            generation["responseSchema"] = JsonNode.Parse(responseSchemaJson);
        }

        var root = new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = partArray } },
            ["generationConfig"] = generation
        };
        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            root["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstruction } }
            };
        }
        return root.ToJsonString();
    }

    /// <summary>Concatenated text of the first candidate; throws <see cref="GeminiException"/> when blocked or empty.</summary>
    public static string ParseText(string json, string lang = "tr")
    {
        bool en = lang == "en";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("promptFeedback", out var fb) && fb.TryGetProperty("blockReason", out var br))
            throw new GeminiException((en ? "Gemini blocked the request: " : "Gemini isteği engelledi: ") + br.GetString());
        if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            throw new GeminiException(en ? "Gemini returned no answer." : "Gemini yanıt vermedi.");

        var first = cands[0];
        var sb = new StringBuilder();
        if (first.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                // Skip "thought" parts some models return before the answer.
                if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
            }
        }
        if (sb.Length == 0)
        {
            string reason = first.TryGetProperty("finishReason", out var fr) ? fr.GetString() ?? string.Empty : string.Empty;
            throw new GeminiException(reason is "SAFETY" or "PROHIBITED_CONTENT"
                ? (en ? "Gemini declined to answer this." : "Gemini bunu yanıtlamadı.")
                : (en ? "Gemini returned an empty answer." : "Gemini boş yanıt döndü."));
        }
        return sb.ToString().Trim();
    }

    /// <summary>The error message of a Gemini error body ({"error":{"message":…,"status":…}}), or null.</summary>
    public static string? ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Removes ```json fences a model may wrap around JSON despite instructions.</summary>
    public static string StripFences(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        int firstNl = t.IndexOf('\n');
        int lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNl > 0 && lastFence > firstNl ? t[(firstNl + 1)..lastFence].Trim() : t.Trim('`').Trim();
    }

    /// <summary>
    /// Thinking budget to send for a model so it answers fast and cheap: 0 (off) for Gemini 2.5 Flash / Flash-Lite,
    /// null (model default) for everything else, because Pro models cannot turn thinking off and other model
    /// families use different thinking settings.
    /// </summary>
    public static int? ThinkingBudgetFor(string? model)
    {
        string m = (model ?? string.Empty).Trim().ToLowerInvariant();
        return m.Contains("2.5") && m.Contains("flash") ? 0 : null;
    }

    /// <summary>Counts words for the speed statistics.</summary>
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
