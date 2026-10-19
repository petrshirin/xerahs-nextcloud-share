using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using XerahS.Common;

namespace NcShare.Plugin;

public enum ChoiceKind { Cancel, Clipboard, Team, Public }

public sealed record Choice(ChoiceKind Kind, int Days = 0)
{
    public Shot? Shot { get; init; }
}

// Меню выбора: цифры 1–5 или щелчок — выбор, Esc или переход в другое окно — отмена
sealed class ChoiceMenu : Window
{
    readonly TaskCompletionSource<Choice> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<(Choice Choice, bool Enabled)> choices = new();
    readonly Shot shot;
    readonly Bitmap? bitmap;
    bool picking;

    public static Task<Choice> ShowAsync(Shot shot, NcShareConfig config)
    {
        ChoiceMenu menu = new(shot, config);
        menu.Show();
        menu.Activate();
        return menu.result.Task;
    }

    ChoiceMenu(Shot shot, NcShareConfig config)
    {
        this.shot = shot;
        bitmap = shot.CreateBitmap();
        Title = "nc-share";
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        StackPanel panel = new() { Margin = new Thickness(12), Spacing = 4, Width = 300 };
        if (bitmap != null) panel.Children.Add(new Image { Source = bitmap, MaxHeight = 170, MaxWidth = 300 });
        else panel.Children.Add(new TextBlock { Text = shot.FileName, FontWeight = Avalonia.Media.FontWeight.Bold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = bitmap != null ? $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} · {shot.SizeText}" : shot.SizeText,
            Opacity = 0.6, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6)
        });
        AddRow(panel, shot.IsImage ? "Скопировать изображение" : "Скопировать файл", new Choice(ChoiceKind.Clipboard), true);
        AddRow(panel, "Ссылка для команды", new Choice(ChoiceKind.Team), config.UploadReady);
        panel.Children.Add(new TextBlock { Text = "Публичная ссылка", Opacity = 0.6, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        foreach (int days in config.Days)
            AddRow(panel, "На " + DaysText(days), new Choice(ChoiceKind.Public, days), config.UploadReady);
        Content = panel;

        KeyDown += OnKeyDown;
        Deactivated += (_, _) => { if (!picking) Finish(new Choice(ChoiceKind.Cancel)); };
        Closed += (_, _) => result.TrySetResult(new Choice(ChoiceKind.Cancel) { Shot = shot });
    }

    // Без папки в настройках загружать некуда — пункты ссылок неактивны
    void AddRow(StackPanel panel, string text, Choice choice, bool enabled)
    {
        choices.Add((choice, enabled));
        Button row = new()
        {
            Content = $"{choices.Count}   {text}{(enabled ? "" : " (не настроено)")}",
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        row.Click += (_, _) => Finish(choice);
        panel.Children.Add(row);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        int index = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            _ => -1
        };
        if (index >= 0 && index < choices.Count && choices[index].Enabled) Finish(choices[index].Choice);
        else if (e.Key == Key.Escape) Finish(new Choice(ChoiceKind.Cancel));
    }

    async void Finish(Choice choice)
    {
        if (picking || result.Task.IsCompleted) return;
        picking = true;
        // Картинку в буфер кладём, пока окно открыто: буфер в Avalonia берётся у окна
        if (choice.Kind == ChoiceKind.Clipboard)
        {
            try { await ImageClipboard.CopyAsync(this, shot); }
            catch (Exception ex) { DebugHelper.WriteException(ex, "[nc-share] не удалось скопировать изображение"); }
        }
        result.TrySetResult(choice with { Shot = shot });
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        bitmap?.Dispose();
    }

    static string DaysText(int n)
    {
        int a = n % 100, b = n % 10;
        string word = a >= 11 && a <= 14 ? "дней" : b == 1 ? "день" : b >= 2 && b <= 4 ? "дня" : "дней";
        return n + " " + word;
    }
}
