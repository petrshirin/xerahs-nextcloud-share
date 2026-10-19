using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using XerahS.Common;

namespace NcShare.Plugin;

// Ошибка, понятная пользователю: показывается в уведомлении как есть
public sealed class NcException : Exception
{
    public NcException(string message) : base(message) { }

    // 401: пароль приложения отозван или неверен — его нужно забыть и войти заново
    public bool Unauthorized { get; init; }
}

public sealed record Credential(string LoginName, string AppPassword);

public sealed record LoginFlow(string LoginUrl, string PollEndpoint, string PollToken);

// Nextcloud: WebDAV для загрузки, OCS API для публичных ссылок, Login Flow v2 для входа через браузер
public sealed class NextcloudClient
{
    // Соединение одно на весь XerahS: прогрев, пока открыто меню, ускоряет загрузку
    static readonly HttpClient Http = CreateHttp();

    readonly NcShareConfig config;
    readonly Credential credential;

    public NextcloudClient(NcShareConfig config, Credential credential)
    {
        this.config = config;
        this.credential = credential;
    }

    static HttpClient CreateHttp()
    {
        SocketsHttpHandler handler = new()
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),   // зависшая попытка соединения не тянется полминуты
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // Под этим именем пароль приложения виден в Nextcloud: Настройки → Безопасность
        http.DefaultRequestHeaders.UserAgent.ParseAdd("nc-share/0.5 (XerahS)");
        return http;
    }

    // DNS и TLS (около секунды) устанавливаются в фоне, пока пользователь выбирает пункт меню
    public static void Warmup(NcShareConfig config)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                using HttpResponseMessage r = await Http.GetAsync(config.Server + "/status.php", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { }   // не удалось — загрузка откроет соединение сама
        });
    }

    // Возвращает id файла для внутренней ссылки /index.php/f/<id> или null. progress — доля отправленного (0…1)
    public async Task<string?> UploadAsync(string folder, string fileName, byte[] data, Action<double> progress, CancellationToken cancel)
    {
        string path = folder + "/" + fileName;
        // Видео бывают большими: на файл даём минуту плюс секунду на каждые 100 КБ
        TimeSpan timeout = TimeSpan.FromSeconds(60 + data.Length / 100_000);
        Response r = await SendAsync(HttpMethod.Put, DavUrl(path), () => new ProgressContent(data, progress), cancel, timeout).ConfigureAwait(false);
        if (r.Status == HttpStatusCode.Conflict)   // папки ещё нет — создаём по уровням и повторяем
        {
            await CreateFoldersAsync(folder, cancel).ConfigureAwait(false);
            r = await SendAsync(HttpMethod.Put, DavUrl(path), () => new ProgressContent(data, progress), cancel, timeout).ConfigureAwait(false);
        }
        Check(r, "Загрузка файла");
        // OC-FileId = id файла с нулями слева + id инстанса: "00012345oc..." → 12345
        Match m = Regex.Match(r.FileId ?? "", @"^(\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null;
    }

    public async Task<string> CreatePublicLinkAsync(string path, DateTime until, CancellationToken cancel)
    {
        Dictionary<string, string> form = new()
        {
            ["path"] = "/" + path.TrimStart('/'),   // путь от корня файлов пользователя: /Screenshots/…
            ["shareType"] = "3",
            ["permissions"] = "1",
            ["expireDate"] = until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        Response r = await SendAsync(HttpMethod.Post, config.Server + "/ocs/v2.php/apps/files_sharing/api/v1/shares?format=json",
            () => new FormUrlEncodedContent(form), cancel).ConfigureAwait(false);
        Check(r, "Создание публичной ссылки");
        // OCS v2 отвечает statuscode 200, v1 — 100
        JToken? ocs = JObject.Parse(r.Body)["ocs"];
        int? status = ocs?["meta"]?["statuscode"]?.Value<int?>();
        string? url = ocs?["data"]?["url"]?.Value<string>();
        if (status is not (100 or 200) || string.IsNullOrEmpty(url))
            throw new NcException("Создание публичной ссылки: " + (ocs?["meta"]?["message"]?.Value<string>() ?? "сервер не вернул ссылку"));
        return url;
    }

    // Подпапки создаются от BaseFolder вниз. Если нет и самой BaseFolder, сначала она — от корня.
    // Выше BaseFolder без нужды не ходим: там может не быть права записи
    async Task CreateFoldersAsync(string folder, CancellationToken cancel)
    {
        string base_ = config.BaseFolder.Trim().TrimEnd('/');
        string inside = folder.Substring(base_.Length);
        if (await CreateLevelsAsync(base_, inside, cancel).ConfigureAwait(false)) return;
        await CreateLevelsAsync("", base_, cancel).ConfigureAwait(false);
        await CreateLevelsAsync(base_, inside, cancel).ConfigureAwait(false);
    }

    // MKCOL по уровням; 405 — папка уже есть. false — нет родительской папки (409 на первом уровне)
    async Task<bool> CreateLevelsAsync(string parent, string relative, CancellationToken cancel)
    {
        string current = parent;
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            bool first = current == parent;
            current += "/" + part;
            Response r = await SendAsync(new HttpMethod("MKCOL"), DavUrl(current), null, cancel).ConfigureAwait(false);
            if (first && r.Status == HttpStatusCode.Conflict) return false;
            if (r.Status != HttpStatusCode.MethodNotAllowed) Check(r, "Создание папки " + current);
        }
        return true;
    }

    string DavUrl(string path)
    {
        IEnumerable<string> parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return config.Server + "/remote.php/webdav/" + string.Join("/", parts);
    }

    // Через VPN и туннели соединение иногда не устанавливается — сетевые сбои повторяем, ответы сервера нет.
    // Повтор безопасен: PUT перезаписывает тот же файл, MKCOL на существующую папку даёт 405.
    const int Attempts = 3;

    async Task<Response> SendAsync(HttpMethod method, string url, Func<HttpContent>? content, CancellationToken cancel, TimeSpan? limit = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(60));
            try
            {
                using HttpRequestMessage request = new(method, url) { Content = content?.Invoke() };
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.LoginName + ":" + credential.AppPassword)));
                request.Headers.Add("OCS-APIRequest", "true");
                using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                return new Response(response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false),
                    response.Headers.TryGetValues("OC-FileId", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null);
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                DebugHelper.WriteLine($"[nc-share] {method} {url}: попытка {attempt} не удалась за {watch.ElapsedMilliseconds} мс — {ex.Message}");
                if (attempt >= Attempts)
                    throw new NcException($"Нет связи с {config.Server} после {Attempts} попыток: {ex.Message}");
                await Task.Delay(500, cancel).ConfigureAwait(false);
            }
        }
    }

    static void Check(Response r, string action)
    {
        if ((int)r.Status < 400) return;
        string hint = r.Status switch
        {
            HttpStatusCode.Unauthorized => "Пароль приложения не подходит — при следующей загрузке nc-share попросит войти заново.",
            HttpStatusCode.Forbidden => "Нет прав в папке (нужны запись и, для публичных ссылок, право делиться) или публичные ссылки запрещены на сервере.",
            HttpStatusCode.NotFound => "Не найден путь — проверьте адрес сервера и папку (BaseFolder) в настройках nc-share.",
            HttpStatusCode.Conflict => "Не удалось создать папку — проверьте BaseFolder в настройках nc-share.",
            _ => r.Body.Length > 300 ? r.Body.Substring(0, 300) : r.Body
        };
        throw new NcException($"{action} — HTTP {(int)r.Status}\n{hint}") { Unauthorized = r.Status == HttpStatusCode.Unauthorized };
    }

    // --- Вход через браузер (Login Flow v2): пароль приложения создаётся сам, SSO проходит в браузере ---

    public static async Task<LoginFlow> StartLoginFlowAsync(NcShareConfig config, CancellationToken cancel)
    {
        using HttpResponseMessage r = await Http.PostAsync(config.Server + "/index.php/login/v2", null, cancel).ConfigureAwait(false);
        string body = await r.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new NcException($"Вход в Nextcloud — HTTP {(int)r.StatusCode}");
        JObject json = JObject.Parse(body);
        return new LoginFlow(
            json["login"]?.Value<string>() ?? throw new NcException("Nextcloud не вернул адрес входа"),
            json["poll"]?["endpoint"]?.Value<string>() ?? throw new NcException("Nextcloud не вернул адрес проверки входа"),
            json["poll"]?["token"]?.Value<string>() ?? throw new NcException("Nextcloud не вернул токен входа"));
    }

    // null — пользователь ещё не подтвердил вход в браузере
    public static async Task<Credential?> PollLoginFlowAsync(LoginFlow flow, CancellationToken cancel)
    {
        using HttpResponseMessage r = await Http.PostAsync(flow.PollEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = flow.PollToken }), cancel).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        string body = await r.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new NcException($"Вход в Nextcloud — HTTP {(int)r.StatusCode}");
        JObject json = JObject.Parse(body);
        string? login = json["loginName"]?.Value<string>(), password = json["appPassword"]?.Value<string>();
        return string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password) ? null : new Credential(login, password);
    }

    sealed record Response(HttpStatusCode Status, string Body, string? FileId);

    // Тело PUT отправляется частями, чтобы уведомление показывало, сколько ушло. При повторе отсчёт начинается заново
    sealed class ProgressContent : HttpContent
    {
        const int Chunk = 64 * 1024;
        readonly byte[] data;
        readonly Action<double> progress;

        public ProgressContent(byte[] data, Action<double> progress)
        {
            this.data = data;
            this.progress = progress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            progress(0);
            for (int sent = 0; sent < data.Length;)
            {
                int n = Math.Min(Chunk, data.Length - sent);
                await stream.WriteAsync(data.AsMemory(sent, n), cancellationToken).ConfigureAwait(false);
                sent += n;
                progress((double)sent / data.Length);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }
    }
}
