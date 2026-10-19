using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace NcShare.Plugin;

// Пункт «Скопировать»: картинка — как изображение, остальное (видео и т. п.) — как файл, чтобы вставить в мессенджер.
// На macOS буфер Avalonia отдаёт данные только в момент вставки, и после закрытия окна вставлялся пустой PNG,
// поэтому там всё кладётся в NSPasteboard сразу. На других ОС — буфер Avalonia.
static class ImageClipboard
{
    // Картинка должна жить, пока лежит в буфере: Avalonia кодирует её при вставке
    static Bitmap? lastCopied;

    public static async Task CopyAsync(TopLevel window, Shot shot)
    {
        if (!shot.IsImage)
        {
            await CopyFileAsync(window, FileOnDisk(shot));
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            MacPasteboard.SetPng(shot.Data);
            return;
        }
        IClipboard? clipboard = window.Clipboard;
        Bitmap? bitmap = shot.CreateBitmap();
        if (clipboard == null || bitmap == null) return;
        await clipboard.SetBitmapAsync(bitmap);
        await clipboard.FlushAsync();
        lastCopied?.Dispose();
        lastCopied = bitmap;
    }

    static async Task CopyFileAsync(TopLevel window, string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacPasteboard.SetFileUrl(path);
            return;
        }
        IStorageFile? file = await window.StorageProvider.TryGetFileFromPathAsync(path);
        if (file != null && window.Clipboard != null) await window.Clipboard.SetFileAsync(file);
    }

    // Файл из папки уже лежит на диске; если пути нет — копия во временной папке
    static string FileOnDisk(Shot shot)
    {
        if (shot.FilePath != null && File.Exists(shot.FilePath)) return shot.FilePath;
        string path = Path.Combine(Path.GetTempPath(), "nc-share", shot.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, shot.Data);
        return path;
    }

    static class MacPasteboard
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

        // NSPasteboard — класс AppKit; в XerahS он уже загружен, но полагаться на это не стоит
        static MacPasteboard() => NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

        public static void SetPng(byte[] png)
        {
            IntPtr pasteboard = Clear();
            GCHandle pinned = GCHandle.Alloc(png, GCHandleType.Pinned);
            try
            {
                IntPtr data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), pinned.AddrOfPinnedObject(), (nuint)png.Length);
                if (!SendBool(pasteboard, sel_registerName("setData:forType:"), data, NSString("public.png")))
                    throw new InvalidOperationException("NSPasteboard не принял изображение");
            }
            finally { pinned.Free(); }
        }

        // Ссылка на файл, как при Cmd+C в Finder: вставляется в мессенджеры как вложение
        public static void SetFileUrl(string path)
        {
            IntPtr pasteboard = Clear();
            IntPtr url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), NSString(path));
            IntPtr items = Send(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), url);
            if (!SendBool(pasteboard, sel_registerName("writeObjects:"), items))
                throw new InvalidOperationException("NSPasteboard не принял файл");
        }

        static IntPtr Clear()
        {
            IntPtr pasteboard = Send(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));
            if (pasteboard == IntPtr.Zero) throw new InvalidOperationException("Нет доступа к NSPasteboard");
            Send(pasteboard, sel_registerName("clearContents"));
            return pasteboard;
        }

        // Строка автоматически освобождается пулом autorelease
        static IntPtr NSString(string value)
        {
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try { return Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }
    }
}
