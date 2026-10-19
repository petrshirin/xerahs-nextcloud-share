using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using XerahS.Common;
using XerahS.Uploaders;
using XerahS.Uploaders.PluginSystem;

namespace NcShare.Plugin;

// Сценарий XerahS для плагина: без «Copy image to clipboard» и без своего уведомления —
// картинку в буфер кладёт и уведомление показывает плагин, иначе Esc в меню нечего было бы отменять
public sealed class NcShareUploader : FileUploader, IUploadHandler
{
    // XerahS вызывает загрузку на один снимок дважды: Upload сразу после снимка и UploadAsync, если первый вызов
    // не вернул ссылку. Результат запоминается по имени файла, чтобы меню и загрузка выполнялись один раз.
    static readonly ConcurrentDictionary<string, Task<UploadOutcome>> Runs = new(StringComparer.Ordinal);
    static readonly UploadOutcome NothingUploaded = UploadOutcome.Success(null, "nc-share: без загрузки", urlExpected: false);
    static int calls;

    readonly NcShareConfig config;
    readonly CredentialStore credentials;

    public NcShareUploader(NcShareConfig config, CredentialStore credentials)
    {
        this.config = config;
        this.credentials = credentials;
    }

    public override UploadResult Upload(Stream stream, string fileName)
    {
        Log("Upload", fileName);
        UploadOutcome outcome = Wait(Run(stream, fileName, fromCapture: true));
        if (!outcome.Succeeded && !string.IsNullOrEmpty(outcome.Error)) Errors.Add(outcome.Error);
        return outcome.ToUploadResult();
    }

    public Task<UploadOutcome> UploadAsync(UploadRequest request, CancellationToken cancellationToken = default)
    {
        Log("UploadAsync", request.FileName);
        return Run(request.Content, request.FileName, fromCapture: false);
    }

    // fromCapture: снимок XerahS (синхронный Upload после снимка), а не файл из папки или истории.
    // Только такие снимки плагин исправляет (цвета) и удаляет при отмене.
    Task<UploadOutcome> Run(Stream stream, string fileName, bool fromCapture)
    {
        if (Runs.TryGetValue(fileName, out Task<UploadOutcome>? known)) return known;
        // Скрытые файлы — временные (так пишут некоторые программы): их не трогаем
        if (fileName.StartsWith('.')) return Task.FromResult(NothingUploaded);
        Shot shot = Shot.Read(stream, fileName, config, fromCapture);
        return Runs.GetOrAdd(fileName, _ => ProcessAsync(shot));
    }

    // Синхронный Upload может прийти в потоке интерфейса: тогда ждать можно только во вложенном цикле сообщений,
    // иначе XerahS зависнет. На практике XerahS вызывает его в фоновом потоке.
    static T Wait<T>(Task<T> task)
    {
        if (!Dispatcher.UIThread.CheckAccess()) return task.GetAwaiter().GetResult();
        DispatcherFrame frame = new();
        task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    async Task<UploadOutcome> ProcessAsync(Shot shot)
    {
        if (config.UploadReady) NextcloudClient.Warmup(config);
        Choice choice = await Dispatcher.UIThread.InvokeAsync(() => ChoiceMenu.ShowAsync(shot, config));
        DebugHelper.WriteLine($"[nc-share] выбор для \"{shot.FileName}\": {choice.Kind}{(choice.Days > 0 ? " " + choice.Days : "")}");
        switch (choice.Kind)
        {
            case ChoiceKind.Clipboard:   // в буфер уже положило меню
                Toast.Post(shot, shot.IsImage ? "Изображение скопировано" : "Файл скопирован", "В буфере обмена, в Nextcloud не загружалось");
                return NothingUploaded;
            case ChoiceKind.Cancel:
                if (shot.FromCapture) DeleteSaved(shot);   // файлы из папки при отмене остаются как есть
                return NothingUploaded;
        }

        try
        {
            string? url = await ShareAsync(shot, choice).ConfigureAwait(false);
            return url == null ? NothingUploaded : UploadOutcome.Success(url);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "[nc-share] загрузка не удалась");
            string message = ex is NcException ? ex.Message : ex.Message + "\nПодробности — в логе XerahS.";
            Toast.Error(shot, "Не удалось загрузить в Nextcloud", message);
            return UploadOutcome.Failed(message);
        }
    }

    // Ссылку в буфер кладёт XerahS («Copy URL to clipboard» после загрузки). null — пользователь отменил вход
    async Task<string?> ShareAsync(Shot shot, Choice choice)
    {
        Credential? credential = credentials.Get() ?? await Login.RunAsync(config, credentials, CancellationToken.None).ConfigureAwait(false);
        if (credential == null)
        {
            Toast.Post(shot, "Вход отменён", "Снимок не загружен");
            return null;
        }

        bool isPublic = choice.Kind == ChoiceKind.Public;
        DateTime until = DateTime.Today.AddDays(choice.Days);
        string note = isPublic ? "Публичная, до " + until.ToString("dd.MM.yyyy") : "Для команды, без срока";
        Action<double> progress = Toast.Progress(shot, "Загружаю в Nextcloud…", note);

        NextcloudClient nc = new(config, credential);
        string folder = config.FolderFor(choice.Kind, shot.FileName);
        try
        {
            string? fileId = await nc.UploadAsync(folder, shot.FileName, shot.Data, progress, CancellationToken.None).ConfigureAwait(false);
            string url = isPublic
                ? await nc.CreatePublicLinkAsync(folder + "/" + shot.FileName, until, CancellationToken.None).ConfigureAwait(false)
                  + (shot.IsImage ? config.PublicLinkSuffix : config.FilePublicLinkSuffix)
                // Без index.php сервер отдаёт 404
                : fileId != null ? config.Server + "/index.php/f/" + fileId
                                 : config.Server + "/index.php/apps/files/?dir=" + Uri.EscapeDataString(folder);
            DebugHelper.WriteLine($"[nc-share] загружено: {folder}/{shot.FileName} → {url}");
            Toast.Post(shot, "Ссылка скопирована", note, url);
            return url;
        }
        catch (NcException ex) when (ex.Unauthorized)
        {
            credentials.Forget();   // при следующей загрузке nc-share попросит войти заново
            throw;
        }
    }

    // Esc в меню — снимка как не было: XerahS уже сохранил файл, удаляем его, но только из папки снимков XerahS
    static void DeleteSaved(Shot shot)
    {
        string? path = shot.FilePath;
        if (path == null) return;
        string folder = Path.GetFullPath(PathsManager.ScreenshotsFolder) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.Ordinal)) return;
        try
        {
            File.Delete(path);
            DebugHelper.WriteLine($"[nc-share] отмена: удалён {path}");
        }
        catch (IOException ex) { DebugHelper.WriteLine($"[nc-share] не удалось удалить {path}: {ex.Message}"); }
    }

    static void Log(string method, string fileName)
    {
        DebugHelper.WriteLine($"[nc-share] вызов {Interlocked.Increment(ref calls)}: {method} \"{fileName}\", " +
            $"поток {Environment.CurrentManagedThreadId}, поток интерфейса: {Dispatcher.UIThread.CheckAccess()}");
    }
}

// Что пришло от XerahS: снимок или файл из папки. Байты для загрузки, картинка для миниатюры и буфера, путь к файлу
public sealed class Shot
{
    public required string FileName { get; init; }
    public required byte[] Data { get; init; }
    public string? FilePath { get; init; }
    public bool FromCapture { get; init; }
    public bool IsImage => NcShareConfig.IsImage(FileName);

    public static Shot Read(Stream stream, string fileName, NcShareConfig config, bool fromCapture)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] data = copy.ToArray();
        string? path = (stream as FileStream)?.Name;
        if (fromCapture && config.FixEditorColors && EditorColorFix.Applies(data))
        {
            try
            {
                data = EditorColorFix.SwapRedBlue(data);
                EditorColorFix.RewriteSaved(path, data);
                DebugHelper.WriteLine($"[nc-share] цвета исправлены (снимок из редактора, XerahS #298): {fileName}");
            }
            catch (Exception ex) { DebugHelper.WriteException(ex, "[nc-share] не удалось исправить цвета"); }
        }
        return new Shot { FileName = fileName, Data = data, FilePath = path, FromCapture = fromCapture };
    }

    // Bitmap создаётся в потоке интерфейса, там же, где его рисуют. У видео и прочих файлов миниатюры нет
    public Bitmap? CreateBitmap()
    {
        if (!IsImage) return null;
        try { return new Bitmap(new MemoryStream(Data)); }
        catch (Exception) { return null; }
    }

    public string SizeText => Data.Length >= 1024 * 1024
        ? $"{Data.Length / 1024.0 / 1024.0:0.#} МБ"
        : $"{Math.Ceiling(Data.Length / 1024.0):N0} КБ";
}
