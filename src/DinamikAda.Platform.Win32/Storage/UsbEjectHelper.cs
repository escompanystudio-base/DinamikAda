using System.Runtime.InteropServices;

namespace DinamikAda.Platform.Win32.Storage;

/// <summary>
/// Resolves a drive letter to its removable parent PnP DevInst and performs native
/// Win32 safe removal via CM_Request_Device_EjectW without blocking dialogs.
/// </summary>
public static partial class UsbEjectHelper
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint DN_REMOVABLE = 0x00004000;

    private static readonly Guid GUID_DEVINTERFACE_DISK = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
    private static readonly nint INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        out STORAGE_DEVICE_NUMBER lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiGetClassDevsW(
        ref Guid ClassGuid,
        nint Enumerator,
        nint hwndParent,
        uint Flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(
        nint DeviceInfoSet,
        nint DeviceInfoData,
        ref Guid InterfaceClassGuid,
        uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceInterfaceDetailW(
        nint DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        nint DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint DeviceInfoSet);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Request_Device_EjectW(
        uint dnDevInst,
        out int pVetoType,
        char* pszVetoName,
        int ulNameLength,
        uint ulFlags);

    public static List<string> DriveLettersFromUnitMask(uint unitMask)
    {
        var drives = new List<string>();
        for (int i = 0; i < 26; i++)
        {
            if ((unitMask & (1u << i)) != 0)
            {
                drives.Add($"{(char)('A' + i)}:\\");
            }
        }
        return drives;
    }

    public static unsafe (bool Succeeded, string? ErrorMessage) TrySafeEjectDrive(string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return (false, "Empty drive path.");
        }

        char letter = char.ToUpperInvariant(driveRoot.Trim()[0]);
        if (letter is < 'A' or > 'Z')
        {
            return (false, $"Invalid drive letter: {driveRoot}");
        }

        if (!TryGetStorageDeviceNumber($@"\\.\{letter}:", out uint targetDevType, out uint targetDevNumber))
        {
            return (false, "Unable to query volume device number.");
        }

        Guid diskGuid = GUID_DEVINTERFACE_DISK;
        nint devInfo = SetupDiGetClassDevsW(ref diskGuid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == 0 || devInfo == INVALID_HANDLE_VALUE)
        {
            return (false, "SetupDiGetClassDevsW failed.");
        }

        try
        {
            uint index = 0;
            var ifaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            while (SetupDiEnumDeviceInterfaces(devInfo, 0, ref diskGuid, index, ref ifaceData))
            {
                index++;
                var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                _ = SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, 0, 0, out uint reqSize, ref devInfoData);
                if (reqSize <= 4 || reqSize > 4096) continue;

                nint detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, 0, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, detailBuf, reqSize, out _, ref devInfoData))
                    {
                        continue;
                    }

                    string? devPath = Marshal.PtrToStringUni(detailBuf + 4);
                    if (string.IsNullOrWhiteSpace(devPath)) continue;

                    if (TryGetStorageDeviceNumber(devPath, out uint devType, out uint devNum) &&
                        devType == targetDevType && devNum == targetDevNumber)
                    {
                        uint ejectInst = ResolveRemovableAncestor(devInfoData.DevInst);
                        char* vetoBuf = stackalloc char[260];
                        uint cr = CM_Request_Device_EjectW(ejectInst, out int vetoType, vetoBuf, 260, 0);
                        if (cr == 0 && vetoType == 0)
                        {
                            return (true, null);
                        }

                        string vetoName = new string(vetoBuf).TrimEnd('\0');
                        return (false, string.IsNullOrWhiteSpace(vetoName)
                            ? $"Device is busy (CR=0x{cr:X}, veto={vetoType})."
                            : $"Blocked by: {vetoName}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }

            return (false, "Matching removable disk device not found.");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    private static uint ResolveRemovableAncestor(uint diskDevInst)
    {
        uint current = diskDevInst;
        for (int depth = 0; depth < 6; depth++)
        {
            if (CM_Get_DevNode_Status(out uint status, out _, current, 0) == 0 &&
                (status & DN_REMOVABLE) != 0)
            {
                return current;
            }
            if (CM_Get_Parent(out uint parent, current, 0) != 0 || parent == 0)
            {
                break;
            }
            current = parent;
        }
        return CM_Get_Parent(out uint directParent, diskDevInst, 0) == 0 && directParent != 0
            ? directParent
            : diskDevInst;
    }

    private static bool TryGetStorageDeviceNumber(string devicePath, out uint deviceType, out uint deviceNumber)
    {
        deviceType = 0;
        deviceNumber = 0;
        nint h = CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h == 0 || h == INVALID_HANDLE_VALUE)
        {
            // Retry with GENERIC_READ if needed on certain storage stacks
            h = CreateFileW(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (h == 0 || h == INVALID_HANDLE_VALUE) return false;
        }

        try
        {
            if (DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, 0, 0, out var sdn, (uint)Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, 0))
            {
                deviceType = sdn.DeviceType;
                deviceNumber = sdn.DeviceNumber;
                return true;
            }
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
