using System.Runtime.InteropServices;

namespace DinamikAda.Platform.Win32.Audio;

/// <summary>
/// Wraps Windows.Media.Internal.AudioPolicyConfig (IAudioPolicyConfigFactory) for
/// per-application playback endpoint routing on Windows 10/11 without external dependencies.
/// </summary>
public static partial class AudioPolicyConfigClient
{
    private const string RuntimeClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const uint S_OK = 0;

    // 21H2 / Win11 builds (verified live on Windows 11 Build 26200)
    private static readonly Guid IID_AudioPolicyConfig21H2 = new("AB3D4648-E242-459F-B02F-541C70306324");
    // Earlier Win10 1803-21H1 builds
    private static readonly Guid IID_AudioPolicyConfigDownlevel = new("2A59116D-6C4F-45E0-A74F-707E3FEF9258");

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial nint WindowsGetStringRawBuffer(nint hstring, out uint length);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointDelegate(nint thisPtr, uint processId, int flow, int role, nint deviceIdHstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedDefaultAudioEndpointDelegate(nint thisPtr, uint processId, int flow, int role, out nint deviceIdHstring);

    public static bool TrySetProcessOutputEndpoint(int processId, string? mmDeviceId, out string? error)
    {
        error = null;
        if (processId <= 0)
        {
            error = "Invalid process ID.";
            return false;
        }

        if (!TryAcquireFactory(out nint factory, out int setSlot, out _))
        {
            error = "Windows per-app AudioPolicyConfig interface is unavailable on this OS build.";
            return false;
        }

        nint hDevice = 0;
        try
        {
            // EDataFlow.eRender = 0; apply to eConsole(0), eMultimedia(1), and eCommunications(2) like Windows Sound Settings.
            // Null HSTRING (0) clears the per-process override back to system default.
            if (!string.IsNullOrWhiteSpace(mmDeviceId))
            {
                string targetId = NormalizeDeviceIdForPolicy(mmDeviceId);
                int hsHr = WindowsCreateString(targetId, (uint)targetId.Length, out hDevice);
                if (hsHr != 0)
                {
                    error = $"WindowsCreateString failed (0x{hsHr:X8}).";
                    return false;
                }
            }

            nint vtbl = Marshal.ReadIntPtr(factory);
            var setFn = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointDelegate>(
                Marshal.ReadIntPtr(vtbl, setSlot * IntPtr.Size));

            int hr0 = setFn(factory, (uint)processId, 0, 0, hDevice);
            int hr1 = setFn(factory, (uint)processId, 0, 1, hDevice);
            int hr2 = setFn(factory, (uint)processId, 0, 2, hDevice);
            if (hr0 != 0 && hr1 != 0 && hr2 != 0)
            {
                error = $"SetPersistedDefaultAudioEndpoint failed (0x{hr0:X8}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (hDevice != 0) WindowsDeleteString(hDevice);
            if (factory != 0) Marshal.Release(factory);
        }
    }

    public static string? TryGetProcessOutputEndpoint(int processId)
    {
        if (processId <= 0) return null;
        if (!TryAcquireFactory(out nint factory, out _, out int getSlot))
        {
            return null;
        }

        nint hOut = 0;
        try
        {
            nint vtbl = Marshal.ReadIntPtr(factory);
            var getFn = Marshal.GetDelegateForFunctionPointer<GetPersistedDefaultAudioEndpointDelegate>(
                Marshal.ReadIntPtr(vtbl, getSlot * IntPtr.Size));

            int hr = getFn(factory, (uint)processId, 0, 1, out hOut);
            if (hr != 0)
            {
                hr = getFn(factory, (uint)processId, 0, 0, out hOut);
            }
            if (hr != 0 || hOut == 0) return null;

            nint buf = WindowsGetStringRawBuffer(hOut, out uint len);
            if (buf == 0 || len == 0) return null;
            return Marshal.PtrToStringUni(buf, (int)len);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOut != 0) WindowsDeleteString(hOut);
            if (factory != 0) Marshal.Release(factory);
        }
    }

    public static string? ExtractEndpointGuidToken(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return null;
        int idx = rawId.LastIndexOf("}.", StringComparison.Ordinal);
        string core = idx >= 0 && idx + 2 < rawId.Length ? rawId[(idx + 2)..] : rawId;
        int endBrace = core.IndexOf('}');
        if (core.StartsWith('{') && endBrace > 0)
        {
            return core[..(endBrace + 1)];
        }
        return rawId.Trim();
    }

    private static string NormalizeDeviceIdForPolicy(string mmDeviceId)
    {
        // AudioPolicyConfig expects either the full MMDevice ID or the DEVINTERFACE_AUDIO_RENDER suffix;
        // if caller passes standard "{0.0.0.00000000}.{guid}", format as "\\?\SWD#MMDEVAPI#" + id + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}"
        string trimmed = mmDeviceId.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        if (trimmed.StartsWith("{0.", StringComparison.Ordinal))
        {
            return $@"\\?\SWD#MMDEVAPI#{trimmed}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";
        }
        return trimmed;
    }

    private static bool TryAcquireFactory(out nint factory, out int setSlot, out int getSlot)
    {
        factory = 0;
        setSlot = 25;
        getSlot = 26;

        int hsHr = WindowsCreateString(RuntimeClassName, (uint)RuntimeClassName.Length, out nint hClass);
        if (hsHr != 0 || hClass == 0) return false;

        try
        {
            Guid iid21 = IID_AudioPolicyConfig21H2;
            if (RoGetActivationFactory(hClass, ref iid21, out factory) == S_OK && factory != 0)
            {
                setSlot = 25;
                getSlot = 26;
                return true;
            }

            Guid iidDown = IID_AudioPolicyConfigDownlevel;
            if (RoGetActivationFactory(hClass, ref iidDown, out factory) == S_OK && factory != 0)
            {
                setSlot = 24;
                getSlot = 25;
                return true;
            }

            return false;
        }
        finally
        {
            WindowsDeleteString(hClass);
        }
    }
}
