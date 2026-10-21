// nc-share — действие ShareX: снимок или запись экрана копируется в буфер или загружается в Nextcloud
// со ссылкой для команды или публичной ссылкой со сроком. Оформление берётся из текущей темы ShareX.
// Собирает и ставит install-windows.ps1 (компилятор C# из .NET Framework, C# 5). Настройки: nc-share.ini рядом с exe.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NcShare
{
    static class Program
    {
        static readonly string LogFile = Path.Combine(Path.GetTempPath(), "nc-share.log");

        public static void Log(string text)
        {
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("s") + "  " + text + Environment.NewLine, Encoding.UTF8); }
            catch (IOException) { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string file = args.Length > 0 ? args[0] : null;
            Theme theme = Theme.Load();
            Shot shot = null;
            using (CursorLoader loader = new CursorLoader(theme))
            {
                try
                {
                    // Лоадер у курсора появится, только если меню не успеет открыться за 150 мс
                    loader.Show(150);
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                        throw new NcException("ShareX не передал путь к файлу — в аргументах действия должно быть %input.");

                    Settings settings = Settings.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nc-share.ini"));
                    if (settings.UploadReady) Nextcloud.Warmup(settings);
                    shot = Shot.Read(file);

                    Choice choice;
                    using (ChoiceMenu menu = new ChoiceMenu(theme, settings, shot))
                    {
                        menu.Shown += delegate { loader.Hide(); };
                        menu.ShowDialog();
                        choice = menu.Result;
                    }
                    loader.Hide();
                    if (choice == null) return 0;

                    if (choice.Kind == ChoiceKind.Clipboard)
                    {
                        shot.CopyToClipboard();
                        Toast.Run(theme, shot, shot.CopiesAsImage ? "Изображение скопировано" : "Файл скопирован",
                            "В буфере обмена, в Nextcloud не загружалось", null, settings.ToastSeconds);
                        return 0;
                    }

                    Credential credential = Credential.Get(theme, settings, loader);
                    if (credential == null) return 0;

                    loader.Show(0);
                    Nextcloud nc = new Nextcloud(settings, credential);
                    string folder = settings.FolderFor(choice.Kind, shot);
                    string fileId = nc.Upload(folder, shot.FileName, shot.Data, loader.SetProgress);
                    loader.SetProgress(-1);

                    string url, note;
                    if (choice.Kind == ChoiceKind.Public)
                    {
                        DateTime until = DateTime.Today.AddDays(choice.Days);
                        url = nc.CreatePublicLink(folder + "/" + shot.FileName, until) + (shot.IsImage ? settings.PublicLinkSuffix : settings.FilePublicLinkSuffix);
                        note = "Публичная, до " + until.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        // Без index.php сервер отдаёт 404
                        url = fileId != null
                            ? settings.Server + "/index.php/f/" + fileId
                            : settings.Server + "/index.php/apps/files/?dir=" + Uri.EscapeDataString(folder);
                        note = "Для команды, без срока";
                    }

                    Clipboard.SetDataObject(url, true, 10, 50);
                    loader.Hide();
                    Toast.Run(theme, shot, "Ссылка скопирована", note, url, settings.ToastSeconds);
                    return 0;
                }
                catch (Exception ex)
                {
                    loader.Hide();
                    Log(file + Environment.NewLine + ex + Environment.NewLine);
                    string message = ex is NcException ? ex.Message : ex.Message + "\n\nПодробности: " + LogFile;
                    MessageBox.Show(message, "Скриншот в Nextcloud — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    if (shot != null) shot.Dispose();
                }
            }
        }
    }

    // --- Настройки: nc-share.ini, читаются при каждом запуске ---
    sealed class Settings
    {
        public string Server = "";
        public string BaseFolder = "";
        // Путь внутри BaseFolder: {type} — папка по типу файла, {access} — по виду ссылки, %y и %mo — год и месяц
        public string PathTemplate = "{type}/{access}/%y/%mo";
        public string ImagesDir = "screenshots";
        public string VideosDir = "videos";
        public string FilesDir = "files";
        public string PublicDir = "public";
        public string TeamDir = "team";
        public int[] PublicDays = { 1, 7, 30 };
        public string PublicLinkSuffix = "/preview";
        public string FilePublicLinkSuffix = "";
        public int ToastSeconds = 4;

        // Без сервера и папки доступно только копирование в буфер
        public bool UploadReady { get { return Server.Length > 0 && BaseFolder.Length > 0 && BaseFolder.IndexOf('<') < 0; } }

        public static Settings Load(string path)
        {
            Settings s = new Settings();
            if (!File.Exists(path)) return s;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string value = line.Substring(eq + 1).Trim();
                switch (line.Substring(0, eq).Trim().ToLowerInvariant())
                {
                    case "server": s.Server = value.TrimEnd('/'); break;
                    case "basefolder":
                        s.BaseFolder = value.TrimEnd('/');
                        if (s.BaseFolder.Length > 0 && s.BaseFolder[0] != '/') s.BaseFolder = "/" + s.BaseFolder;
                        break;
                    case "pathtemplate": s.PathTemplate = value; break;
                    case "imagesdir": s.ImagesDir = value.Trim('/'); break;
                    case "videosdir": s.VideosDir = value.Trim('/'); break;
                    case "filesdir": s.FilesDir = value.Trim('/'); break;
                    case "publicdir": s.PublicDir = value.Trim('/'); break;
                    case "teamdir": s.TeamDir = value.Trim('/'); break;
                    case "publiclinksuffix": s.PublicLinkSuffix = value; break;
                    case "filepubliclinksuffix": s.FilePublicLinkSuffix = value; break;
                    case "toastseconds": int.TryParse(value, out s.ToastSeconds); break;
                    case "publicdays":
                    {
                        List<int> days = new List<int>();
                        foreach (string part in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int d;
                            if (int.TryParse(part, out d) && d > 0) days.Add(d);
                        }
                        if (days.Count > 0) s.PublicDays = days.ToArray();
                        break;
                    }
                }
            }
            return s;
        }

        // Папка для загрузки, например BaseFolder/screenshots/team/2026/10 — так же, как у плагина для XerahS
        public string FolderFor(ChoiceKind kind, Shot shot)
        {
            DateTime now = DateTime.Now;
            string inside = PathTemplate
                .Replace("{type}", shot.IsImage ? ImagesDir : shot.IsVideo ? VideosDir : FilesDir)
                .Replace("{access}", kind == ChoiceKind.Public ? PublicDir : TeamDir)
                .Replace("%y", now.ToString("yyyy", CultureInfo.InvariantCulture))
                .Replace("%mo", now.ToString("MM", CultureInfo.InvariantCulture))
                .Replace("%d", now.ToString("dd", CultureInfo.InvariantCulture));
            StringBuilder folder = new StringBuilder(BaseFolder);
            foreach (string part in inside.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                folder.Append('/').Append(part);
            return folder.ToString();
        }

        public static string DaysText(int n)
        {
            int a = n % 100, b = n % 10;
            string word = a >= 11 && a <= 14 ? "дней" : b == 1 ? "день" : b >= 2 && b <= 4 ? "дня" : "дней";
            return n + " " + word;
        }
    }

    // --- Что передал ShareX: снимок, запись экрана или другой файл ---
    sealed class Shot : IDisposable
    {
        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".heic" };
        // GIF у ShareX — запись экрана, поэтому к видео: копируется файлом, чтобы не потерять анимацию
        static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v", ".gif" };

        public string FilePath, FileName, Caption;
        public bool IsImage, IsVideo;
        // Снимок целиком (для буфера и меню) или миниатюра видео и файла от Windows; null — показать нечего
        public Image Picture;
        // Снимок, который удалось прочитать, копируется картинкой; всё остальное — файлом
        public bool CopiesAsImage;
        byte[] data;

        // Видео читается с диска, только когда дошло до загрузки: меню открывается сразу и у больших записей
        public byte[] Data { get { return data ?? (data = File.ReadAllBytes(FilePath)); } }

        public string Glyph { get { return IsVideo ? "\uE714" : IsImage ? "\uE91B" : "\uE8A5"; } }

        public static Shot Read(string path)
        {
            Shot s = new Shot { FilePath = path, FileName = Path.GetFileName(path) };
            string ext = Path.GetExtension(path);
            s.IsImage = Array.IndexOf(ImageExtensions, ext.ToLowerInvariant()) >= 0;
            s.IsVideo = Array.IndexOf(VideoExtensions, ext.ToLowerInvariant()) >= 0;
            if (s.IsImage)
            {
                try
                {
                    s.Picture = Image.FromStream(new MemoryStream(s.Data));
                    s.CopiesAsImage = true;
                    s.Caption = string.Format("{0} × {1} · {2}", s.Picture.Width, s.Picture.Height, SizeText(s.Data.Length));
                    return s;
                }
                catch (ArgumentException) { }   // формат, которого нет в Windows (WebP, HEIC), — как обычный файл
            }
            // Миниатюру и длительность видео даёт Windows, как в Проводнике
            s.Picture = ShellFile.Thumbnail(path, 600);
            TimeSpan? duration = s.IsVideo ? ShellFile.Duration(path) : null;
            s.Caption = (duration.HasValue ? DurationText(duration.Value) + " · " : "") + SizeText(new FileInfo(path).Length);
            return s;
        }

        public void CopyToClipboard()
        {
            DataObject copy = new DataObject();
            if (CopiesAsImage) copy.SetImage(Picture);
            else copy.SetFileDropList(new StringCollection { FilePath });
            Clipboard.SetDataObject(copy, true, 10, 50);
        }

        static string SizeText(long size)
        {
            return size >= 1024 * 1024
                ? string.Format("{0:0.#} МБ", size / 1024.0 / 1024.0)
                : string.Format("{0:N0} КБ", Math.Ceiling(size / 1024.0));
        }

        static string DurationText(TimeSpan d)
        {
            return d.TotalHours >= 1 ? string.Format("{0}:{1:mm\\:ss}", (int)d.TotalHours, d) : string.Format("{0}:{1:ss}", (int)d.TotalMinutes, d);
        }

        public void Dispose()
        {
            if (Picture != null) Picture.Dispose();
        }
    }

    // --- Миниатюры и свойства файлов от оболочки Windows: то же, что показывает Проводник ---
    static class ShellFile
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(Size size, int flags, out IntPtr hbitmap);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        // Методы до GetUInt64 объявлены только ради порядка в таблице COM — они не вызываются
        [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem2
        {
            void BindToHandler(); void GetParent(); void GetDisplayName(); void GetAttributes(); void Compare();
            void GetPropertyStore(); void GetPropertyStoreWithCreateObject(); void GetPropertyStoreForKeys();
            void GetPropertyDescriptionList(); void Update(); void GetProperty(); void GetCLSID(); void GetFileTime();
            void GetInt32(); void GetString(); void GetUInt32();
            [PreserveSig] int GetUInt64(ref PropertyKey key, out ulong value);
        }

        const int ThumbnailOnly = 0x8;   // SIIGBF_THUMBNAILONLY: без значка вместо миниатюры

        // System.Media.Duration, в сотнях наносекунд
        static readonly Guid MediaFormatId = new Guid("64440490-4c8b-11d1-8b70-080036b11a03");
        const int DurationPropertyId = 3;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object item);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);

        public static Image Thumbnail(string path, int size)
        {
            object item = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out item);
                IntPtr hbitmap;
                if (((IShellItemImageFactory)item).GetImage(new Size(size, size), ThumbnailOnly, out hbitmap) != 0) return null;
                try { return Image.FromHbitmap(hbitmap); }
                finally { DeleteObject(hbitmap); }
            }
            catch (Exception ex)
            {
                Program.Log("Миниатюра " + path + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }

        public static TimeSpan? Duration(string path)
        {
            object item = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem2).GUID, out item);
                PropertyKey key = new PropertyKey { FormatId = MediaFormatId, PropertyId = DurationPropertyId };
                ulong value;
                if (((IShellItem2)item).GetUInt64(ref key, out value) != 0 || value == 0) return null;
                return TimeSpan.FromTicks((long)value);
            }
            catch (Exception ex)
            {
                Program.Log("Длительность " + path + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }
    }

    // --- Тема ShareX: цвета и шрифт из ApplicationConfig.json ---
    sealed class Theme
    {
        // Значения темы Dark из ShareX — на случай, если конфиг не прочитается
        public Color Back = Color.FromArgb(39, 39, 39);
        public Color LightBack = Color.FromArgb(46, 46, 46);
        public Color DarkBack = Color.FromArgb(34, 34, 34);
        public Color Text = Color.FromArgb(231, 233, 234);
        public Color Link = Color.FromArgb(166, 212, 255);
        public Color MenuBorder = Color.FromArgb(63, 63, 63);
        public Color Highlight = Color.FromArgb(46, 46, 46);
        public Color HighlightBorder = Color.FromArgb(63, 63, 63);
        public Color Dim;
        public Font Font, BoldFont, SmallFont, IconFont, BigIconFont;
        public float Scale = 1f;

        public int Px(double value) { return (int)Math.Round(value * Scale); }

        // nc-share лежит в папке ShareX: <папка ShareX>\Tools\nc-share. Обычно это Документы\ShareX
        static string ConfigPath()
        {
            string beside = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ApplicationConfig.json"));
            if (File.Exists(beside)) return beside;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "ApplicationConfig.json");
        }

        public static Theme Load()
        {
            Theme t = new Theme();
            string fontSpec = "Segoe UI, 9.75pt";
            try
            {
                JavaScriptSerializer json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> config = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(ConfigPath()));
                Dictionary<string, object> theme = (Dictionary<string, object>)((object[])config["Themes"])[Convert.ToInt32(config["SelectedTheme"])];
                t.Back = ReadColor(theme, "BackgroundColor", t.Back);
                t.LightBack = ReadColor(theme, "LightBackgroundColor", t.LightBack);
                t.DarkBack = ReadColor(theme, "DarkBackgroundColor", t.DarkBack);
                t.Text = ReadColor(theme, "TextColor", t.Text);
                t.Link = ReadColor(theme, "LinkColor", t.Link);
                t.MenuBorder = ReadColor(theme, "MenuBorderColor", t.MenuBorder);
                t.Highlight = ReadColor(theme, "MenuHighlightColor", t.Highlight);
                t.HighlightBorder = ReadColor(theme, "MenuHighlightBorderColor", t.HighlightBorder);
                object font;
                if (theme.TryGetValue("MenuFont", out font) && font is string) fontSpec = (string)font;
            }
            catch (Exception) { }

            try { t.Font = (Font)new FontConverter().ConvertFromInvariantString(fontSpec); }
            catch (Exception) { t.Font = new Font("Segoe UI", 9.75f); }
            t.BoldFont = new Font(t.Font, FontStyle.Bold);
            t.SmallFont = new Font(t.Font.FontFamily, t.Font.SizeInPoints - 1f);
            // Монохромные иконки, как на панели редактора ShareX
            foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                Font icon = new Font(name, t.Font.SizeInPoints + 1.5f);
                if (icon.Name == name)
                {
                    t.IconFont = icon;
                    t.BigIconFont = new Font(name, 22f);
                    break;
                }
                icon.Dispose();
            }
            // Приглушённый текст — между цветом текста и фона
            t.Dim = Color.FromArgb(
                (int)(t.Back.R + (t.Text.R - t.Back.R) * 0.55),
                (int)(t.Back.G + (t.Text.G - t.Back.G) * 0.55),
                (int)(t.Back.B + (t.Text.B - t.Back.B) * 0.55));
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) t.Scale = g.DpiX / 96f;
            return t;
        }

        static Color ReadColor(Dictionary<string, object> theme, string key, Color fallback)
        {
            object value;
            if (!theme.TryGetValue(key, out value) || !(value is string)) return fallback;
            string[] p = ((string)value).Split(',');
            try
            {
                if (p.Length == 3) return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
                if (p.Length == 4) return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                Color named = Color.FromName((string)value);
                if (named.IsKnownColor) return named;
            }
            catch (FormatException) { }
            catch (ArgumentException) { }
            return fallback;
        }
    }

    static class Native
    {
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        // Чёткий текст при масштабировании экрана, как у ShareX
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }

    static class Gfx
    {
        public static Bitmap Thumbnail(Image image, int maxWidth, int maxHeight)
        {
            double k = Math.Min(1.0, Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height));
            Bitmap bmp = new Bitmap(Math.Max(1, (int)(image.Width * k)), Math.Max(1, (int)(image.Height * k)));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, bmp.Width, bmp.Height);
            }
            return bmp;
        }

        public static void DrawCentered(Graphics g, Image image, Rectangle box)
        {
            g.DrawImage(image, box.X + (box.Width - image.Width) / 2, box.Y + (box.Height - image.Height) / 2, image.Width, image.Height);
        }

        // Вместо миниатюры, если Windows её не дала: значок видео или файла
        public static void DrawGlyph(Graphics g, Theme t, string glyph, Rectangle box)
        {
            if (t.BigIconFont != null) TextRenderer.DrawText(g, glyph, t.BigIconFont, box, t.Dim, Line | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        public const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
    }

    // --- Меню выбора в стиле меню ShareX ---
    enum ChoiceKind { Clipboard, Team, Public }

    sealed class Choice
    {
        public ChoiceKind Kind;
        public int Days;
    }

    // Открывается у курсора; цифры 1..N — выбор, стрелки + Enter — навигация, Esc или щелчок мимо — отмена
    sealed class ChoiceMenu : Form
    {
        enum ItemKind { Thumbnail, Caption, Separator, Header, Row }

        sealed class Item
        {
            public ItemKind Kind;
            public Rectangle Bounds;
            public string Glyph, Text;
            public Choice Choice;
            public bool Enabled;
        }

        readonly Theme t;
        readonly Shot shot;
        readonly Bitmap thumb;
        readonly List<Item> items = new List<Item>();
        readonly List<Item> rows = new List<Item>();
        readonly int width, inset;
        int y, hover = -1;
        bool closing;

        public Choice Result;

        public ChoiceMenu(Theme theme, Settings settings, Shot shot)
        {
            t = theme;
            this.shot = shot;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = t.Back;
            Font = t.Font;
            Text = "Скриншот в Nextcloud";

            width = t.Px(300);
            inset = 1 + t.Px(4);   // рамка + отступ
            y = inset;
            if (shot.Picture != null) thumb = Gfx.Thumbnail(shot.Picture, width, t.Px(170));

            // Без миниатюры — значок и имя файла
            AddItem(ItemKind.Thumbnail, thumb != null ? thumb.Height + t.Px(10) : t.Px(84));
            AddItem(ItemKind.Caption, t.Px(24));
            AddSeparator();
            AddRow("\uE8C8", shot.CopiesAsImage ? "Скопировать изображение" : "Скопировать файл", new Choice { Kind = ChoiceKind.Clipboard }, true);
            AddRow("\uE716", "Ссылка для команды", new Choice { Kind = ChoiceKind.Team }, settings.UploadReady);
            AddSeparator();
            AddItem(ItemKind.Header, t.Px(22)).Text = "Публичная ссылка";
            foreach (int days in settings.PublicDays)
                AddRow("\uE774", "На " + Settings.DaysText(days), new Choice { Kind = ChoiceKind.Public, Days = days }, settings.UploadReady);

            ClientSize = new Size(width + 2 * inset, y + inset);
            Point c = MousePosition;
            Rectangle wa = Screen.FromPoint(c).WorkingArea;
            Location = new Point(
                Math.Max(wa.Left, Math.Min(c.X + 2, wa.Right - Width)),
                Math.Max(wa.Top, Math.Min(c.Y + 2, wa.Bottom - Height)));
        }

        Item AddItem(ItemKind kind, int height)
        {
            Item item = new Item { Kind = kind, Bounds = new Rectangle(inset, y, width, height) };
            items.Add(item);
            y += height;
            return item;
        }

        void AddSeparator()
        {
            y += t.Px(4);
            AddItem(ItemKind.Separator, 1);
            y += t.Px(4);
        }

        void AddRow(string glyph, string text, Choice choice, bool enabled)
        {
            Item row = AddItem(ItemKind.Row, t.Px(30));
            row.Glyph = glyph;
            row.Text = enabled ? text : text + " (не настроено)";
            row.Choice = choice;
            row.Enabled = enabled;
            rows.Add(row);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen border = new Pen(t.MenuBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            foreach (Item item in items)
            {
                Rectangle b = item.Bounds;
                switch (item.Kind)
                {
                    case ItemKind.Thumbnail:
                        using (SolidBrush bg = new SolidBrush(t.DarkBack)) g.FillRectangle(bg, b);
                        if (thumb != null)
                        {
                            Gfx.DrawCentered(g, thumb, b);
                        }
                        else
                        {
                            Gfx.DrawGlyph(g, t, shot.Glyph, new Rectangle(b.X, b.Y + t.Px(8), b.Width, t.Px(44)));
                            TextRenderer.DrawText(g, shot.FileName, t.SmallFont, new Rectangle(b.X + t.Px(8), b.Y + t.Px(54), b.Width - t.Px(16), t.Px(22)), t.Dim, Gfx.Line | TextFormatFlags.HorizontalCenter);
                        }
                        break;
                    case ItemKind.Caption:
                        TextRenderer.DrawText(g, shot.Caption, t.SmallFont, b, t.Dim, Gfx.Line | TextFormatFlags.HorizontalCenter);
                        break;
                    case ItemKind.Separator:
                        using (SolidBrush line = new SolidBrush(t.MenuBorder)) g.FillRectangle(line, b);
                        break;
                    case ItemKind.Header:
                        TextRenderer.DrawText(g, item.Text, t.SmallFont, Rectangle.FromLTRB(b.Left + t.Px(34), b.Top, b.Right, b.Bottom), t.Dim, Gfx.Line);
                        break;
                    case ItemKind.Row:
                        int index = rows.IndexOf(item);
                        if (index == hover)
                        {
                            using (SolidBrush hl = new SolidBrush(t.Highlight)) g.FillRectangle(hl, b);
                            using (Pen hb = new Pen(t.HighlightBorder)) g.DrawRectangle(hb, b.X, b.Y, b.Width - 1, b.Height - 1);
                        }
                        Color fore = item.Enabled ? t.Text : t.Dim;
                        if (t.IconFont != null)
                            TextRenderer.DrawText(g, item.Glyph, t.IconFont, new Rectangle(b.Left + t.Px(4), b.Top, t.Px(26), b.Height), fore, Gfx.Line | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                        TextRenderer.DrawText(g, item.Text, t.Font, Rectangle.FromLTRB(b.Left + t.Px(34), b.Top, b.Right - t.Px(34), b.Bottom), fore, Gfx.Line);
                        TextRenderer.DrawText(g, (index + 1).ToString(CultureInfo.InvariantCulture), t.Font, Rectangle.FromLTRB(b.Right - t.Px(30), b.Top, b.Right - t.Px(6), b.Bottom), t.Dim, Gfx.Line | TextFormatFlags.HorizontalCenter);
                        break;
                }
            }
        }

        int RowAt(Point p)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Enabled && rows[i].Bounds.Contains(p)) return i;
            return -1;
        }

        void SetHover(int index)
        {
            if (index == hover) return;
            hover = index;
            Invalidate();
        }

        void MoveHover(int step)
        {
            int i = hover >= 0 ? hover : step > 0 ? -1 : rows.Count;
            for (int k = 0; k < rows.Count; k++)
            {
                i = (i + step + rows.Count) % rows.Count;
                if (rows[i].Enabled) { SetHover(i); return; }
            }
        }

        void Pick(int index)
        {
            if (index < 0 || index >= rows.Count || !rows[index].Enabled) return;
            Result = rows[index].Choice;
            Close();
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHover(RowAt(e.Location)); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) Pick(RowAt(e.Location));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key >= Keys.D1 && key <= Keys.D9) { Pick(key - Keys.D1); return true; }
            if (key >= Keys.NumPad1 && key <= Keys.NumPad9) { Pick(key - Keys.NumPad1); return true; }
            switch (key)
            {
                case Keys.Down: MoveHover(1); return true;
                case Keys.Up: MoveHover(-1); return true;
                case Keys.Enter: Pick(hover); return true;
                case Keys.Escape: Close(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

        protected override void OnFormClosing(FormClosingEventArgs e) { closing = true; base.OnFormClosing(e); }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Result == null && !closing) Close();   // щелчок мимо меню — отмена
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && thumb != null) thumb.Dispose();
            base.Dispose(disposing);
        }
    }

    // --- Уведомление в стиле ShareX: правый нижний угол, не забирает фокус (Ctrl+V сразу идёт в нужное окно) ---
    sealed class Toast : Form
    {
        readonly Theme t;
        readonly Bitmap thumb;
        readonly string glyph, title, detail, url;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public static void Run(Theme theme, Shot shot, string title, string detail, string url, int seconds)
        {
            using (Toast toast = new Toast(theme, shot, title, detail, url, seconds))
                Application.Run(toast);
        }

        Toast(Theme theme, Shot shot, string title, string detail, string url, int seconds)
        {
            t = theme;
            this.title = title;
            this.detail = detail;
            this.url = url;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = t.Back;
            Opacity = 0.99;
            if (url != null) Cursor = Cursors.Hand;
            ClientSize = new Size(t.Px(360), t.Px(88));
            Rectangle wa = Screen.FromPoint(MousePosition).WorkingArea;
            Location = new Point(wa.Right - Width - t.Px(12), wa.Bottom - Height - t.Px(12));
            glyph = shot.Glyph;
            if (shot.Picture != null) thumb = Gfx.Thumbnail(shot.Picture, t.Px(96), t.Px(70));
            timer.Interval = Math.Max(1, seconds) * 1000;
            timer.Tick += Fade;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); timer.Start(); }

        void Fade(object sender, EventArgs e)
        {
            timer.Interval = 30;
            Opacity -= 0.08;
            if (Opacity <= 0.1) { timer.Stop(); Close(); }
        }

        // Пока курсор над уведомлением, оно не исчезает; щелчок открывает ссылку
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); timer.Stop(); Opacity = 0.99; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); timer.Interval = 1500; timer.Start(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (url != null) Process.Start(url);
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen border = new Pen(t.MenuBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            Rectangle box = new Rectangle(t.Px(8), t.Px(8), t.Px(96), t.Px(70));
            using (SolidBrush bg = new SolidBrush(t.DarkBack)) g.FillRectangle(bg, box);
            if (thumb != null) Gfx.DrawCentered(g, thumb, box);
            else Gfx.DrawGlyph(g, t, glyph, box);
            int x = t.Px(114), w = Width - x - t.Px(8);
            TextRenderer.DrawText(g, title, t.BoldFont, new Rectangle(x, t.Px(9), w, t.Px(22)), t.Text, Gfx.Line);
            TextRenderer.DrawText(g, detail, t.Font, new Rectangle(x, t.Px(32), w, t.Px(20)), t.Dim, Gfx.Line);
            if (url != null) TextRenderer.DrawText(g, url, t.Font, new Rectangle(x, t.Px(54), w, t.Px(20)), t.Link, Gfx.Line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (thumb != null) thumb.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // --- Лоадер у курсора: в своём потоке, чтобы крутиться, пока основной занят запуском или загрузкой.
    //     Во время загрузки кольцо заполняется по мере отправки файла ---
    sealed class CursorLoader : IDisposable
    {
        readonly Theme theme;
        readonly object sync = new object();
        ManualResetEvent cancel;
        LoaderForm form;
        // Отправлено, в тысячных; -1 — неизвестно, тогда кольцо просто крутится
        volatile int permille = -1;

        public CursorLoader(Theme theme) { this.theme = theme; }

        public int Permille { get { return permille; } }

        public void SetProgress(double fraction)
        {
            permille = fraction < 0 ? -1 : (int)(Math.Min(1.0, fraction) * 1000);
        }

        public void Show(int delayMs)
        {
            Hide();
            permille = -1;
            ManualResetEvent myCancel = new ManualResetEvent(false);
            lock (sync) cancel = myCancel;
            Thread thread = new Thread(() =>
            {
                try
                {
                    if (myCancel.WaitOne(delayMs)) return;
                    LoaderForm f = new LoaderForm(theme, this);
                    GC.KeepAlive(f.Handle);   // окно создаётся заранее, чтобы Hide мог его закрыть
                    lock (sync)
                    {
                        if (myCancel.WaitOne(0)) { f.Dispose(); return; }
                        form = f;
                    }
                    Application.Run(f);
                }
                catch (Exception) { }   // лоадер не должен мешать основной работе
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Hide()
        {
            lock (sync)
            {
                if (cancel != null) { cancel.Set(); cancel = null; }
                if (form == null) return;
                LoaderForm f = form;
                form = null;
                try { f.BeginInvoke(new MethodInvoker(f.Close)); }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose() { Hide(); }
    }

    sealed class LoaderForm : Form
    {
        readonly Theme t;
        readonly CursorLoader loader;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        float angle;

        public LoaderForm(Theme theme, CursorLoader loader)
        {
            t = theme;
            this.loader = loader;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = t.Back;
            Opacity = 0.97;
            ClientSize = new Size(t.Px(30), t.Px(30));
            FollowCursor();
            timer.Interval = 16;
            timer.Tick += delegate { angle = (angle + 12) % 360; FollowCursor(); Invalidate(); };
        }

        void FollowCursor()
        {
            Point c = MousePosition;
            Rectangle wa = Screen.FromPoint(c).WorkingArea;
            int offset = t.Px(18);
            Location = new Point(Math.Min(c.X + offset, wa.Right - Width), Math.Min(c.Y + offset, wa.Bottom - Height));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); timer.Start(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen border = new Pen(t.MenuBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pen = Math.Max(2f, 2.5f * t.Scale);
            int pad = t.Px(8);
            RectangleF ring = new RectangleF(pad, pad, Width - 2 * pad, Height - 2 * pad);
            using (Pen track = new Pen(t.MenuBorder, pen)) g.DrawEllipse(track, ring);
            using (Pen arc = new Pen(t.Text, pen))
            {
                arc.StartCap = arc.EndCap = LineCap.Round;
                int done = loader.Permille;
                if (done >= 0) g.DrawArc(arc, ring, -90, Math.Max(6f, 360f * done / 1000));
                else g.DrawArc(arc, ring, angle, 100);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // --- Вход через браузер (Login Flow v2, Nextcloud 16+): пароль приложения создаётся сам, работают SSO и
    //     двухфакторная проверка, свой пароль nc-share не видит. Логин и пароль приложения хранятся зашифрованными (DPAPI) ---
    sealed class Credential
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nc-share");
        static readonly string FilePath = Path.Combine(Dir, "credential.dat");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nc-share");

        public string User, Password;

        // Вход привязан к серверу: если в nc-share.ini сменили сервер, пароль от старого туда не отправляется
        public static Credential Get(Theme theme, Settings settings, CursorLoader loader)
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    string[] parts = Unprotect(File.ReadAllBytes(FilePath)).Split('\n');
                    // Вход, сохранённый прежними версиями, сервера не помнит — считаем, что он от текущего
                    bool sameServer = parts.Length == 2 || (parts.Length == 3 && string.Equals(parts[2], settings.Server, StringComparison.OrdinalIgnoreCase));
                    if (sameServer) return new Credential { User = parts[0], Password = parts[1] };
                }
                catch (CryptographicException) { }
            }

            loader.Show(0);
            Credential c;
            LoginFlow flow = null;
            try
            {
                c = LoginFlow.TakePending(settings.Server);
                if (c == null)
                {
                    flow = Nextcloud.StartLoginFlow(settings);
                    flow.Save(settings.Server);
                }
            }
            finally { loader.Hide(); }
            if (c == null) c = LoginWindow.Run(theme, settings, flow);
            if (c == null) return null;
            LoginFlow.Delete();
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(FilePath, Protect(c.User + "\n" + c.Password + "\n" + settings.Server));
            return c;
        }

        public static void Forget() { File.Delete(FilePath); }

        public static byte[] Protect(string text)
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser);
        }

        public static string Unprotect(byte[] data)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        }
    }

    // Начатый вход хранится, пока Nextcloud его помнит (20 минут): если окно входа закрыли, а в браузере
    // доступ всё-таки разрешили, следующая загрузка забирает пароль приложения без второго входа
    sealed class LoginFlow
    {
        static readonly string FilePath = Path.Combine(Credential.Dir, "login-flow.dat");
        static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

        public string LoginUrl, Endpoint, Token;

        public void Save(string server)
        {
            Directory.CreateDirectory(Credential.Dir);
            File.WriteAllBytes(FilePath, Credential.Protect(Endpoint + "\n" + Token + "\n" + server));
        }

        // null — начатого входа нет, он устарел или в браузере его так и не подтвердили
        public static Credential TakePending(string server)
        {
            if (!File.Exists(FilePath) || DateTime.Now - File.GetLastWriteTime(FilePath) > Lifetime) return null;
            try
            {
                string[] parts = Credential.Unprotect(File.ReadAllBytes(FilePath)).Split('\n');
                if (parts.Length != 3 || !string.Equals(parts[2], server, StringComparison.OrdinalIgnoreCase)) return null;
                return Nextcloud.PollLoginFlow(new LoginFlow { Endpoint = parts[0], Token = parts[1] });
            }
            catch (CryptographicException) { return null; }
            catch (NcException) { return null; }   // со старым входом что-то не так — просто начинаем новый
        }

        public static void Delete()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
    }

    // Окно на время входа: браузер уже открыт, окно ждёт подтверждения и закрывается само.
    // Стоит в углу экрана, как уведомление: по центру оно закрывало бы кнопку входа на странице в браузере
    sealed class LoginWindow : Form
    {
        static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

        readonly Theme t;
        readonly LoginFlow flow;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Credential result;
        Exception error;

        // null — пользователь закрыл окно, не дождавшись входа
        public static Credential Run(Theme theme, Settings settings, LoginFlow flow)
        {
            Process.Start(flow.LoginUrl);
            using (LoginWindow w = new LoginWindow(theme, settings.Server, flow))
            {
                w.ShowDialog();
                if (w.error != null) throw w.error is NcException ? w.error : new NcException("Вход в Nextcloud: " + w.error.Message);
                return w.result;
            }
        }

        LoginWindow(Theme theme, string server, LoginFlow flow)
        {
            t = theme;
            this.flow = flow;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = t.Back;
            Font = t.Font;
            Text = "nc-share: вход в Nextcloud";

            int w = t.Px(340), x = t.Px(16), y = x;
            y = AddLabel("Вход в Nextcloud", t.BoldFont, t.Text, x, y, w) + t.Px(6);
            y = AddLabel("В браузере открылся " + server + ". Войдите и разрешите доступ для nc-share — это окно закроется само, " +
                "и загрузка продолжится. Входить нужно один раз.", t.Font, t.Dim, x, y, w) + t.Px(14);
            int bw = (w - t.Px(8)) / 2;
            Button again = AddButton("Открыть ещё раз", x, y, bw);
            again.Click += delegate { Process.Start(flow.LoginUrl); };
            Button cancel = AddButton("Отмена", x + bw + t.Px(8), y, bw);
            cancel.Click += delegate { Close(); };
            CancelButton = cancel;
            ClientSize = new Size(w + 2 * x, y + t.Px(32) + x);
            Rectangle wa = Screen.FromPoint(MousePosition).WorkingArea;
            Location = new Point(wa.Right - Width - t.Px(12), wa.Bottom - Height - t.Px(12));
        }

        int AddLabel(string text, Font font, Color color, int x, int y, int w)
        {
            int h = TextRenderer.MeasureText(text, font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 2;
            Controls.Add(new Label { Text = text, Font = font, ForeColor = color, UseMnemonic = false, Location = new Point(x, y), Size = new Size(w, h) });
            return y + h;
        }

        Button AddButton(string text, int x, int y, int w)
        {
            Button b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, t.Px(32)), FlatStyle = FlatStyle.Flat,
                BackColor = t.LightBack, ForeColor = t.Text, UseMnemonic = false
            };
            b.FlatAppearance.BorderColor = t.MenuBorder;
            b.FlatAppearance.MouseOverBackColor = t.HighlightBorder;
            Controls.Add(b);
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(t.MenuBorder)) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Thread poll = new Thread(Poll);
            poll.IsBackground = true;
            poll.Start();
        }

        // Nextcloud отвечает 404, пока вход в браузере не подтверждён
        void Poll()
        {
            DateTime deadline = DateTime.Now + Limit;
            try
            {
                while (!stop.WaitOne(2000))
                {
                    Credential c = Nextcloud.PollLoginFlow(flow);
                    if (c != null) { Finish(c, null); return; }
                    if (DateTime.Now > deadline) throw new NcException("Вход в Nextcloud не завершён за " + Limit.TotalMinutes + " минут.");
                }
            }
            catch (Exception ex) { Finish(null, ex); }
        }

        void Finish(Credential c, Exception ex)
        {
            try { BeginInvoke(new MethodInvoker(delegate { result = c; error = ex; Close(); })); }
            catch (InvalidOperationException) { }   // окно уже закрыто
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            stop.Set();
            base.OnFormClosed(e);
        }
    }

    // --- Nextcloud: WebDAV для загрузки, OCS API для публичных ссылок, Login Flow v2 для входа через браузер ---
    sealed class NcException : Exception
    {
        public NcException(string message) : base(message) { }
    }

    sealed class Nextcloud
    {
        sealed class Response
        {
            public int Status;
            public string Body;
            public WebHeaderCollection Headers;
        }

        // Под этим именем пароль приложения виден в Nextcloud: Настройки → Безопасность
        const string UserAgent = "nc-share (ShareX)";

        readonly Settings settings;
        readonly string auth;

        static Nextcloud()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;   // без лишнего круга «100 Continue» перед загрузкой
        }

        public Nextcloud(Settings settings, Credential credential)
        {
            this.settings = settings;
            auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.User + ":" + credential.Password));
        }

        // Соединение (DNS + TLS, около секунды) устанавливается в фоне, пока пользователь выбирает пункт меню;
        // загрузка потом идёт по уже открытому keep-alive соединению
        public static void Warmup(Settings settings)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(settings.Server + "/status.php");
                    req.Timeout = 10000;
                    req.UserAgent = UserAgent;
                    using (req.GetResponse()) { }
                }
                catch (Exception) { }   // не удалось — загрузка просто откроет соединение сама
            });
        }

        // Возвращает id файла для внутренней ссылки /index.php/f/<id> или null. progress — доля отправленного (0…1)
        public string Upload(string folder, string fileName, byte[] data, Action<double> progress)
        {
            string path = folder + "/" + fileName;
            Response r = Request("PUT", DavUrl(path), data, "application/octet-stream", auth, progress);
            if (r.Status == 409)   // папки ещё нет — создаём по уровням и повторяем
            {
                CreateFolders(folder);
                r = Request("PUT", DavUrl(path), data, "application/octet-stream", auth, progress);
            }
            Check(r, "Загрузка файла");
            // OC-FileId = id файла с нулями слева + id инстанса: "00012345oc..." → 12345
            Match m = Regex.Match(r.Headers["OC-FileId"] ?? "", @"^(\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null;
        }

        public string CreatePublicLink(string path, DateTime until)
        {
            string form = "path=" + Uri.EscapeDataString("/" + path.TrimStart('/')) + "&shareType=3&permissions=1&expireDate=" + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Response r = Request("POST", settings.Server + "/ocs/v2.php/apps/files_sharing/api/v1/shares?format=json", Encoding.UTF8.GetBytes(form), "application/x-www-form-urlencoded", auth, null);
            Check(r, "Создание публичной ссылки");
            string url = Get(Get(Get(Json(r.Body), "ocs"), "data"), "url") as string;
            if (string.IsNullOrEmpty(url)) throw new NcException("Создание публичной ссылки: сервер не вернул ссылку.");
            return url;
        }

        public static LoginFlow StartLoginFlow(Settings settings)
        {
            Response r = Request("POST", settings.Server + "/index.php/login/v2", new byte[0], "application/x-www-form-urlencoded", null, null);
            Check(r, "Вход в Nextcloud");
            Dictionary<string, object> json = Json(r.Body);
            LoginFlow flow = new LoginFlow
            {
                LoginUrl = Get(json, "login") as string,
                Endpoint = Get(Get(json, "poll"), "endpoint") as string,
                Token = Get(Get(json, "poll"), "token") as string
            };
            if (string.IsNullOrEmpty(flow.LoginUrl) || string.IsNullOrEmpty(flow.Endpoint) || string.IsNullOrEmpty(flow.Token))
                throw new NcException("Вход в Nextcloud: сервер не начал вход через браузер. Нужен Nextcloud 16 или новее.");
            return flow;
        }

        // null — пользователь ещё не подтвердил вход в браузере
        public static Credential PollLoginFlow(LoginFlow flow)
        {
            Response r = Request("POST", flow.Endpoint, Encoding.UTF8.GetBytes("token=" + Uri.EscapeDataString(flow.Token)), "application/x-www-form-urlencoded", null, null);
            if (r.Status == 404) return null;
            Check(r, "Вход в Nextcloud");
            Dictionary<string, object> json = Json(r.Body);
            string user = Get(json, "loginName") as string, password = Get(json, "appPassword") as string;
            return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) ? null : new Credential { User = user, Password = password };
        }

        // Подпапки создаются от BaseFolder вниз. Если нет и самой BaseFolder, сначала она — от корня.
        // Выше BaseFolder без нужды не ходим: там может не быть права записи
        void CreateFolders(string folder)
        {
            string inside = folder.Substring(settings.BaseFolder.Length);
            if (CreateLevels(settings.BaseFolder, inside)) return;
            CreateLevels("", settings.BaseFolder);
            CreateLevels(settings.BaseFolder, inside);
        }

        // MKCOL по уровням; 405 — папка уже есть. false — нет родительской папки (409 на первом уровне)
        bool CreateLevels(string parent, string relative)
        {
            string current = parent;
            foreach (string part in relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bool first = current == parent;
                current += "/" + part;
                Response r = Request("MKCOL", DavUrl(current), null, null, auth, null);
                if (first && r.Status == 409) return false;
                if (r.Status != 405) Check(r, "Создание папки " + current);
            }
            return true;
        }

        string DavUrl(string path)
        {
            List<string> parts = new List<string>();
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) parts.Add(Uri.EscapeDataString(part));
            return settings.Server + "/remote.php/webdav/" + string.Join("/", parts);
        }

        // null — ответ не JSON-объект
        static Dictionary<string, object> Json(string body)
        {
            try { return new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        // Значение по ключу; null, если ключа или самого объекта нет
        static object Get(object json, string key)
        {
            Dictionary<string, object> d = json as Dictionary<string, object>;
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        // Через VPN и туннели соединение иногда не устанавливается — сетевые сбои повторяем, ответы сервера нет.
        // Повтор безопасен: PUT перезаписывает тот же файл, MKCOL на существующую папку даёт 405, опрос входа ничего не меняет.
        const int Attempts = 3;
        const int Chunk = 64 * 1024;

        static Response Request(string method, string url, byte[] body, string contentType, string auth, Action<double> progress)
        {
            for (int attempt = 1; ; attempt++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                try { return RequestOnce(method, url, body, contentType, auth, progress); }
                catch (WebException ex)
                {
                    Program.Log(string.Format("{0} {1}: попытка {2} не удалась за {3} мс — {4}: {5}",
                        method, url, attempt, watch.ElapsedMilliseconds, ex.Status, ex.Message));
                    if (attempt >= Attempts)
                        throw new NcException("Нет связи с " + new Uri(url).GetLeftPart(UriPartial.Authority) + " после " + Attempts + " попыток: " + ex.Message +
                            "\nПроверьте сеть и VPN, если сервер доступен только через него.");
                    Thread.Sleep(500);
                }
            }
        }

        static Response RequestOnce(string method, string url, byte[] body, string contentType, string auth, Action<double> progress)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = UserAgent;
            // Ожидание соединения и ответа — зависшая попытка не тянется полминуты. Большое видео сервер
            // дописывает дольше: к ожиданию ответа добавляется секунда на каждые 10 МБ
            req.Timeout = 10000 + (body != null ? body.Length / 10000 : 0);
            req.ReadWriteTimeout = 30000;   // на каждую порцию отправки и чтения ответа
            if (auth != null) req.Headers[HttpRequestHeader.Authorization] = auth;
            req.Headers["OCS-APIRequest"] = "true";
            HttpWebResponse resp;
            try
            {
                if (body != null)
                {
                    req.ContentType = contentType;
                    req.ContentLength = body.Length;
                    req.AllowWriteStreamBuffering = false;   // тело уходит частями, а не копится в памяти второй раз
                    using (Stream s = req.GetRequestStream())
                    {
                        for (int offset = 0; offset < body.Length; offset += Chunk)
                        {
                            int count = Math.Min(Chunk, body.Length - offset);
                            s.Write(body, offset, count);
                            if (progress != null) progress((double)(offset + count) / body.Length);
                        }
                    }
                }
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;   // сетевой сбой — повтор решает Request
            }
            using (resp)
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return new Response { Status = (int)resp.StatusCode, Body = reader.ReadToEnd(), Headers = resp.Headers };
        }

        static void Check(Response r, string action)
        {
            if (r.Status < 400) return;
            string hint;
            switch (r.Status)
            {
                case 401:
                    Credential.Forget();
                    hint = "Пароль приложения не подходит — при следующей загрузке nc-share попросит войти заново.";
                    break;
                case 403: hint = "Нет прав в папке (нужны запись и, для публичных ссылок, право делиться) или публичные ссылки запрещены на сервере."; break;
                case 404: hint = "Не найден путь — проверьте Server и BaseFolder в nc-share.ini или запустите установщик ещё раз."; break;
                case 409: hint = "Не удалось создать папку — проверьте BaseFolder в nc-share.ini."; break;
                default: hint = r.Body.Length > 500 ? r.Body.Substring(0, 500) : r.Body; break;
            }
            throw new NcException(action + " — HTTP " + r.Status + "\n" + hint);
        }
    }
}
