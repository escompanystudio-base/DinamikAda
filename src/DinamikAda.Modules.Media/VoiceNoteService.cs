using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Audio;
using DinamikAda.Platform.Win32.Interop;
using Windows.Media.SpeechRecognition;
using ActivityKind = DinamikAda.Core.Abstractions.ActivityKind;

namespace DinamikAda.Modules.Media;

public sealed record VoiceNoteCompletedResult(
    string WavPath,
    TimeSpan Duration,
    string? Transcript);

/// <summary>
/// Pure helper & WASAPI eCapture microphone recorder writing real 16-bit PCM WAV files
/// with live 8-band waveform feedback and optional Windows speech-to-text transcription.
/// </summary>
public static class VoiceNoteWavHelper
{
    public static byte[] ConvertSamplesToPcm16(
        ReadOnlySpan<byte> rawFrameBytes,
        int channels,
        int bitsPerSample,
        bool isFloat)
    {
        if (channels is <= 0 or > 32 || rawFrameBytes.IsEmpty ||
            (isFloat ? bitsPerSample is not (32 or 64) : bitsPerSample is not (8 or 16 or 24 or 32))) return [];

        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = channels * bytesPerSample;
        if (frameBytes <= 0) return [];

        int frameCount = rawFrameBytes.Length / frameBytes;
        var pcm16 = new byte[frameCount * 2]; // Downmix to mono 16-bit PCM for compact clear notes + speech recognition

        for (int f = 0; f < frameCount; f++)
        {
            double sum = 0.0;
            int frameOffset = f * frameBytes;
            for (int c = 0; c < channels; c++)
            {
                int off = frameOffset + c * bytesPerSample;
                double sample = 0.0;
                if (isFloat && bitsPerSample == 32 && off + 4 <= rawFrameBytes.Length)
                {
                    sample = BitConverter.ToSingle(rawFrameBytes.Slice(off, 4));
                }
                else if (isFloat && bitsPerSample == 64)
                {
                    sample = BitConverter.ToDouble(rawFrameBytes.Slice(off, 8));
                }
                else if (bitsPerSample == 8)
                {
                    sample = (rawFrameBytes[off] - 128) / 128.0;
                }
                else if (bitsPerSample == 16 && off + 2 <= rawFrameBytes.Length)
                {
                    sample = BitConverter.ToInt16(rawFrameBytes.Slice(off, 2)) / 32768.0;
                }
                else if (bitsPerSample == 24 && off + 3 <= rawFrameBytes.Length)
                {
                    int val = rawFrameBytes[off] | (rawFrameBytes[off + 1] << 8) | ((sbyte)rawFrameBytes[off + 2] << 16);
                    sample = val / 8388608.0;
                }
                else if (bitsPerSample == 32 && off + 4 <= rawFrameBytes.Length)
                {
                    sample = BitConverter.ToInt32(rawFrameBytes.Slice(off, 4)) / 2147483648.0;
                }
                sum += double.IsFinite(sample) ? sample : 0.0;
            }

            double mono = Math.Clamp(sum / channels, -1.0, 1.0);
            short pcmVal = (short)Math.Round(mono * 32767.0);
            pcm16[f * 2] = (byte)(pcmVal & 0xFF);
            pcm16[f * 2 + 1] = (byte)((pcmVal >> 8) & 0xFF);
        }

        return pcm16;
    }

    public static byte[] BuildRiffHeader(int sampleRateHz, short channels, short bitsPerSample, int dataBytes)
    {
        var hdr = new byte[44];
        Encoding.ASCII.GetBytes("RIFF", hdr.AsSpan(0, 4));
        BitConverter.TryWriteBytes(hdr.AsSpan(4, 4), Math.Max(0, dataBytes) + 36);
        Encoding.ASCII.GetBytes("WAVEfmt ", hdr.AsSpan(8, 8));
        BitConverter.TryWriteBytes(hdr.AsSpan(16, 4), 16); // fmt chunk size
        BitConverter.TryWriteBytes(hdr.AsSpan(20, 2), (short)1); // PCM
        BitConverter.TryWriteBytes(hdr.AsSpan(22, 2), channels);
        BitConverter.TryWriteBytes(hdr.AsSpan(24, 4), sampleRateHz);
        int byteRate = sampleRateHz * channels * (bitsPerSample / 8);
        BitConverter.TryWriteBytes(hdr.AsSpan(28, 4), byteRate);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        BitConverter.TryWriteBytes(hdr.AsSpan(32, 2), blockAlign);
        BitConverter.TryWriteBytes(hdr.AsSpan(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data", hdr.AsSpan(36, 4));
        BitConverter.TryWriteBytes(hdr.AsSpan(40, 4), Math.Max(0, dataBytes));
        return hdr;
    }

    public static double ComputeNormalizedRms(ReadOnlySpan<byte> pcm16Bytes)
    {
        if (pcm16Bytes.Length < 2) return 0.0;
        int samples = pcm16Bytes.Length / 2;
        double sumSq = 0.0;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(pcm16Bytes.Slice(i * 2, 2));
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        return Math.Clamp(Math.Sqrt(sumSq / samples) * 3.2, 0.0, 1.0);
    }
}

public sealed class VoiceNoteService : ILiveActivitySource, IDisposable
{
    private const int CLSCTX_ALL = 0x17;
    private const int eCapture = 1;
    private const int eCommunications = 2;
    private static readonly Guid KsSubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid KsSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly string _notesDir;
    private readonly object _sync = new();
    private CancellationTokenSource? _recordCts;
    private Task<VoiceNoteCompletedResult?>? _recordTask;
    private volatile bool _isRecording;
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private LiveActivityState? _currentActivity;
    private readonly double[] _waveformBands = new double[8];
    private string? _lastSavedWavPath;

    public string SourceId => "voicenote:recorder";
    public string Language { get; set; } = "tr";
    public string NotesDirectory => _notesDir;
    public string? LastSavedWavPath { get { lock (_sync) return _lastSavedWavPath; } }
    public bool IsRecording => _isRecording && !_stopRequested;
    public LiveActivityState? CurrentActivity { get { lock (_sync) return _currentActivity; } }
    public double[] WaveformBandsCopy
    {
        get { lock (_sync) return (double[])_waveformBands.Clone(); }
    }

    public event EventHandler<LiveActivityState?>? ActivityChanged;
    public event EventHandler<double[]>? WaveformUpdated;
    public event EventHandler<VoiceNoteCompletedResult>? NoteCompleted;
    public event EventHandler<string>? RecordingFailed;

    public VoiceNoteService(string? notesDir = null)
    {
        _notesDir = notesDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "VoiceNotes");
    }

    public bool TryOpenNotesFolder()
    {
        try
        {
            Directory.CreateDirectory(_notesDir);
            Process.Start(new ProcessStartInfo("explorer.exe", _notesDir) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ToggleRecording()
    {
        if (IsRecording)
        {
            _ = StopRecordingAsync();
            return false;
        }
        return StartRecording();
    }

    public bool StartRecording()
    {
        lock (_sync)
        {
            if (_disposed || _isRecording) return false;
            _stopRequested = false;
            _isRecording = true;
            _recordCts = new CancellationTokenSource();
            var ct = _recordCts.Token;
            _recordTask = Task.Run(() => RecordLoop(ct));
            PublishActivityLocked(0.0, TimeSpan.Zero);
            return true;
        }
    }

    public async Task<VoiceNoteCompletedResult?> StopRecordingAsync()
    {
        Task<VoiceNoteCompletedResult?>? task;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (!_isRecording) return null;
            _stopRequested = true;
            Array.Clear(_waveformBands);
            _currentActivity = null;
            ActivityChanged?.Invoke(this, null);
            cts = _recordCts;
            task = _recordTask;
        }

        try { cts?.Cancel(); } catch { }
        VoiceNoteCompletedResult? result = null;
        if (task is not null)
        {
            try { result = await task.ConfigureAwait(false); } catch { }
        }
        return result;
    }

    private VoiceNoteCompletedResult? RecordLoop(CancellationToken ct)
    {
        string wavPath = Path.Combine(_notesDir, $"not_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.wav");

        bool comInit = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? capDevice = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? capClient = null;
        nint fmtPtr = 0;
        SpeechRecognizer? speechRec = null;
        bool saved = false;

        int sampleRate = 48000;
        int dataBytesWritten = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(_notesDir);
            int hrInit = NativeMethods.CoInitializeEx(0, 0x0 /* COINIT_MULTITHREADED */);
            comInit = hrInit is 0 or 1;

            Type? enumType = Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_MMDeviceEnumerator);
            enumerator = enumType is not null ? Activator.CreateInstance(enumType) as IMMDeviceEnumerator : null;
            if (enumerator is null || enumerator.GetDefaultAudioEndpoint(eCapture, eCommunications, out capDevice) != 0 || capDevice is null)
            {
                RecordingFailed?.Invoke(this, Strings.Pick(Language, "Mikrofon aygıtı bulunamadı.", "Microphone device not found."));
                return null;
            }

            Guid clientIid = CoreAudioGuids.IID_IAudioClient;
            if (capDevice.Activate(ref clientIid, CLSCTX_ALL, 0, out object clientObj) != 0 || clientObj is not IAudioClient ac)
            {
                RecordingFailed?.Invoke(this, Strings.Pick(Language, "Ses yakalama istemcisi başlatılamadı.", "Failed to activate audio capture client."));
                return null;
            }
            audioClient = ac;

            if (audioClient.GetMixFormat(out fmtPtr) != 0 || fmtPtr == 0)
            {
                RecordingFailed?.Invoke(this, Strings.Pick(Language, "Mikrofon ses biçimi okunamadı.", "Failed to read mic mix format."));
                return null;
            }

            var wfx = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
            sampleRate = (int)Math.Clamp(wfx.nSamplesPerSec, 8000, 192000);
            int channels = Math.Max(1, (int)wfx.nChannels);
            int bitsPerSample = Math.Max(8, (int)wfx.wBitsPerSample);
            bool isFloat = wfx.wFormatTag == 3;
            if (wfx.wFormatTag == 0xFFFE && wfx.cbSize >= 22)
            {
                var wfext = Marshal.PtrToStructure<WaveFormatExtensible>(fmtPtr);
                isFloat = wfext.SubFormat == KsSubtypeFloat;
                if (wfext.SubFormat != KsSubtypeFloat && wfext.SubFormat != KsSubtypePcm)
                {
                    isFloat = wfx.wBitsPerSample == 32;
                }
            }

            Guid sessionGuid = Guid.Empty;
            if (audioClient.Initialize(0 /* AUDCLNT_SHAREMODE_SHARED */, 0, 1_000_000, 0, fmtPtr, ref sessionGuid) != 0)
            {
                RecordingFailed?.Invoke(this, Strings.Pick(Language, "Mikrofon akışı başlatılamadı.", "Failed to initialize microphone capture stream."));
                return null;
            }

            Guid capIid = CoreAudioGuids.IID_IAudioCaptureClient;
            if (audioClient.GetService(ref capIid, out object capObj) != 0 || capObj is not IAudioCaptureClient cc)
            {
                RecordingFailed?.Invoke(this, Strings.Pick(Language, "Mikrofon servisi alınamadı.", "Failed to acquire capture service."));
                return null;
            }
            capClient = cc;

            using (var fs = new FileStream(wavPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Write(VoiceNoteWavHelper.BuildRiffHeader(sampleRate, 1, 16, 0));
                if (audioClient.Start() != 0)
                {
                    RecordingFailed?.Invoke(this, Strings.Pick(Language, "Mikrofon kaydı başlatılamadı.", "Failed to start microphone recording."));
                    return null;
                }

                int frameBytes = channels * (bitsPerSample / 8);
                long lastUiTick = Environment.TickCount64;

                var transcriptSb = new StringBuilder();
                Task<SpeechRecognizer?> dictationTask = Task.Run(() => TryStartContinuousDictation(ct, transcriptSb));

                while (!ct.IsCancellationRequested && !_stopRequested && !_disposed && sw.Elapsed < TimeSpan.FromMinutes(10))
                {
                    while (capClient.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames > 0)
                    {
                        if (capClient.GetBuffer(out nint dataPtr, out uint framesRead, out uint flags, out _, out _) != 0)
                            break;

                        try
                        {
                            int byteCount = checked((int)framesRead * frameBytes);
                            byte[] raw = new byte[byteCount];
                            if (!isFloat && bitsPerSample == 8) Array.Fill(raw, (byte)128);
                            if ((flags & 0x2 /* AUDCLNT_BUFFERFLAGS_SILENT */) == 0 && byteCount > 0 && dataPtr != 0)
                            {
                                Marshal.Copy(dataPtr, raw, 0, byteCount);
                            }

                            byte[] pcm16 = VoiceNoteWavHelper.ConvertSamplesToPcm16(raw, channels, bitsPerSample, isFloat);
                            if (pcm16.Length > 0)
                            {
                                fs.Write(pcm16);
                                dataBytesWritten += pcm16.Length;
                                double rms = VoiceNoteWavHelper.ComputeNormalizedRms(pcm16);
                                UpdateWaveformBand(rms);
                            }
                        }
                        finally
                        {
                            capClient.ReleaseBuffer(framesRead);
                        }
                    }

                    long now = Environment.TickCount64;
                    if (!_stopRequested && !ct.IsCancellationRequested && now - lastUiTick >= 65)
                    {
                        lastUiTick = now;
                        double[] snapshot;
                        lock (_sync)
                        {
                            if (_stopRequested) continue;
                            snapshot = (double[])_waveformBands.Clone();
                            PublishActivityLocked(snapshot[^1], sw.Elapsed);
                        }
                        WaveformUpdated?.Invoke(this, snapshot);
                    }

                    Thread.Sleep(15);
                }

                try { audioClient.Stop(); } catch { }

                fs.Position = 0;
                fs.Write(VoiceNoteWavHelper.BuildRiffHeader(sampleRate, 1, 16, dataBytesWritten));
                fs.Flush();

                if (dictationTask.IsCompletedSuccessfully)
                {
                    speechRec = dictationTask.Result;
                    if (speechRec is not null)
                    {
                        try { speechRec.ContinuousRecognitionSession.StopAsync().AsTask().Wait(180); } catch { }
                        speechRec.Dispose();
                        speechRec = null;
                    }
                }
                else
                {
                    // Never block Stop waiting on WinRT SpeechRecognizer init; clean it up async when it settles.
                    _ = dictationTask.ContinueWith(static t =>
                    {
                        try
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                t.Result?.Dispose();
                            }
                        }
                        catch { }
                    }, TaskScheduler.Default);
                }

                if (dataBytesWritten == 0)
                {
                    try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                    return null;
                }

                string transcriptText;
                lock (transcriptSb) transcriptText = transcriptSb.ToString().Trim();
                string? finalTranscript = string.IsNullOrWhiteSpace(transcriptText) ? null : transcriptText;
                var completed = new VoiceNoteCompletedResult(wavPath, sw.Elapsed, finalTranscript);
                lock (_sync)
                {
                    _lastSavedWavPath = wavPath;
                }
                saved = true;
                NoteCompleted?.Invoke(this, completed);
                return completed;
            }
        }
        catch (Exception ex)
        {
            RecordingFailed?.Invoke(this, ex.Message);
            return null;
        }
        finally
        {
            try { speechRec?.Dispose(); } catch { }
            if (!saved)
            {
                try { File.Delete(wavPath); } catch { }
            }
            if (fmtPtr != 0) NativeMethods.CoTaskMemFree(fmtPtr);
            if (capClient is not null && Marshal.IsComObject(capClient)) Marshal.ReleaseComObject(capClient);
            if (audioClient is not null && Marshal.IsComObject(audioClient)) Marshal.ReleaseComObject(audioClient);
            if (capDevice is not null && Marshal.IsComObject(capDevice)) Marshal.ReleaseComObject(capDevice);
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
            if (comInit) NativeMethods.CoUninitialize();

            lock (_sync)
            {
                _stopRequested = false;
                _isRecording = false;
                Array.Clear(_waveformBands);
                _currentActivity = null;
                ActivityChanged?.Invoke(this, null);
                _recordCts?.Dispose();
                _recordCts = null;
            }
        }
    }

    private void UpdateWaveformBand(double rms)
    {
        lock (_sync)
        {
            for (int i = 0; i < _waveformBands.Length - 1; i++)
            {
                _waveformBands[i] = _waveformBands[i + 1];
            }
            _waveformBands[^1] = Math.Clamp(rms, 0.06, 1.0);
        }
    }

    private void PublishActivityLocked(double level, TimeSpan elapsed)
    {
        string timerStr = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        var next = new LiveActivityState(
            SourceId: SourceId,
            Kind: ActivityKind.VoiceNote,
            Priority: (int)ActivityKind.VoiceNote,
            PrimaryText: $"{Strings.VoiceNoteRecording(Language)} • {timerStr}",
            SecondaryText: Strings.VoiceNoteStopBtn(Language),
            AccentHex: "#FF453A",
            Progress01: Math.Clamp(level, 0.1, 1.0),
            IsCritical: true,
            HasMicActive: true,
            IconGlyph: "\uE720",
            TargetTab: ExpandedTabId.Audio);
        _currentActivity = next;
        ActivityChanged?.Invoke(this, next);
    }

    private static SpeechRecognizer? TryStartContinuousDictation(CancellationToken ct, StringBuilder transcriptSb)
    {
        try
        {
            if (ct.IsCancellationRequested || SpeechRecognizer.SupportedTopicLanguages.Count == 0) return null;
            var rec = new SpeechRecognizer();
            rec.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
            {
                string t = args.Result?.Text?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(t))
                {
                    lock (transcriptSb)
                    {
                        if (transcriptSb.Length > 0) transcriptSb.Append(' ');
                        transcriptSb.Append(t);
                    }
                }
            };
            var compileTask = rec.CompileConstraintsAsync().AsTask();
            if (!compileTask.Wait(1200) || ct.IsCancellationRequested)
            {
                rec.Dispose();
                return null;
            }
            var startTask = rec.ContinuousRecognitionSession.StartAsync().AsTask();
            if (!startTask.Wait(1200) || ct.IsCancellationRequested)
            {
                rec.Dispose();
                return null;
            }
            return rec;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _recordCts?.Cancel(); } catch { }
        }
    }
}
