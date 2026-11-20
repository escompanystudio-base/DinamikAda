using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DinamikAda.Platform.Win32.Audio;
using DinamikAda.Platform.Win32.Interop;
using Windows.Media.SpeechRecognition;

namespace DinamikAda.Modules.Media;

/// <summary>A finished push-to-talk recording.</summary>
/// <param name="Wav">16 kHz, mono, 16-bit PCM WAV (header included); ready to send to a speech model.</param>
/// <param name="WindowsTranscript">Text from Windows speech recognition, when it was requested and produced text.</param>
/// <param name="PeakLevel">Loudest normalized level seen (0..1); near zero means silence.</param>
public sealed record MicCapture(byte[] Wav, TimeSpan Duration, string? WindowsTranscript, double PeakLevel)
{
    public bool IsSilent => PeakLevel < 0.04 || Duration < TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Push-to-talk microphone capture for the voice assistant: WASAPI shared-mode capture of the default communications
/// microphone, downmixed and resampled to 16 kHz mono in memory, with a live level for the UI and optional Windows
/// speech recognition running alongside (the offline fallback engine).
/// </summary>
public sealed class MicCaptureService : IDisposable
{
    private const int CLSCTX_ALL = 0x17;
    private const int eCapture = 1;
    private const int eCommunications = 2;
    private const int TargetRate = 16000;
    private static readonly Guid KsSubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid KsSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task<MicCapture?>? _task;
    private bool _disposed;

    /// <summary>Normalized input level (0..1), raised about 20 times a second from a worker thread.</summary>
    public event EventHandler<double>? LevelChanged;
    /// <summary>A user-facing reason the capture could not start.</summary>
    public event EventHandler<string>? Failed;

    public bool IsCapturing { get { lock (_sync) return _task is { IsCompleted: false }; } }

    /// <summary>Starts capturing. <paramref name="windowsDictationLanguage"/> (e.g. "tr-TR") also runs Windows recognition.</summary>
    public bool Start(string? windowsDictationLanguage = null, TimeSpan? maxDuration = null)
    {
        lock (_sync)
        {
            if (_disposed || _task is { IsCompleted: false }) return false;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var max = maxDuration ?? TimeSpan.FromMinutes(2);
            _task = Task.Run(() => CaptureLoop(ct, windowsDictationLanguage, max));
            return true;
        }
    }

    /// <summary>Stops and returns the recording (null when nothing was captured or the capture failed).</summary>
    public async Task<MicCapture?> StopAsync()
    {
        Task<MicCapture?>? task;
        lock (_sync)
        {
            task = _task;
            try { _cts?.Cancel(); } catch { }
        }
        if (task is null) return null;
        try { return await task.ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>Stops and discards the recording.</summary>
    public void Cancel()
    {
        lock (_sync) { try { _cts?.Cancel(); } catch { } }
    }

    private MicCapture? CaptureLoop(CancellationToken ct, string? dictationLang, TimeSpan max)
    {
        bool comInit = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint fmtPtr = 0;
        SpeechRecognizer? recognizer = null;
        var transcript = new StringBuilder();
        var pcm = new MemoryStream();
        double peak = 0;
        var sw = Stopwatch.StartNew();
        int sampleRate = 48000;
        try
        {
            int hr = NativeMethods.CoInitializeEx(0, 0x0);
            comInit = hr is 0 or 1;
            Type? enumType = Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_MMDeviceEnumerator);
            enumerator = enumType is not null ? Activator.CreateInstance(enumType) as IMMDeviceEnumerator : null;
            if (enumerator is null || enumerator.GetDefaultAudioEndpoint(eCapture, eCommunications, out device) != 0 || device is null)
            {
                Failed?.Invoke(this, "mic-not-found");
                return null;
            }
            Guid clientIid = CoreAudioGuids.IID_IAudioClient;
            if (device.Activate(ref clientIid, CLSCTX_ALL, 0, out object clientObj) != 0 || clientObj is not IAudioClient ac)
            {
                Failed?.Invoke(this, "mic-activate-failed");
                return null;
            }
            client = ac;
            if (client.GetMixFormat(out fmtPtr) != 0 || fmtPtr == 0)
            {
                Failed?.Invoke(this, "mic-format-failed");
                return null;
            }
            var wfx = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
            sampleRate = (int)Math.Clamp(wfx.nSamplesPerSec, 8000, 192000);
            int channels = Math.Max(1, (int)wfx.nChannels);
            int bits = Math.Max(8, (int)wfx.wBitsPerSample);
            bool isFloat = wfx.wFormatTag == 3;
            if (wfx.wFormatTag == 0xFFFE && wfx.cbSize >= 22)
            {
                var ext = Marshal.PtrToStructure<WaveFormatExtensible>(fmtPtr);
                isFloat = ext.SubFormat == KsSubtypeFloat || (ext.SubFormat != KsSubtypePcm && bits == 32);
            }
            Guid session = Guid.Empty;
            if (client.Initialize(0, 0, 1_000_000, 0, fmtPtr, ref session) != 0)
            {
                Failed?.Invoke(this, "mic-init-failed");
                return null;
            }
            Guid capIid = CoreAudioGuids.IID_IAudioCaptureClient;
            if (client.GetService(ref capIid, out object capObj) != 0 || capObj is not IAudioCaptureClient cc)
            {
                Failed?.Invoke(this, "mic-service-failed");
                return null;
            }
            capture = cc;

            Task<SpeechRecognizer?>? dictationTask = string.IsNullOrWhiteSpace(dictationLang)
                ? null
                : Task.Run(() => StartDictation(dictationLang!, transcript, ct));

            if (client.Start() != 0)
            {
                Failed?.Invoke(this, "mic-start-failed");
                return null;
            }
            int frameBytes = channels * (bits / 8);
            long lastLevel = 0;
            double levelAcc = 0;
            while (!ct.IsCancellationRequested && sw.Elapsed < max)
            {
                while (capture.GetNextPacketSize(out uint frames) == 0 && frames > 0)
                {
                    if (capture.GetBuffer(out nint data, out uint read, out uint flags, out _, out _) != 0) break;
                    try
                    {
                        int count = checked((int)read * frameBytes);
                        byte[] raw = new byte[count];
                        if (!isFloat && bits == 8) Array.Fill(raw, (byte)128);
                        if ((flags & 0x2) == 0 && count > 0 && data != 0) Marshal.Copy(data, raw, 0, count);
                        byte[] mono = VoiceNoteWavHelper.ConvertSamplesToPcm16(raw, channels, bits, isFloat);
                        if (mono.Length > 0)
                        {
                            pcm.Write(mono);
                            double rms = VoiceNoteWavHelper.ComputeNormalizedRms(mono);
                            levelAcc = Math.Max(levelAcc, rms);
                            peak = Math.Max(peak, rms);
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(read);
                    }
                }
                long now = sw.ElapsedMilliseconds;
                if (now - lastLevel >= 50)
                {
                    lastLevel = now;
                    LevelChanged?.Invoke(this, levelAcc);
                    levelAcc = 0;
                }
                Thread.Sleep(10);
            }
            try { client.Stop(); } catch { }

            if (dictationTask is not null)
            {
                // Give Windows recognition a moment to deliver the last phrase.
                if (dictationTask.Wait(800) && dictationTask.Result is { } rec)
                {
                    recognizer = rec;
                    try { rec.ContinuousRecognitionSession.StopAsync().AsTask().Wait(1200); } catch { }
                }
                else
                {
                    _ = dictationTask.ContinueWith(static t => { try { t.Result?.Dispose(); } catch { } }, TaskScheduler.Default);
                }
            }

            if (pcm.Length == 0) return null;
            byte[] resampled = ResampleTo16k(pcm.ToArray(), sampleRate);
            var wav = new byte[44 + resampled.Length];
            VoiceNoteWavHelper.BuildRiffHeader(TargetRate, 1, 16, resampled.Length).CopyTo(wav, 0);
            resampled.CopyTo(wav, 44);
            string text;
            lock (transcript) text = transcript.ToString().Trim();
            return new MicCapture(wav, sw.Elapsed, text.Length == 0 ? null : text, peak);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex.Message);
            return null;
        }
        finally
        {
            try { recognizer?.Dispose(); } catch { }
            if (fmtPtr != 0) NativeMethods.CoTaskMemFree(fmtPtr);
            if (capture is not null && Marshal.IsComObject(capture)) Marshal.ReleaseComObject(capture);
            if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
            if (comInit) NativeMethods.CoUninitialize();
            LevelChanged?.Invoke(this, 0);
        }
    }

    /// <summary>Linear-interpolation resample of 16-bit mono PCM to 16 kHz (speech quality).</summary>
    public static byte[] ResampleTo16k(byte[] pcm16Mono, int sourceRate)
    {
        int inSamples = pcm16Mono.Length / 2;
        if (inSamples == 0) return [];
        if (sourceRate == TargetRate) return pcm16Mono;
        double ratio = (double)sourceRate / TargetRate;
        int outSamples = (int)(inSamples / ratio);
        var output = new byte[outSamples * 2];
        for (int i = 0; i < outSamples; i++)
        {
            double src = i * ratio;
            int i0 = (int)src;
            int i1 = Math.Min(i0 + 1, inSamples - 1);
            double frac = src - i0;
            // Average the samples the output sample spans when downsampling (cheap low-pass).
            int span = Math.Max(1, (int)ratio);
            double acc = 0;
            for (int s = 0; s < span; s++)
            {
                int idx = Math.Min(i0 + s, inSamples - 1);
                acc += BitConverter.ToInt16(pcm16Mono, idx * 2);
            }
            double avg = acc / span;
            double next = BitConverter.ToInt16(pcm16Mono, i1 * 2);
            double v = span > 1 ? avg : avg + (next - avg) * frac;
            short sample = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(sample & 0xFF);
            output[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return output;
    }

    private static SpeechRecognizer? StartDictation(string languageTag, StringBuilder transcript, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return null;
            var lang = SpeechRecognizer.SupportedTopicLanguages
                .FirstOrDefault(l => string.Equals(l.LanguageTag, languageTag, StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognizer.SupportedTopicLanguages.FirstOrDefault(l =>
                    l.LanguageTag.StartsWith(languageTag.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (lang is null) return null;
            var rec = new SpeechRecognizer(lang);
            rec.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            rec.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
            {
                string t = args.Result?.Text?.Trim() ?? string.Empty;
                if (t.Length == 0) return;
                lock (transcript)
                {
                    if (transcript.Length > 0) transcript.Append(' ');
                    transcript.Append(t);
                }
            };
            if (!rec.CompileConstraintsAsync().AsTask().Wait(1500) || ct.IsCancellationRequested) { rec.Dispose(); return null; }
            if (!rec.ContinuousRecognitionSession.StartAsync().AsTask().Wait(1500)) { rec.Dispose(); return null; }
            return rec;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Language tags Windows speech recognition can use on this PC (for Settings).</summary>
    public static IReadOnlyList<string> WindowsDictationLanguages()
    {
        try { return SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToList(); }
        catch { return []; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
        }
    }
}
