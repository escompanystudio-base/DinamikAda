using System.Diagnostics;
using DinamikAda.Platform.Win32.Storage;

namespace DinamikAda.Modules.SystemControl;

public sealed record UsbDriveArrivalInfo(
    string DriveRoot,
    string DriveLetter,
    string VolumeLabel,
    string DisplayName,
    double TotalGb,
    double FreeGb,
    double UsedGb,
    bool IsRemovable);

public sealed class UsbDriveMonitorService
{
    public event EventHandler<UsbDriveArrivalInfo>? DriveArrived;
    public event EventHandler<string>? DriveRemoved;

    public static IReadOnlyList<string> DriveLettersFromUnitMask(uint unitMask) =>
        UsbEjectHelper.DriveLettersFromUnitMask(unitMask);

    public static UsbDriveArrivalInfo? BuildArrivalInfo(
        string? driveRoot,
        DriveType driveType,
        bool isReady,
        string? volumeLabel,
        long totalBytes,
        long availableFreeBytes)
    {
        if (!TryNormalizeDriveRoot(driveRoot, out string root, out string letter))
        {
            return null;
        }

        if (!isReady || totalBytes <= 0 || driveType is not (DriveType.Removable or DriveType.Fixed))
        {
            return null;
        }

        string label = string.IsNullOrWhiteSpace(volumeLabel) ? string.Empty : volumeLabel.Trim();
        string displayName = string.IsNullOrWhiteSpace(label) ? $"USB ({letter})" : $"{label} ({letter})";

        long clampedFree = Math.Clamp(availableFreeBytes, 0L, totalBytes);
        long usedBytes = totalBytes - clampedFree;

        double totalGb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double freeGb = Math.Round(clampedFree / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGb = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);

        return new UsbDriveArrivalInfo(
            DriveRoot: root,
            DriveLetter: letter,
            VolumeLabel: label,
            DisplayName: displayName,
            TotalGb: totalGb,
            FreeGb: freeGb,
            UsedGb: usedGb,
            IsRemovable: driveType == DriveType.Removable);
    }

    public UsbDriveArrivalInfo? TryBuildArrivalInfo(string driveRoot)
    {
        if (!TryNormalizeDriveRoot(driveRoot, out string root, out _))
        {
            return null;
        }

        try
        {
            var info = new DriveInfo(root);
            if (!info.IsReady)
            {
                return null;
            }

            return BuildArrivalInfo(
                root,
                info.DriveType,
                info.IsReady,
                info.VolumeLabel,
                info.TotalSize,
                info.AvailableFreeSpace);
        }
        catch
        {
            return null;
        }
    }

    public bool OpenDriveFolder(string driveRoot)
    {
        if (!TryNormalizeDriveRoot(driveRoot, out string root, out _))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(root))
            {
                return false;
            }

            using var proc = Process.Start(new ProcessStartInfo("explorer.exe", root)
            {
                UseShellExecute = true
            });
            return proc is not null;
        }
        catch
        {
            return false;
        }
    }

    public Task<(bool Succeeded, string? ErrorMessage)> SafeEjectAsync(string driveRoot)
    {
        if (!TryNormalizeDriveRoot(driveRoot, out string root, out _))
        {
            return Task.FromResult<(bool, string?)>((false, "Invalid drive path."));
        }

        return Task.Run(() => UsbEjectHelper.TrySafeEjectDrive(root));
    }

    public void HandleVolumeDeviceChange(bool isArrival, uint unitMask)
    {
        var drives = UsbEjectHelper.DriveLettersFromUnitMask(unitMask);
        foreach (string driveRoot in drives)
        {
            if (isArrival)
            {
                var info = TryBuildArrivalInfo(driveRoot);
                if (info is not null)
                {
                    DriveArrived?.Invoke(this, info);
                }
            }
            else
            {
                DriveRemoved?.Invoke(this, driveRoot);
            }
        }
    }

    public static bool TryNormalizeDriveRoot(string? driveRoot, out string normalizedRoot, out string driveLetter)
    {
        normalizedRoot = string.Empty;
        driveLetter = string.Empty;
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return false;
        }

        char ch = char.ToUpperInvariant(driveRoot.Trim()[0]);
        if (ch is < 'A' or > 'Z')
        {
            return false;
        }

        driveLetter = $"{ch}:";
        normalizedRoot = $"{ch}:\\";
        return true;
    }
}
