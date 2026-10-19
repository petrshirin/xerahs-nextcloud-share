// nc-share — действие ShareX: скриншот копируется в буфер или загружается в Nextcloud
// со ссылкой для команды или публичной ссылкой со сроком. Оформление берётся из текущей темы ShareX.
// Сборка: install.ps1 (компилятор C# из .NET Framework, C# 5). Настройки: nc-share.ini рядом с exe.
using System;
using System.Collections.Generic;
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
            using (CursorLoader loader = new CursorLoader(theme))
            {
                try
                {
                    // Лоадер у курсора появится, только если меню не успеет открыться за 150 мс
                    loader.Show(150);
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                        throw new NcException("ShareX не передал путь к скриншоту — в аргументах действия должно быть %input.");

                    Settings settings = Settings.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nc-share.ini"));
                    if (settings.UploadReady) Nextcloud.Warmup(settings);
                    byte[] data = File.ReadAllBytes(file);
                    Image shot = Image.FromStream(new MemoryStream(data));

                    Choice choice;
                    using (ChoiceMenu menu = new ChoiceMenu(theme, settings, shot, data.Length))
                    {
                        menu.Shown += delegate { loader.Hide(); };
                        menu.ShowDialog();
                        choice = menu.Result;
                    }
                    loader.Hide();
                    if (choice == null) return 0;

                    if (choice.Kind == ChoiceKind.Clipboard)
                    {
                        DataObject image = new DataObject();
                        image.SetImage(shot);
                        Clipboard.SetDataObject(image, true, 10, 50);
                        Toast.Run(theme, shot, "Изображение скопировано", "В буфере обмена, в Nextcloud не загружалось", null, settings.ToastSeconds);
                        return 0;
                    }

                    Credential credential = Credential.Get(theme);
                    if (credential == null) return 0;

                    loader.Show(0);
                    Nextcloud nc = new Nextcloud(settings, credential);
                    string folder = settings.BaseFolder + "/" + (choice.Kind == ChoiceKind.Public ? settings.PublicDir : settings.TeamDir);
                    string path = folder + "/" + Path.GetFileName(file);
                    string fileId = nc.Upload(path, data);

                    string url, note;
                    if (choice.Kind == ChoiceKind.Public)
                    {
                        DateTime until = DateTime.Today.AddDays(choice.Days);
                        url = nc.CreatePublicLink(path, until) + settings.PublicLinkSuffix;
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
            }
        }
    }

    // --- Настройки: nc-share.ini, читаются при каждом запуске ---
    sealed class Settings
    {
        public string Server = "";
        public string BaseFolder = "";
        public string PublicDir = "public";
        public string TeamDir = "team";
        public int[] PublicDays = { 1, 7, 30 };
        public string PublicLinkSuffix = "/preview";
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
                    case "basefolder": s.BaseFolder = value.TrimEnd('/'); break;
                    case "publicdir": s.PublicDir = value.Trim('/'); break;
                    case "teamdir": s.TeamDir = value.Trim('/'); break;
                    case "publiclinksuffix": s.PublicLinkSuffix = value; break;
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

        public static string DaysText(int n)
        {
            int a = n % 100, b = n % 10;
            string word = a >= 11 && a <= 14 ? "дней" : b == 1 ? "день" : b >= 2 && b <= 4 ? "дня" : "дней";
            return n + " " + word;
        }
    }

    // --- Тема ShareX: цвета и шрифт из Documents\ShareX\ApplicationConfig.json ---
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
        public Font Font, BoldFont, SmallFont, IconFont;
        public float Scale = 1f;

        public int Px(double value) { return (int)Math.Round(value * Scale); }

        public static Theme Load()
        {
            Theme t = new Theme();
            string fontSpec = "Segoe UI, 9.75pt";
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "ApplicationConfig.json");
                JavaScriptSerializer json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> config = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(path));
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
                if (icon.Name == name) { t.IconFont = icon; break; }
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
        readonly Bitmap thumb;
        readonly string caption;
        readonly List<Item> items = new List<Item>();
        readonly List<Item> rows = new List<Item>();
        readonly int width, inset;
        int y, hover = -1;
        bool closing;

        public Choice Result;

        public ChoiceMenu(Theme theme, Settings settings, Image shot, long size)
        {
            t = theme;
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
            thumb = Gfx.Thumbnail(shot, width, t.Px(170));
            caption = string.Format("{0} × {1} · {2:N0} КБ", shot.Width, shot.Height, Math.Ceiling(size / 1024.0));

            AddItem(ItemKind.Thumbnail, thumb.Height + t.Px(10));
            AddItem(ItemKind.Caption, t.Px(24));
            AddSeparator();
            AddRow("", "Скопировать изображение", new Choice { Kind = ChoiceKind.Clipboard }, true);
            AddRow("", "Ссылка для команды", new Choice { Kind = ChoiceKind.Team }, settings.UploadReady);
            AddSeparator();
            AddItem(ItemKind.Header, t.Px(22)).Text = "Публичная ссылка";
            foreach (int days in settings.PublicDays)
                AddRow("", "На " + Settings.DaysText(days), new Choice { Kind = ChoiceKind.Public, Days = days }, settings.UploadReady);

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
            row.Text = enabled ? text : text + " (нет папки)";
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
                        Gfx.DrawCentered(g, thumb, b);
                        break;
                    case ItemKind.Caption:
                        TextRenderer.DrawText(g, caption, t.SmallFont, b, t.Dim, Gfx.Line | TextFormatFlags.HorizontalCenter);
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
            if (disposing) thumb.Dispose();
            base.Dispose(disposing);
        }
    }

    // --- Уведомление в стиле ShareX: правый нижний угол, не забирает фокус (Ctrl+V сразу идёт в нужное окно) ---
    sealed class Toast : Form
    {
        readonly Theme t;
        readonly Bitmap thumb;
        readonly string title, detail, url;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public static void Run(Theme theme, Image shot, string title, string detail, string url, int seconds)
        {
            using (Toast toast = new Toast(theme, shot, title, detail, url, seconds))
                Application.Run(toast);
        }

        Toast(Theme theme, Image shot, string title, string detail, string url, int seconds)
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
            thumb = Gfx.Thumbnail(shot, t.Px(96), t.Px(70));
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
            Gfx.DrawCentered(g, thumb, box);
            int x = t.Px(114), w = Width - x - t.Px(8);
            TextRenderer.DrawText(g, title, t.BoldFont, new Rectangle(x, t.Px(9), w, t.Px(22)), t.Text, Gfx.Line);
            TextRenderer.DrawText(g, detail, t.Font, new Rectangle(x, t.Px(32), w, t.Px(20)), t.Dim, Gfx.Line);
            if (url != null) TextRenderer.DrawText(g, url, t.Font, new Rectangle(x, t.Px(54), w, t.Px(20)), t.Link, Gfx.Line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); thumb.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // --- Лоадер у курсора: в своём потоке, чтобы крутиться, пока основной занят запуском или загрузкой ---
    sealed class CursorLoader : IDisposable
    {
        readonly Theme theme;
        readonly object sync = new object();
        ManualResetEvent cancel;
        LoaderForm form;

        public CursorLoader(Theme theme) { this.theme = theme; }

        public void Show(int delayMs)
        {
            Hide();
            ManualResetEvent myCancel = new ManualResetEvent(false);
            lock (sync) cancel = myCancel;
            Thread thread = new Thread(() =>
            {
                try
                {
                    if (myCancel.WaitOne(delayMs)) return;
                    LoaderForm f = new LoaderForm(theme);
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
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        float angle;

        public LoaderForm(Theme theme)
        {
            t = theme;
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
                g.DrawArc(arc, ring, angle, 100);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // --- Вход: спрашивается один раз, дальше логин и пароль приложения хранятся зашифрованными (DPAPI) ---
    sealed class Credential
    {
        static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nc-share", "credential.dat");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nc-share");

        public string User, Password;

        public static Credential Get(Theme theme)
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    string[] parts = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser)).Split('\n');
                    if (parts.Length == 2) return new Credential { User = parts[0], Password = parts[1] };
                }
                catch (CryptographicException) { }
            }
            Credential c = LoginForm.Ask(theme);
            if (c == null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(c.User + "\n" + c.Password), Entropy, DataProtectionScope.CurrentUser));
            return c;
        }

        public static void Forget() { File.Delete(FilePath); }
    }

    sealed class LoginForm : Form
    {
        readonly Theme t;
        readonly TextBox user, pass;

        public static Credential Ask(Theme theme)
        {
            using (LoginForm f = new LoginForm(theme))
            {
                if (f.ShowDialog() != DialogResult.OK || f.user.Text.Trim().Length == 0 || f.pass.Text.Length == 0) return null;
                return new Credential { User = f.user.Text.Trim(), Password = f.pass.Text };
            }
        }

        LoginForm(Theme theme)
        {
            t = theme;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = t.Back;
            Font = t.Font;
            Text = "Nextcloud: вход";

            int w = t.Px(320), x = t.Px(15), y = x;
            y = AddLabel("Nextcloud: вход", t.BoldFont, t.Text, x, y, w) + t.Px(4);
            y = AddLabel("Логин и пароль приложения из Nextcloud (Настройки → Безопасность). Спрашиваю один раз.", t.SmallFont, t.Dim, x, y, w) + t.Px(12);
            y = AddLabel("Логин", t.Font, t.Text, x, y, w) + t.Px(3);
            user = AddBox(x, ref y, w, false);
            y = AddLabel("Пароль приложения", t.Font, t.Text, x, y, w) + t.Px(3);
            pass = AddBox(x, ref y, w, true);
            y += t.Px(4);
            int bw = (w - t.Px(8)) / 2;
            AcceptButton = AddButton("Сохранить", x, y, bw, DialogResult.OK);
            CancelButton = AddButton("Отмена", x + bw + t.Px(8), y, bw, DialogResult.Cancel);
            ClientSize = new Size(w + 2 * x, y + t.Px(32) + x);
        }

        int AddLabel(string text, Font font, Color color, int x, int y, int w)
        {
            int h = TextRenderer.MeasureText(text, font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 2;
            Controls.Add(new Label { Text = text, Font = font, ForeColor = color, UseMnemonic = false, Location = new Point(x, y), Size = new Size(w, h) });
            return y + h;
        }

        TextBox AddBox(int x, ref int y, int w, bool secret)
        {
            TextBox box = new TextBox
            {
                Location = new Point(x, y), Width = w, BorderStyle = BorderStyle.FixedSingle,
                BackColor = t.LightBack, ForeColor = t.Text, UseSystemPasswordChar = secret
            };
            Controls.Add(box);
            y += box.Height + t.Px(10);
            return box;
        }

        Button AddButton(string text, int x, int y, int w, DialogResult result)
        {
            Button b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, t.Px(32)), FlatStyle = FlatStyle.Flat,
                BackColor = t.LightBack, ForeColor = t.Text, DialogResult = result, UseMnemonic = false
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

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); user.Focus(); }
    }

    // --- Nextcloud: WebDAV для загрузки, OCS API для публичных ссылок ---
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
                    using (req.GetResponse()) { }
                }
                catch (Exception) { }   // не удалось — загрузка просто откроет соединение сама
            });
        }

        // Возвращает id файла для внутренней ссылки /index.php/f/<id> или null
        public string Upload(string path, byte[] data)
        {
            Response r = Send("PUT", DavUrl(path), data, "application/octet-stream");
            if (r.Status == 409)   // папки ещё нет — создаём и повторяем
            {
                CreateFolder(settings.BaseFolder);
                CreateFolder(path.Substring(0, path.LastIndexOf('/')));
                r = Send("PUT", DavUrl(path), data, "application/octet-stream");
            }
            Check(r, "Загрузка файла");
            // OC-FileId = id файла с нулями слева + id инстанса: "00012345oc..." → 12345
            Match m = Regex.Match(r.Headers["OC-FileId"] ?? "", @"^(\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null;
        }

        public string CreatePublicLink(string path, DateTime until)
        {
            string form = "path=" + Uri.EscapeDataString(path) + "&shareType=3&permissions=1&expireDate=" + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Response r = Send("POST", settings.Server + "/ocs/v2.php/apps/files_sharing/api/v1/shares?format=json", Encoding.UTF8.GetBytes(form), "application/x-www-form-urlencoded");
            Check(r, "Создание публичной ссылки");
            Dictionary<string, object> ocs = (Dictionary<string, object>)((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(r.Body))["ocs"];
            return (string)((Dictionary<string, object>)ocs["data"])["url"];
        }

        void CreateFolder(string path)
        {
            Response r = Send("MKCOL", DavUrl(path), null, null);
            if (r.Status != 405) Check(r, "Создание папки " + path);   // 405 — папка уже есть
        }

        string DavUrl(string path)
        {
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return settings.Server + "/remote.php/webdav" + string.Join("/", parts);
        }

        // Через VPN и туннели соединение иногда не устанавливается — сетевые сбои повторяем, ответы сервера нет.
        // Повтор безопасен: PUT перезаписывает тот же файл, MKCOL на существующую папку даёт 405.
        const int Attempts = 3;

        Response Send(string method, string url, byte[] body, string contentType)
        {
            for (int attempt = 1; ; attempt++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                try { return SendOnce(method, url, body, contentType); }
                catch (WebException ex)
                {
                    Program.Log(string.Format("{0} {1}: попытка {2} не удалась за {3} мс — {4}: {5}",
                        method, url, attempt, watch.ElapsedMilliseconds, ex.Status, ex.Message));
                    if (attempt >= Attempts)
                        throw new NcException("Нет связи с " + settings.Server + " после " + Attempts + " попыток: " + ex.Message +
                            "\nПроверьте сеть и VPN, если сервер доступен только через него.");
                    Thread.Sleep(500);
                }
            }
        }

        Response SendOnce(string method, string url, byte[] body, string contentType)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 10000;            // ожидание соединения и ответа — зависшая попытка не тянется полминуты
            req.ReadWriteTimeout = 30000;   // передача самого файла
            req.Headers[HttpRequestHeader.Authorization] = auth;
            req.Headers["OCS-APIRequest"] = "true";
            HttpWebResponse resp;
            try
            {
                if (body != null)
                {
                    req.ContentType = contentType;
                    req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;   // сетевой сбой — повтор решает Send
            }
            using (resp)
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return new Response { Status = (int)resp.StatusCode, Body = reader.ReadToEnd(), Headers = resp.Headers };
        }

        void Check(Response r, string action)
        {
            if (r.Status < 400) return;
            string hint;
            switch (r.Status)
            {
                case 401:
                    Credential.Forget();
                    hint = "Неверный логин или пароль приложения — при следующем запуске спрошу заново.";
                    break;
                case 403: hint = "Нет прав в папке (нужны запись и, для публичных ссылок, право делиться) или публичные ссылки запрещены на сервере."; break;
                case 404: hint = "Не найден путь — проверьте Server и BaseFolder в nc-share.ini."; break;
                case 409: hint = "Нет родительской папки — проверьте BaseFolder в nc-share.ini (папка должна существовать)."; break;
                default: hint = r.Body.Length > 500 ? r.Body.Substring(0, 500) : r.Body; break;
            }
            throw new NcException(action + " — HTTP " + r.Status + "\n" + hint);
        }
    }
}
