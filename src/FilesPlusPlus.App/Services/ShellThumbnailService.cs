using System.Runtime.InteropServices;

namespace FilesPlusPlus.App.Services;

/// <summary>
/// Raw 32bpp BGRA pixel payload for a shell thumbnail or icon, decoupled from any UI type
/// so it can be produced entirely off the UI thread.
/// </summary>
public sealed record ShellThumbnailPixels(int Width, int Height, byte[] Bgra);

public enum ShellThumbnailKind
{
    /// <summary>Prefer a real thumbnail (image/video/doc preview), fall back to the file-type icon.</summary>
    ThumbnailOrIcon,

    /// <summary>Force the file-type/association icon even when a thumbnail exists.</summary>
    IconOnly
}

/// <summary>
/// Fetches real Windows shell icons and thumbnails using <c>IShellItemImageFactory</c> (the same
/// surface Explorer uses). Work runs on the thread pool; results are returned as raw BGRA pixels
/// so callers can build a <c>WriteableBitmap</c> on the UI thread.
/// </summary>
public sealed class ShellThumbnailService
{
    public static ShellThumbnailService Instance { get; } = new();

    private const int MaxCacheEntries = 1024;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, ShellThumbnailPixels> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lruOrder = new();

    private ShellThumbnailService()
    {
    }

    public async Task<ShellThumbnailPixels?> GetPixelsAsync(
        string path,
        int pixelSize,
        ShellThumbnailKind kind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || pixelSize <= 0)
        {
            return null;
        }

        var cacheKey = BuildCacheKey(path, pixelSize, kind);
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var pixels = await Task.Run(() => TryCapturePixels(path, pixelSize, kind, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (pixels is not null)
        {
            Store(cacheKey, pixels);
        }

        return pixels;
    }

    private static string BuildCacheKey(string path, int pixelSize, ShellThumbnailKind kind)
    {
        // Why: Icons can change when a file's modified time changes (e.g. regenerated thumbnails),
        // so the last-write timestamp is folded into the key when cheaply available.
        long stamp = 0;
        try
        {
            stamp = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks
                : Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : 0;
        }
        catch
        {
            // Why: Access failures should not prevent caching by path alone.
        }

        return $"{(int)kind}|{pixelSize}|{stamp}|{path.ToLowerInvariant()}";
    }

    private bool TryGetCached(string cacheKey, out ShellThumbnailPixels? pixels)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var found))
            {
                _lruOrder.Remove(cacheKey);
                _lruOrder.AddFirst(cacheKey);
                pixels = found;
                return true;
            }
        }

        pixels = null;
        return false;
    }

    private void Store(string cacheKey, ShellThumbnailPixels pixels)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(cacheKey))
            {
                _cache[cacheKey] = pixels;
                _lruOrder.Remove(cacheKey);
                _lruOrder.AddFirst(cacheKey);
                return;
            }

            _cache[cacheKey] = pixels;
            _lruOrder.AddFirst(cacheKey);

            while (_lruOrder.Count > MaxCacheEntries && _lruOrder.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _lruOrder.RemoveLast();
            }
        }
    }

    private static ShellThumbnailPixels? TryCapturePixels(
        string path,
        int pixelSize,
        ShellThumbnailKind kind,
        CancellationToken cancellationToken)
    {
        var hbitmap = IntPtr.Zero;
        object? factoryObject = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var factoryGuid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, factoryGuid, out factoryObject);
            if (hr != 0 || factoryObject is not IShellItemImageFactory factory)
            {
                return null;
            }

            var flags = kind == ShellThumbnailKind.IconOnly
                ? SIIGBF.IconOnly | SIIGBF.ResizeToFit
                : SIIGBF.ResizeToFit;

            var size = new SIZE { cx = pixelSize, cy = pixelSize };

            hr = factory.GetImage(size, flags, out hbitmap);
            if (hr != 0 && kind == ShellThumbnailKind.ThumbnailOrIcon)
            {
                // Why: Some providers reject the default request but still serve an icon-only image.
                hr = factory.GetImage(size, SIIGBF.IconOnly | SIIGBF.ResizeToFit, out hbitmap);
            }

            if (hr != 0 || hbitmap == IntPtr.Zero)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CopyBitmapToBgra(hbitmap);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }

            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                Marshal.ReleaseComObject(factoryObject);
            }
        }
    }

    private static ShellThumbnailPixels? CopyBitmapToBgra(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0)
        {
            return null;
        }

        var width = bitmap.bmWidth;
        var height = bitmap.bmHeight;
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            return null;
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            // Why: Negative height yields a top-down DIB so rows match WriteableBitmap ordering.
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB
        };

        var info = new BITMAPINFO { bmiHeader = header };
        var buffer = new byte[width * height * 4];

        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var scanLines = GetDIBits(hdc, hbitmap, 0, (uint)height, buffer, ref info, DIB_RGB_COLORS);
            if (scanLines == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        EnsureVisibleAlpha(buffer);
        return new ShellThumbnailPixels(width, height, buffer);
    }

    private static void EnsureVisibleAlpha(byte[] bgra)
    {
        // Why: A handful of icon providers return a 32bpp bitmap with a zeroed alpha channel,
        // which would render fully transparent. If every pixel is transparent, force it opaque.
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return;
            }
        }

        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 0xFF;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint uStartScan,
        uint cScanLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFO lpbi,
        uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00000000,
        BiggerSizeOk = 0x00000001,
        MemoryOnly = 0x00000002,
        IconOnly = 0x00000004,
        ThumbnailOnly = 0x00000008,
        InCacheOnly = 0x00000010,
        ScaleUp = 0x00000100
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
