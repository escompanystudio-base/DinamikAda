using System.Net;
using System.Net.Http;
using System.Text;
using DinamikAda.Core.Config;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Productivity;

namespace DinamikAda.App.Services.Voice;

/// <summary>
/// Minimal Gemini REST client (generateContent) using the user's own API key. Requests go straight from this PC to
/// generativelanguage.googleapis.com; the key is sent in the x-goog-api-key header, never in the URL.
/// </summary>
public sealed class GeminiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AppOrchestrator _orch;

    public GeminiClient(AppOrchestrator orch) => _orch = orch;

    /// <summary>Plain key from config, or null.</summary>
    public static string? GetKey(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.GeminiApiKeyProtected)) return null;
        string? plain = LocalAiCliUsageReader.UnprotectSecret(cfg.GeminiApiKeyProtected);
        return string.IsNullOrWhiteSpace(plain) ? null : plain.Trim();
    }

    public static string ProtectKey(string plain) => LocalAiCliUsageReader.ProtectSecret(plain.Trim());

    public bool HasKey => GetKey(_orch.Config) is not null;

    /// <summary>
    /// Sends one request and returns the answer text (or JSON when <paramref name="responseSchemaJson"/> is given).
    /// Throws <see cref="GeminiException"/> with a user-facing message on any failure.
    /// </summary>
    public async Task<string> GenerateAsync(string? systemInstruction, IReadOnlyList<GeminiPart> parts,
        string? responseSchemaJson = null, double temperature = 0.2, int maxOutputTokens = 2048, CancellationToken ct = default)
    {
        var cfg = _orch.Config;
        string lang = cfg.Language;
        bool en = lang == "en";
        string key = GetKey(cfg) ?? throw new GeminiException(en
            ? "Add your Gemini API key in Settings → Voice Assistant."
            : "Gemini API anahtarını Ayarlar → Sesli Asistan bölümüne ekle.");
        string model = Uri.EscapeDataString(string.IsNullOrWhiteSpace(cfg.GeminiModel) ? "gemini-2.5-flash" : cfg.GeminiModel.Trim());
        string body = GeminiJson.BuildRequest(systemInstruction, parts, responseSchemaJson, temperature, maxOutputTokens);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-goog-api-key", key);

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GeminiException(en ? "Could not reach Gemini. Check your connection." : "Gemini'ye ulaşılamadı. İnternet bağlantını kontrol et.");
        }
        using (resp)
        {
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string? detail = GeminiJson.ParseError(text);
                throw new GeminiException(resp.StatusCode switch
                {
                    HttpStatusCode.BadRequest when detail?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true =>
                        en ? "The Gemini API key is not valid." : "Gemini API anahtarı geçersiz.",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        en ? "The Gemini API key was rejected." : "Gemini API anahtarı reddedildi.",
                    (HttpStatusCode)429 =>
                        en ? "Gemini quota reached; try again in a minute." : "Gemini kotası doldu; bir dakika sonra tekrar dene.",
                    HttpStatusCode.NotFound =>
                        (en ? "Unknown Gemini model: " : "Bilinmeyen Gemini modeli: ") + cfg.GeminiModel,
                    _ => (en ? "Gemini error: " : "Gemini hatası: ") + (detail ?? ((int)resp.StatusCode).ToString())
                });
            }
            try
            {
                return GeminiJson.ParseText(text, lang);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new GeminiException(en ? "Gemini sent an unexpected response." : "Gemini beklenmeyen bir yanıt gönderdi.");
            }
        }
    }
}
