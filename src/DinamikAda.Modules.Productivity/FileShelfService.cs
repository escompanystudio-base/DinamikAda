using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DinamikAda.Modules.Productivity;

public sealed record CleanImageResult(
    bool Succeeded,
    string? CleanFilePath,
    long OriginalBytes,
    long CleanBytes,
    string? ErrorMessage);

[ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataTransferManagerInterop
{
    [PreserveSig] int GetForWindow(nint appWindow, ref Guid riid, out nint dataTransferManager);
    [PreserveSig] int ShowShareUIForWindow(nint appWindow);
}

public sealed class FileShelfService
{
    private static readonly Guid IID_IDataTransferManager = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private static WeakReference<FileShelfService>? _activeService;
    private static int _noteCounter;

    private readonly string _shelfDir;
    private readonly string _manifestPath;
    private readonly object _sync = new();
    private readonly object _shareSync = new();
    private List<ShelfItem> _items = new();
    private nint _shareHwnd;
    private DataTransferManager? _shareManager;
    private TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? _shareHandler;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint pidl);

    [DllImport("shell32.dll")]
    private static extern nint ILFindLastID(nint pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        nint pidlFolder,
        uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] nint[] apidl,
        uint dwFlags);

    public event EventHandler<IReadOnlyList<ShelfItem>>? ItemsChanged;

    public FileShelfService(string? shelfDir = null)
    {
        _shelfDir = shelfDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "Shelf");
        _manifestPath = Path.Combine(_shelfDir, "shelf.json");
        Load();
        _activeService = new WeakReference<FileShelfService>(this);
    }

    public IReadOnlyList<ShelfItem> GetItems()
    {
        lock (_sync) return _items.ToList();
    }

    public int AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        lock (_sync)
        {
            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    string full = Path.GetFullPath(raw);
                    if (!File.Exists(full) && !Directory.Exists(full)) continue;
                    if (_items.Any(x => string.Equals(x.FullPath, full, StringComparison.OrdinalIgnoreCase))) continue;

                    bool isDir = Directory.Exists(full);
                    long size = isDir ? 0 : new FileInfo(full).Length;
                    string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(name)) name = full;

                    _items.Insert(0, new ShelfItem(
                        Guid.NewGuid().ToString("N")[..10],
                        name,
                        full,
                        isDir,
                        size,
                        DateTimeOffset.UtcNow));
                    added++;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    // One stale or inaccessible drag item must not reject the remaining files.
                }
            }

            if (added > 0) SaveLocked();
        }

        if (added > 0) ItemsChanged?.Invoke(this, GetItems());
        return added;
    }

    public string StashTextAsNoteFile(string text, string? titlePrefix = null)
    {
        string file = PrepareNotePath(titlePrefix);
        File.WriteAllText(file, text);
        AddPaths([file]);
        return file;
    }

    public async Task<string> StashTextAsNoteFileAsync(string text, string? titlePrefix = null, CancellationToken ct = default)
    {
        string file = PrepareNotePath(titlePrefix);
        await File.WriteAllTextAsync(file, text, ct).ConfigureAwait(false);
        AddPaths([file]);
        return file;
    }

    private string PrepareNotePath(string? titlePrefix)
    {
        Directory.CreateDirectory(_shelfDir);
        string rawPrefix = string.IsNullOrWhiteSpace(titlePrefix) ? "Note" : titlePrefix.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        string safePrefix = new(rawPrefix.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(safePrefix)) safePrefix = "Note";
        int seq = Interlocked.Increment(ref _noteCounter);
        return Path.Combine(_shelfDir, $"{safePrefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{seq}.txt");
    }

    public void Remove(string id)
    {
        bool removed;
        lock (_sync)
        {
            removed = _items.RemoveAll(x => x.Id == id) > 0;
            if (removed) SaveLocked();
        }
        if (removed) ItemsChanged?.Invoke(this, GetItems());
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
            SaveLocked();
        }
        ItemsChanged?.Invoke(this, Array.Empty<ShelfItem>());
    }

    public string? CreateZipArchive(string? destinationFolder = null)
    {
        try
        {
            List<ShelfItem> snapshot = GetItems().ToList();
            return BuildZipArchiveCore(snapshot, destinationFolder, CancellationToken.None);
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> CreateZipArchiveAsync(string? destinationFolder = null, CancellationToken ct = default)
    {
        List<ShelfItem> snapshot = GetItems().ToList();
        if (snapshot.Count == 0) return Task.FromResult<string?>(null);
        return Task.Run(() => BuildZipArchiveCore(snapshot, destinationFolder, ct), ct);
    }

    public CleanImageResult CompressAndStripExifCopy(string sourcePath)
    {
        try
        {
            return Task.Run(() => CompressAndStripExifCopyCoreAsync(sourcePath, CancellationToken.None))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return new CleanImageResult(false, null, 0, 0, ex.Message);
        }
    }

    public Task<CleanImageResult> CompressAndStripExifCopyAsync(string sourcePath, CancellationToken ct = default)
    {
        return CompressAndStripExifCopyCoreAsync(sourcePath, ct);
    }

    private async Task<CleanImageResult> CompressAndStripExifCopyCoreAsync(string sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new CleanImageResult(false, null, 0, 0, "Source path is empty.");
        }

        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            return new CleanImageResult(false, null, 0, 0, ex.Message);
        }

        if (!File.Exists(fullSource))
        {
            return new CleanImageResult(false, null, 0, 0, "Source image file does not exist.");
        }

        long originalBytes = new FileInfo(fullSource).Length;
        string ext = Path.GetExtension(fullSource);
        bool isJpeg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                      ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        bool isPng = ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
        if (!isJpeg && !isPng)
        {
            return new CleanImageResult(false, null, originalBytes, 0, "Only .png, .jpg, and .jpeg images are supported.");
        }

        ct.ThrowIfCancellationRequested();

        string dir = Path.GetDirectoryName(fullSource) ?? _shelfDir;
        string stem = Path.GetFileNameWithoutExtension(fullSource);
        string tmpPath = Path.Combine(dir, $".dinamikada_clean_{Guid.NewGuid():N}{ext}.tmp");

        try
        {
            using var encodedStream = new InMemoryRandomAccessStream();
            await using (var inputFs = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            using (var inputRas = inputFs.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(inputRas).AsTask(ct).ConfigureAwait(false);
                BitmapAlphaMode alphaMode = isJpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight;

                SoftwareBitmap decoded;
                try
                {
                    decoded = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        alphaMode,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    decoded = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        alphaMode,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
                }

                using (decoded)
                {
                    BitmapEncoder encoder;
                    if (isJpeg)
                    {
                        var props = new BitmapPropertySet
                        {
                            { "ImageQuality", new BitmapTypedValue(0.84f, Windows.Foundation.PropertyType.Single) }
                        };
                        encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encodedStream, props).AsTask(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encodedStream).AsTask(ct).ConfigureAwait(false);
                    }

                    encoder.IsThumbnailGenerated = false;
                    encoder.SetSoftwareBitmap(decoded);
                    await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
                }
            }

            encodedStream.Seek(0);
            await using (var outFs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            using (var readStream = encodedStream.AsStreamForRead())
            {
                await readStream.CopyToAsync(outFs, ct).ConfigureAwait(false);
            }

            string cleanPath = MoveTempToUniqueCleanPath(tmpPath, dir, stem, ext);
            long cleanBytes = new FileInfo(cleanPath).Length;
            AddPaths([cleanPath]);
            return new CleanImageResult(true, cleanPath, originalBytes, cleanBytes, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            return new CleanImageResult(false, null, originalBytes, 0, ex.Message);
        }
    }

    private static string MoveTempToUniqueCleanPath(string tmpPath, string dir, string stem, string ext)
    {
        int suffix = 1;
        while (true)
        {
            string fileName = suffix == 1 ? $"{stem}_clean{ext}" : $"{stem}_clean_{suffix}{ext}";
            string candidate = Path.Combine(dir, fileName);
            suffix++;
            if (File.Exists(candidate)) continue;
            try
            {
                File.Move(tmpPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Retry next numeric suffix on concurrent creation.
            }
        }
    }

    private string? BuildZipArchiveCore(List<ShelfItem> snapshot, string? destinationFolder, CancellationToken ct)
    {
        if (snapshot.Count == 0) return null;

        string targetDir = string.IsNullOrWhiteSpace(destinationFolder) ? _shelfDir : destinationFolder;
        Directory.CreateDirectory(targetDir);
        int seq = Interlocked.Increment(ref _noteCounter);
        string zipPath = Path.Combine(targetDir, $"DinamikAda-Shelf-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{seq}.zip");
        string tmpPath = zipPath + ".tmp";

        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in snapshot)
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(item.FullPath))
                    {
                        string entryName = MakeUniqueEntryName(Path.GetFileName(item.FullPath), usedNames);
                        archive.CreateEntryFromFile(item.FullPath, entryName, CompressionLevel.Optimal);
                    }
                    else if (Directory.Exists(item.FullPath))
                    {
                        string rootName = Path.GetFileName(item.FullPath.TrimEnd(Path.DirectorySeparatorChar));
                        if (string.IsNullOrWhiteSpace(rootName)) rootName = "Drive";
                        rootName = MakeUniqueEntryName(rootName, usedNames);
                        usedNames.Add(rootName + "/");
                        archive.CreateEntry(rootName + "/");
                        foreach (string f in Directory.EnumerateFiles(item.FullPath, "*", new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            AttributesToSkip = System.IO.FileAttributes.ReparsePoint
                        }))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(tmpPath), StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFullPath(f), Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase)) continue;
                            string rel = Path.GetRelativePath(item.FullPath, f);
                            string entryName = MakeUniqueEntryName(Path.Combine(rootName, rel).Replace('\\', '/'), usedNames);
                            archive.CreateEntryFromFile(f, entryName, CompressionLevel.Optimal);
                        }
                    }
                }
            }

            File.Move(tmpPath, zipPath, overwrite: true);
            return zipPath;
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    public void ClearShareWindow(nint hwnd = 0)
    {
        lock (_shareSync)
        {
            if (hwnd == 0 || _shareHwnd == hwnd)
            {
                if (_shareManager is not null && _shareHandler is not null)
                {
                    try { _shareManager.DataRequested -= _shareHandler; } catch { }
                }
                _shareManager = null;
                _shareHandler = null;
                _shareHwnd = 0;
            }
        }
    }

    public bool TryShowShareUi(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            ClearShareWindow();
            return TryOpenExplorerShareFallback();
        }

        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            lock (_shareSync)
            {
                if (_shareManager is not null && (_shareHwnd != hwnd || !NativeMethods.IsWindow(_shareHwnd)))
                {
                    if (_shareHandler is not null)
                    {
                        try { _shareManager.DataRequested -= _shareHandler; } catch { }
                    }
                    _shareManager = null;
                    _shareHandler = null;
                    _shareHwnd = 0;
                }

                if (_shareManager is null)
                {
                    Guid iid = IID_IDataTransferManager;
                    int hr = interop.GetForWindow(hwnd, ref iid, out nint dtmPtr);
                    if (hr == 0 && dtmPtr != 0)
                    {
                        try
                        {
                            _shareManager = DataTransferManager.FromAbi(dtmPtr);
                        }
                        finally
                        {
                            Marshal.Release(dtmPtr);
                        }

                        if (_shareManager is not null)
                        {
                            _shareHandler = OnShareDataRequested;
                            _shareManager.DataRequested += _shareHandler;
                            _shareHwnd = hwnd;
                        }
                    }
                }
            }

            if (_shareManager is not null)
            {
                int showHr = interop.ShowShareUIForWindow(hwnd);
                if (showHr == 0) return true;
                ClearShareWindow(hwnd);
            }
        }
        catch
        {
            ClearShareWindow(hwnd);
            // Fall through to Explorer selection fallback when unpackaged/Share contract is unavailable.
        }

        return TryOpenExplorerShareFallback();
    }

    public static bool TryShowWindowsShareUi(nint hwnd)
    {
        if (hwnd == 0) return false;
        if (_activeService is not null && _activeService.TryGetTarget(out var active))
        {
            return active.TryShowShareUi(hwnd);
        }

        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void OnShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            var items = GetItems();
            args.Request.Data.Properties.Title = items.Count == 1
                ? items[0].Name
                : $"DinamikAda Shelf ({items.Count})";
            args.Request.Data.Properties.Description = "Shared from DinamikAda File Shelf";

            var storageItems = new List<IStorageItem>(items.Count);
            foreach (var entry in items)
            {
                try
                {
                    if (entry.IsDirectory && Directory.Exists(entry.FullPath))
                    {
                        storageItems.Add(await StorageFolder.GetFolderFromPathAsync(entry.FullPath));
                    }
                    else if (!entry.IsDirectory && File.Exists(entry.FullPath))
                    {
                        storageItems.Add(await StorageFile.GetFileFromPathAsync(entry.FullPath));
                    }
                }
                catch { }
            }

            if (storageItems.Count > 0)
            {
                args.Request.Data.SetStorageItems(storageItems);
            }
            else
            {
                args.Request.FailWithDisplayText("DinamikAda File Shelf is empty.");
            }
        }
        catch (Exception ex)
        {
            try { args.Request.FailWithDisplayText(ex.Message); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool TryOpenExplorerShareFallback()
    {
        var existing = GetItems()
            .Where(i => File.Exists(i.FullPath) || Directory.Exists(i.FullPath))
            .ToList();
        if (existing.Count == 0) return false;

        if (existing.Count == 1)
        {
            return TryOpenSingleExplorerSelect(existing[0].FullPath);
        }

        bool anyOpened = false;
        var groupedByParent = existing
            .GroupBy(i => Path.GetDirectoryName(i.FullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Take(3);

        foreach (var group in groupedByParent)
        {
            var paths = group.Select(x => x.FullPath).ToList();
            if (!string.IsNullOrWhiteSpace(group.Key) &&
                Directory.Exists(group.Key) &&
                TrySelectItemsInExplorer(group.Key, paths))
            {
                anyOpened = true;
            }
            else if (TryOpenSingleExplorerSelect(paths[0]))
            {
                anyOpened = true;
            }
        }

        return anyOpened;
    }

    private static bool TryOpenSingleExplorerSelect(string fullPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySelectItemsInExplorer(string parentFolder, IReadOnlyList<string> itemPaths)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || itemPaths.Count == 0)
        {
            return false;
        }

        nint folderPidl = 0;
        var childPidls = new List<nint>(itemPaths.Count);
        try
        {
            folderPidl = ILCreateFromPathW(parentFolder);
            if (folderPidl == 0) return false;

            foreach (string path in itemPaths)
            {
                nint p = ILCreateFromPathW(path);
                if (p != 0) childPidls.Add(p);
            }
            if (childPidls.Count == 0) return false;

            nint[] relativePidls = childPidls.Select(ILFindLastID).ToArray();
            int hr = SHOpenFolderAndSelectItems(folderPidl, (uint)relativePidls.Length, relativePidls, 0);
            return hr == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (nint p in childPidls) ILFree(p);
            if (folderPidl != 0) ILFree(folderPidl);
        }
    }

    private static string MakeUniqueEntryName(string candidate, HashSet<string> used)
    {
        if (used.Add(candidate)) return candidate;
        string? folder = Path.GetDirectoryName(candidate);
        string stem = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        int idx = 2;
        string unique;
        do { unique = Path.Combine(folder ?? string.Empty, $"{stem}_{idx++}{ext}").Replace('\\', '/'); }
        while (!used.Add(unique));
        return unique;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return;
            string json = File.ReadAllText(_manifestPath);
            var loaded = JsonSerializer.Deserialize<List<ShelfItem>>(json);
            if (loaded is not null)
            {
                _items = loaded.Where(i => File.Exists(i.FullPath) || Directory.Exists(i.FullPath)).ToList();
            }
        }
        catch { }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(_shelfDir);
        string json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
        string tmp = _manifestPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _manifestPath, overwrite: true);
    }
}
