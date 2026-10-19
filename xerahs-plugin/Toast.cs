using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace NcShare.Plugin;

// Уведомление в правом нижнем углу, как у nc-share: не забирает фокус (Cmd+V сразу идёт в нужное окно),
// пока курсор над ним — не исчезает, щелчок открывает ссылку. Новое уведомление заменяет предыдущее.
sealed class Toast : Window
{
    const double ToastWidth = 360, ToastHeight = 88, Margin12 = 12;
    const int Seconds = 4, ErrorSeconds = 10;

    static Toast? current;

    readonly DispatcherTimer timer = new();
    readonly Bitmap? bitmap;
    readonly string? url;
    // Полоса загрузки есть только у «Загружаю…»; такое уведомление висит, пока его не заменят
    readonly ProgressBar? bar;
    readonly object? progressKey;
    bool sticky => bar != null;

    public static void Post(Shot? shot, string title, string detail, string? url = null) => Show(shot, title, detail, url, Seconds, null);

    public static void Error(Shot? shot, string title, string detail) => Show(shot, title, detail, null, ErrorSeconds, null);

    // «Загружаю…»: висит, пока его не заменит результат. Возвращает, куда сообщать долю отправленного (0…1)
    public static Action<double> Progress(Shot? shot, string title, string detail)
    {
        object key = new();
        Show(shot, title, detail, null, 0, key);
        int shown = 0;
        return fraction =>
        {
            int percent = (int)(Math.Clamp(fraction, 0, 1) * 100);
            if (Interlocked.Exchange(ref shown, percent) == percent) return;
            // Уведомление могли уже заменить результатом или загрузкой другого снимка
            Dispatcher.UIThread.Post(() => { if (current?.progressKey == key) current.SetProgress(percent); });
        };
    }

    static void Show(Shot? shot, string title, string detail, string? url, int seconds, object? progressKey)
    {
        Dispatcher.UIThread.Post(() =>
        {
            current?.Close();
            current = new Toast(shot, title, detail, url, seconds, progressKey);
            current.Show();
        });
    }

    Toast(Shot? shot, string title, string detail, string? url, int seconds, object? progressKey)
    {
        this.url = url;
        this.progressKey = progressKey;
        bitmap = shot?.CreateBitmap();
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        Width = ToastWidth;
        Height = ToastHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (url != null) Cursor = new Cursor(StandardCursorType.Hand);

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("96,*"), Margin = new Thickness(8) };
        if (bitmap != null) grid.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        StackPanel text = new() { Margin = new Thickness(10, 0, 0, 0), Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, MaxLines = url == null && progressKey == null ? 3 : 1, TextTrimming = TextTrimming.CharacterEllipsis });
        if (url != null) text.Children.Add(new TextBlock { Text = url, Foreground = Brushes.CornflowerBlue, TextTrimming = TextTrimming.CharacterEllipsis });
        if (progressKey != null)
        {
            bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };
            text.Children.Add(bar);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Content = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = grid };

        timer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
        timer.Tick += (_, _) => Fade();
        Opened += (_, _) => { PlaceBottomRight(); if (!sticky) timer.Start(); };
    }

    void PlaceBottomRight()
    {
        Screen? screen = Screens.Primary;
        if (screen == null) return;
        PixelRect area = screen.WorkingArea;
        double scale = screen.Scaling;
        Position = new PixelPoint(
            area.Right - (int)((ToastWidth + Margin12) * scale),
            area.Bottom - (int)((ToastHeight + Margin12) * scale));
    }

    // До первых байт (соединение) и после последних (сервер сохраняет файл, создаётся ссылка) — бегущая полоса
    void SetProgress(int percent)
    {
        if (bar == null) return;
        bar.IsIndeterminate = percent is <= 0 or >= 100;
        bar.Value = percent;
    }

    void Fade()
    {
        timer.Interval = TimeSpan.FromMilliseconds(30);
        Opacity -= 0.08;
        if (Opacity <= 0.1) { timer.Stop(); Close(); }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        timer.Stop();
        Opacity = 1;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (sticky) return;
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Start();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (sticky) return;
        if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        timer.Stop();
        bitmap?.Dispose();
        if (current == this) current = null;
    }
}
