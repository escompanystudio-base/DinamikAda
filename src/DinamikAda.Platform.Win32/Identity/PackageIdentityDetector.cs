using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Identity;

public sealed record PackageIdentityStatus(bool HasIdentity, string? PackageFullName);

public static unsafe class PackageIdentityDetector
{
    public static PackageIdentityStatus Detect()
    {
        uint len = 0;
        int rc = NativeMethods.GetCurrentPackageFullName(ref len, null);
        if (rc == NativeMethods.APPMODEL_ERROR_NO_PACKAGE)
        {
            return new PackageIdentityStatus(false, null);
        }

        if (len == 0 || len > 512)
        {
            return new PackageIdentityStatus(false, null);
        }

        char* buf = stackalloc char[(int)len];
        rc = NativeMethods.GetCurrentPackageFullName(ref len, buf);
        if (rc == 0)
        {
            string name = new string(buf).TrimEnd('\0');
            return new PackageIdentityStatus(!string.IsNullOrWhiteSpace(name), name);
        }

        return new PackageIdentityStatus(false, null);
    }
}
