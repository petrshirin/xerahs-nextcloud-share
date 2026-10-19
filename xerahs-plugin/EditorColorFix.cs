using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using XerahS.Common;
using XerahS.Uploaders;

namespace NcShare.Plugin;

// Обход ошибки XerahS #298 (https://github.com/ShareX/XerahS/issues/298): на macOS картинка, которую вернул редактор,
// сохраняется с переставленными красным и синим. Снимок из редактора узнаётся по PNG без цветового профиля (iCCP):
// снимки screencapture и ScreenCaptureKit профиль сохраняют, результат редактора — нет (проверено на 14 файлах, 2026-10-19).
static class EditorColorFix
{
    // Последняя версия XerahS с ошибкой (исправлено в 0.33.2, первый релиз с исправлением — KovaForge 0.33.3);
    // в исправленных версиях обход не нужен и испортил бы цвета
    static readonly Version LastBrokenVersion = new(0, 33, 1);
    static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool Applies(byte[] data)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        Version? host = typeof(FileUploader).Assembly.GetName().Version;
        if (host == null || new Version(host.Major, host.Minor, Math.Max(0, host.Build)) > LastBrokenVersion) return false;
        return IsPng(data) && !HasColorProfile(data);
    }

    // Возвращает PNG с красным и синим, поставленными на место
    public static byte[] SwapRedBlue(byte[] png)
    {
        using Bitmap source = new(new MemoryStream(png));
        PixelSize size = source.PixelSize;
        PixelFormat format = source.Format ?? PixelFormat.Bgra8888;
        AlphaFormat alpha = source.AlphaFormat ?? AlphaFormat.Premul;
        int stride = size.Width * 4;
        byte[] pixels = new byte[stride * size.Height];
        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { source.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { pinned.Free(); }

        // В 4-байтовых форматах RGBA и BGRA красный и синий — байты 0 и 2, так что обмен не зависит от формата
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

        using WriteableBitmap fixedBitmap = new(size, source.Dpi, format, alpha);
        using (ILockedFramebuffer frame = fixedBitmap.Lock())
            for (int y = 0; y < size.Height; y++)
                Marshal.Copy(pixels, y * stride, frame.Address + y * frame.RowBytes, stride);
        using MemoryStream result = new();
        fixedBitmap.Save(result, PngBitmapEncoderOptions.Default);
        return result.ToArray();
    }

    // Сохранённый XerahS файл тоже исправляем, чтобы в истории и папке снимков были верные цвета.
    // Запись через временный файл и переименование: XerahS ещё держит файл открытым на чтение.
    public static void RewriteSaved(string? path, byte[] png)
    {
        if (path == null) return;
        string folder = Path.GetFullPath(PathsManager.ScreenshotsFolder) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.Ordinal)) return;
        string temp = path + ".nc-share.tmp";
        try
        {
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            DebugHelper.WriteLine($"[nc-share] не удалось перезаписать {path}: {ex.Message}");
            File.Delete(temp);
        }
    }

    static bool IsPng(byte[] data) => data.Length > 8 && data.AsSpan(0, 8).SequenceEqual(PngSignature);

    // Профиль (iCCP или sRGB) стоит до первого блока IDAT
    static bool HasColorProfile(byte[] png)
    {
        int pos = 8;
        while (pos + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos, 4));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type is "iCCP" or "sRGB") return true;
            if (type is "IDAT" or "IEND" || length < 0) return false;
            pos += 12 + length;
        }
        return false;
    }
}
