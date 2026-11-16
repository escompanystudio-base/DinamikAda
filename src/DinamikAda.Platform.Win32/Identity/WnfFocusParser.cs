using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Identity;

public static class WnfFocusParser
{
    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
    public const ulong WnfShellQuietHoursActiveProfileChanged = 0x0D83063EA3BF1C75UL;

    public static FocusQuietProfile ParseQuietHoursBuffer(int ntStatus, ReadOnlySpan<byte> payload)
    {
        if (ntStatus != 0)
        {
            return FocusQuietProfile.Unknown;
        }

        if (payload.Length == 0)
        {
            return FocusQuietProfile.Off;
        }

        if (payload.Length < 4)
        {
            return FocusQuietProfile.Unknown;
        }

        uint val = BitConverter.ToUInt32(payload[..4]);
        return val switch
        {
            0 => FocusQuietProfile.Off,
            1 => FocusQuietProfile.PriorityOnly,
            2 => FocusQuietProfile.AlarmsOnly,
            _ => FocusQuietProfile.Unknown
        };
    }

    public static unsafe FocusQuietProfile QueryWindowsFocusProfile()
    {
        ulong stateName = WnfShellQuietHoursActiveProfileChanged;
        Span<byte> buf = stackalloc byte[16];
        uint size = (uint)buf.Length;
        fixed (byte* p = buf)
        {
            int status = NativeMethods.NtQueryWnfStateData(ref stateName, 0, 0, out _, p, ref size);
            if (status != 0)
            {
                return ParseQuietHoursBuffer(status, ReadOnlySpan<byte>.Empty);
            }

            int validBytes = (int)Math.Min(size, (uint)buf.Length);
            return ParseQuietHoursBuffer(status, buf[..validBytes]);
        }
    }
}
