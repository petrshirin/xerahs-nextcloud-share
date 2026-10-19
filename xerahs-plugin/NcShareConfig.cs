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

    // Папка в Nextcloud, куда класть файлы; если её нет, создаётся при первой загрузке.
    // Пока не заданы сервер и папка, доступно только копирование в буфер
    public string BaseFolder { get; set; } = "";

    // Путь внутри BaseFolder. Сначала тип файла, потом доступ — тип постоянен, а виды доступа могут поменяться.
    // {type} — папка по типу файла, {access} — папка по виду ссылки, %y/%mo — год и месяц (шаблон XerahS)
    public string PathTemplate { get; set; } = "{type}/{access}/%y/%mo";
    public string ImagesDir { get; set; } = "screenshots";
    public string VideosDir { get; set; } = "videos";
    public string FilesDir { get; set; } = "files";
    public string TeamDir { get; set; } = "team";
    public string PublicDir { get; set; } = "public";

    // Обход ошибки XerahS #298: исправлять цвета снимков после редактора (работает только на версиях XerahS с ошибкой)
    public bool FixEditorColors { get; set; } = true;

    // Сроки публичной ссылки в днях — по пункту меню на каждый
    public string PublicDays { get; set; } = "1, 7, 30";

    // Что копировать для публичной ссылки: /preview — сама картинка, пусто — страница Nextcloud, /download — скачивание
    public string PublicLinkSuffix { get; set; } = "/preview";

    // То же для видео и прочих файлов: /preview у видео — только кадр, а страница Nextcloud показывает плеер
    public string FilePublicLinkSuffix { get; set; } = "";

    [JsonIgnore]
    public string SecretKeyOrDefault => string.IsNullOrWhiteSpace(SecretKey) ? "default" : SecretKey;

    [JsonIgnore]
    public string Server => ServerUrl.Trim().TrimEnd('/');

    [JsonIgnore]
    public bool UploadReady => Server.Length > 0 && BaseFolder.Trim().Length > 0;

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

    // Папка для загрузки, например BaseFolder/screenshots/team/2026/10
    public string FolderFor(ChoiceKind kind, string fileName)
    {
        string access = kind == ChoiceKind.Public ? PublicDir : TeamDir;
        string path = PathTemplate.Replace("{type}", TypeDirFor(fileName).Trim('/')).Replace("{access}", access.Trim('/'));
        path = NameParser.Parse(NameParserType.Default, path);
        return string.Join("/", new[] { BaseFolder.Trim().TrimEnd('/') }.Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries)));
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
