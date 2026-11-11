using Newtonsoft.Json;
using XerahS.Common;

namespace NcShare.Plugin;

// Настройки подключения nc-share — то же, что nc-share.ini у версии для ShareX. Хранит их XerahS (uploader-instances.json),
// логин и пароль приложения лежат отдельно, в хранилище секретов XerahS (на macOS — Связка ключей)
public sealed class NcShareConfig
{
    // Ключ, под которым вход лежит в хранилище секретов. Пусто — общий ключ: XerahS создаёт подключение с настройками «{}»,
    // и случайный ключ по умолчанию менялся бы при каждом снимке
    public string SecretKey { get; set; } = "";

    // Адрес Nextcloud, например https://cloud.example.com (если Nextcloud стоит в подкаталоге — вместе с ним)
    public string ServerUrl { get; set; } = "";

    // Папка команды: сюда ложатся файлы для ссылки «для команды» и публичных ссылок (если не задана PublicFolder); если её
    // нет, создаётся при первой загрузке. Ссылку для команды откроют те, у кого есть доступ к папке. Пока не заданы сервер
    // и папка, этих пунктов нет
    public string BaseFolder { get; set; } = "";

    // Папка, доступная всем пользователям Nextcloud, — пункт «Ссылка для всех». Внутри неё у каждого своя подпапка
    // с логином: файлы коллег не смешиваются и не перезаписываются. Пусто — пункта в меню нет
    public string EveryoneFolder { get; set; } = "";

    // Папка для публичных ссылок — если на сервере они разрешены не везде, а только в ней. Внутри у каждого своя подпапка
    // с логином, как в папке для всех. Пусто — публичные ссылки из папки команды
    public string PublicFolder { get; set; } = "";

    // Публичные ссылки создаёт отдельная учётка: файл загружает своя, а делиться без пароля может только эта.
    // Её логин и пароль приложения лежат в хранилище секретов, их кладёт установщик (install-macos.sh --link-account)
    public bool LinkAccount { get; set; }

    // Путь внутри папки. Сначала тип файла, потом доступ — тип постоянен, а виды доступа могут поменяться.
    // {type} — папка по типу файла, {access} — папка по виду ссылки (в папке для всех её нет), %y/%mo — год и месяц
    public string PathTemplate { get; set; } = "{type}/{access}/%y/%mo";
    public string ImagesDir { get; set; } = "screenshots";
    public string VideosDir { get; set; } = "videos";
    public string FilesDir { get; set; } = "files";
    public string TeamDir { get; set; } = "team";
    public string PublicDir { get; set; } = "public";

    // Сроки публичной ссылки в днях — по пункту меню на каждый
    public string PublicDays { get; set; } = "1, 7, 30";

    // Что копировать для публичной ссылки: /preview — сама картинка, пусто — страница Nextcloud, /download — скачивание
    public string PublicLinkSuffix { get; set; } = "/preview";

    // То же для видео и прочих файлов: /preview у видео — только кадр, а страница Nextcloud показывает плеер
    public string FilePublicLinkSuffix { get; set; } = "";

    [JsonIgnore]
    public string SecretKeyOrDefault => string.IsNullOrWhiteSpace(SecretKey) ? "default" : SecretKey;

    // Вход учётки для публичных ссылок — рядом со своим, под отдельным ключом
    [JsonIgnore]
    public string LinkSecretKey => SecretKeyOrDefault + "-links";

    [JsonIgnore]
    public string Server => ServerUrl.Trim().TrimEnd('/');

    [JsonIgnore]
    public bool UploadReady => Server.Length > 0 && BaseFolder.Trim().Length > 0;

    [JsonIgnore]
    public bool HasPublicFolder => PublicFolder.Trim().Length > 0;

    [JsonIgnore]
    public bool PublicReady => HasPublicFolder ? Server.Length > 0 : UploadReady;

    [JsonIgnore]
    public bool HasEveryoneFolder => EveryoneFolder.Trim().Length > 0;

    [JsonIgnore]
    public bool EveryoneReady => Server.Length > 0 && HasEveryoneFolder;

    [JsonIgnore]
    public int[] Days
    {
        get
        {
            List<int> days = new();
            foreach (string part in PublicDays.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int d) && d > 0) days.Add(d);
            return days.Count > 0 ? days.ToArray() : new[] { 1, 7, 30 };
        }
    }

    // Куда загружать: BaseFolder/screenshots/team/2026/10, EveryoneFolder/<логин>/screenshots/2026/10 или
    // PublicFolder/<логин>/screenshots/2026/10. Root — папка, выше которой плагин не создаёт папок: её дал администратор
    // или выбрал пользователь. Отдельные папки (для всех, для публичных ссылок) — уже по смыслу, папки доступа в них нет
    public UploadTarget TargetFor(ChoiceKind kind, string fileName, string login)
    {
        string? own = kind == ChoiceKind.Everyone ? EveryoneFolder
                    : kind == ChoiceKind.Public && HasPublicFolder ? PublicFolder : null;
        string root = Normalize(own ?? BaseFolder);
        string access = own != null ? "" : kind == ChoiceKind.Public ? PublicDir : TeamDir;
        string path = PathTemplate.Replace("{type}", TypeDirFor(fileName).Trim('/')).Replace("{access}", access.Trim('/'));
        path = NameParser.Parse(NameParserType.Default, path);
        if (own != null) path = SafeName(login) + "/" + path;
        return new UploadTarget(root, string.Join("/", new[] { root }.Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries))));
    }

    static string Normalize(string folder) => "/" + folder.Trim().Trim('/');

    // Логин — имя папки: без разделителей пути
    static string SafeName(string login)
    {
        string name = login.Trim().Replace('/', '_').Replace('\\', '_');
        return name.Length == 0 || name == "." || name == ".." ? "_" : name;
    }

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".heic" };
    // GIF у XerahS — запись экрана, поэтому к видео
    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".gif" };

    public static bool IsImage(string fileName) => ImageExtensions.Contains(Path.GetExtension(fileName));

    string TypeDirFor(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ImageExtensions.Contains(ext) ? ImagesDir : VideoExtensions.Contains(ext) ? VideosDir : FilesDir;
    }
}

// Folder — куда класть файл, Root — граница, выше которой папки не создаются
public sealed record UploadTarget(string Root, string Folder);
