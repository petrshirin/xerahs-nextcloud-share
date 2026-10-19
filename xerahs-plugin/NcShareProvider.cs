// nc-share — плагин загрузки для XerahS: после снимка показывает меню, как nc-share для ShareX:
// скопировать изображение, ссылка для команды или публичная ссылка со сроком
using Newtonsoft.Json;
using XerahS.Uploaders;
using XerahS.Uploaders.PluginSystem;

namespace NcShare.Plugin;

public sealed class NcShareProvider : UploaderProviderBase
{
    public override string ProviderId => "ncshare";
    public override string Name => "nc-share";
    public override string Description => "Меню после снимка: буфер, ссылка для команды или публичная ссылка со сроком (Nextcloud)";
    public override Version Version => new(0, 5, 1);
    // Image — снимки XerahS, File — файлы из папки, за которой следит XerahS (записи и снимки системной панели Cmd+Shift+5)
    public override UploaderCategory[] SupportedCategories => new[] { UploaderCategory.Image, UploaderCategory.File };
    public override Type ConfigModelType => typeof(NcShareConfig);

    public override Uploader CreateInstance(string settingsJson)
    {
        NcShareConfig config = JsonConvert.DeserializeObject<NcShareConfig>(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson) ?? new();
        return new NcShareUploader(config, new CredentialStore(Secrets, config.SecretKeyOrDefault, config.Server));
    }

    public override Dictionary<UploaderCategory, string[]> GetSupportedFileTypes() => new()
    {
        [UploaderCategory.Image] = new[] { "png", "jpg", "jpeg", "gif", "webp", "bmp" },
        [UploaderCategory.File] = new[] { "png", "jpg", "jpeg", "gif", "webp", "bmp", "heic", "mov", "mp4", "webm", "mkv", "m4v", "pdf", "zip", "txt", "log" }
    };
}
