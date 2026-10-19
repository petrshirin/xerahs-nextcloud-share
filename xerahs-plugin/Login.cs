using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using XerahS.Common;
using XerahS.Uploaders.PluginSystem;

namespace NcShare.Plugin;

// Логин и пароль приложения — в хранилище секретов XerahS (на macOS — Связка ключей), не в настройках.
// Вход привязан к серверу: если в настройках сменили сервер, пароль от старого туда не отправляется
public sealed class CredentialStore
{
    const string ProviderId = "ncshare";
    readonly ISecretStore? secrets;
    readonly string key;
    readonly string server;

    public CredentialStore(ISecretStore? secrets, string key, string server)
    {
        this.secrets = secrets;
        this.key = key;
        this.server = server;
    }

    public Credential? Get()
    {
        string? login = secrets?.GetSecret(ProviderId, key, "loginName");
        string? password = secrets?.GetSecret(ProviderId, key, "appPassword");
        // Вход, сохранённый до версии 0.5, сервера не помнит — считаем, что он от текущего
        string? savedFor = secrets?.GetSecret(ProviderId, key, "server");
        if (!string.IsNullOrEmpty(savedFor) && !string.Equals(savedFor, server, StringComparison.OrdinalIgnoreCase)) return null;
        return string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password) ? null : new Credential(login, password);
    }

    public void Save(Credential credential)
    {
        if (secrets == null) throw new NcException("XerahS не дал плагину хранилище секретов — сохранить вход негде.");
        secrets.SetSecret(ProviderId, key, "loginName", credential.LoginName);
        secrets.SetSecret(ProviderId, key, "appPassword", credential.AppPassword);
        secrets.SetSecret(ProviderId, key, "server", server);
    }

    public void Forget()
    {
        secrets?.DeleteSecret(ProviderId, key, "loginName");
        secrets?.DeleteSecret(ProviderId, key, "appPassword");
        secrets?.DeleteSecret(ProviderId, key, "server");
    }
}

// Вход спрашивается при первой загрузке: браузер открывает Nextcloud, после входа через SSO пароль приложения создаётся сам
static class Login
{
    static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    // null — пользователь отменил вход
    public static async Task<Credential?> RunAsync(NcShareConfig config, CredentialStore store, CancellationToken cancel)
    {
        LoginFlow flow = await NextcloudClient.StartLoginFlowAsync(config, cancel).ConfigureAwait(false);
        OpenBrowser(flow.LoginUrl);
        LoginWindow window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LoginWindow w = new(config.Server, flow.LoginUrl);
            w.Show();
            return w;
        });
        try
        {
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancel, window.Cancelled);
            wait.CancelAfter(Limit);
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), wait.Token).ConfigureAwait(false);
                Credential? credential = await NextcloudClient.PollLoginFlowAsync(flow, wait.Token).ConfigureAwait(false);
                if (credential == null) continue;
                store.Save(credential);
                DebugHelper.WriteLine($"[nc-share] вход выполнен: {credential.LoginName}");
                return credential;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            if (window.Cancelled.IsCancellationRequested) return null;
            throw new NcException($"Вход в Nextcloud не завершён за {Limit.TotalMinutes:0} минут.");
        }
        finally
        {
            Dispatcher.UIThread.Post(window.CloseQuietly);
        }
    }

    public static void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

// Окно на время входа: подсказка, «Открыть ещё раз» и «Отмена»
sealed class LoginWindow : Window
{
    readonly CancellationTokenSource cancel = new();
    bool closingQuietly;

    public CancellationToken Cancelled => cancel.Token;

    public LoginWindow(string server, string loginUrl)
    {
        Title = "nc-share: вход в Nextcloud";
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Button again = new() { Content = "Открыть ещё раз" };
        again.Click += (_, _) => Login.OpenBrowser(loginUrl);
        Button stop = new() { Content = "Отмена" };
        stop.Click += (_, _) => Close();

        StackPanel panel = new() { Margin = new Thickness(16), Spacing = 10, Width = 340 };
        panel.Children.Add(new TextBlock { Text = "Вход в Nextcloud", FontWeight = Avalonia.Media.FontWeight.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = $"В браузере открылся {server}. Войдите и разрешите доступ для nc-share — окно закроется само. Спрашиваю один раз.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75
        });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { again, stop } });
        Content = panel;

        Closed += (_, _) => { if (!closingQuietly) cancel.Cancel(); };
    }

    public void CloseQuietly()
    {
        closingQuietly = true;
        Close();
    }
}
