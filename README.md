# xerahs-nextcloud-share — скриншоты со ссылкой в Nextcloud

Сделал снимок или записал видео, поправил в редакторе, выбрал в меню, что с ним сделать: скопировать или получить
ссылку в своём [Nextcloud](https://nextcloud.com) — для команды или публичную со сроком. Снимает и редактирует на macOS
[XerahS](https://github.com/ShareX/XerahS), на Windows — [ShareX](https://getsharex.com); меню и загрузку добавляет
**nc-share** из этого репозитория. Подходит любой сервер Nextcloud — [требования](#nextcloud).

## Как работает

Выдели область → редактор (стрелки, текст, размытие, обрезка) → меню:

| Клавиша | Что делает |
|---|---|
| `1` | копирует изображение в буфер (видео — файлом); в Nextcloud ничего не уходит |
| `2` | ссылка для команды `…/index.php/f/<id>`: без срока, открывается после входа в Nextcloud у тех, у кого есть доступ к папке |
| `3` `4` `5` | публичная ссылка на 1, 7 или 30 дней: открывается без входа, ведёт на саму картинку (у видео — на страницу с плеером) |
| `Esc` | отмена: ничего не загружается, в буфер ничего не попадает. На macOS снимок удаляется, на Windows остаётся в папке ShareX |

Ссылка сразу оказывается в буфере. Уведомление в углу показывает результат, щелчок по нему открывает ссылку. Файлы
раскладываются по типу и месяцу: `<твоя папка>/screenshots/team/2026/10/…`, `…/screenshots/public/…`, видео — в
`…/videos/…`.

| Снимок | macOS (XerahS) | Windows (ShareX) |
|---|---|---|
| область | `Cmd+Shift+4` — системное выделение macOS | `Ctrl+PrintScreen` |
| весь экран | `Cmd+Shift+3` | `PrintScreen` |
| активное окно | `Ctrl+Shift+A` | `Alt+PrintScreen` |
| запись видео | `Cmd+Shift+5` — системная панель, как обычно, без меню | `Shift+PrintScreen`, стоп — тем же сочетанием → меню без редактора |
| запись GIF | — | `Ctrl+Shift+PrintScreen` |

## Установка

macOS — в «Терминале»:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/petrshirin/xerahs-nextcloud-share/main/install-macos.sh)"
```

Windows — в PowerShell:

```powershell
irm https://raw.githubusercontent.com/petrshirin/xerahs-nextcloud-share/main/install-windows.ps1 | iex
```

Установщик поставит XerahS или ShareX, если их нет, и nc-share, спросит папку в Nextcloud (адрес из браузера — сервер
он возьмёт оттуда же) и настроит сочетания. Подробнее — [«Установка»](docs/install.md).

## Nextcloud

| | |
|---|---|
| Версия | **Nextcloud 16 или новее**: с 16-й версии есть вход через браузер (Login Flow v2). Проверено на Nextcloud 33.0.9 |
| Что нужно от сервера | WebDAV (`/remote.php/webdav`), Share API (`/ocs/v2.php/apps/files_sharing`), Login Flow v2 (`/index.php/login/v2`) — всё включено по умолчанию, отдельные приложения не нужны |
| Вход | в браузере, как на сайте: пароль, SSO, двухфакторная проверка. nc-share получает пароль приложения и хранит его в Связке ключей (на Windows — зашифрованным в профиле пользователя); твой пароль он не видит |
| Права | запись в папку. Для публичных ссылок — право делиться, а на сервере должны быть разрешены ссылки (настройки администратора → «Общий доступ») |
| Срок публичной ссылки | 1, 7 и 30 дней. Если администратор ограничил максимальный срок, пункты длиннее лимита сервер отклонит — сроки меняются настройкой `PublicDays` |
| Ссылка для команды | открывается у тех, у кого есть доступ к папке: общая папка (Group folders) или папка, которой ты поделился с коллегами. Файл в личной папке без общего доступа откроешь только ты |
| Адрес сервера | любой, в том числе Nextcloud в подкаталоге (`https://example.org/nextcloud`) |

## Где работает

| ОС | Состояние | Установка |
|---|---|---|
| macOS | работает, проверено на Apple Silicon | [инструкция](docs/install.md#macos) |
| Windows | ShareX и программа `nc-share.exe`: снимки и запись видео, то же меню | [инструкция](docs/install.md#windows-sharex) |
| Linux | не проверялось | — |

## Ограничения

- XerahS пока pre-release (0.31.4), на macOS сыроват: например, активное окно снимается в половинном разрешении.
- `Esc` в редакторе не отменяет снимок, а пропускает правку ([XerahS #297](https://github.com/ShareX/XerahS/issues/297)):
  появится меню — нажми `Esc` ещё раз.
- Редактор XerahS на macOS меняет местами красный и синий ([XerahS #298](https://github.com/ShareX/XerahS/issues/298)).
  Плагин исправляет цвета сам, пока ошибку не починят.
- Обе ошибки исправлены в сборке [KovaForge XerahS 0.33.3](https://github.com/KovaForge/XerahS/releases/tag/v0.33.3),
  но на macOS она падает при запуске, поэтому установщик пока ставит 0.31.4. Плагин готов к исправленным версиям:
  на XerahS новее 0.33.1 он цвета не трогает.
- У nc-share нет окна настроек: сервер и папка задаются установщиком, остальное — в `uploader-instances.json`
  (на Windows — в `nc-share.ini`).

## Репозиторий

| Каталог | Что внутри |
|---|---|
| [`install-macos.sh`](install-macos.sh) | установщик для macOS |
| [`install-windows.ps1`](install-windows.ps1) | установщик для Windows |
| [`xerahs-plugin/`](xerahs-plugin) | плагин nc-share для XerahS: C#, .NET 10, Avalonia |
| [`nc-share/`](nc-share) | nc-share для ShareX на Windows: `nc-share.exe` (C# 5, WinForms) и `nc-share.ini` |
| [`docs/`](docs) | [установка](docs/install.md) |
| [`.github/workflows/release.yml`](.github/workflows/release.yml) | сборка релиза |

## Помощь

Не получилось или есть идея — заведи [issue](https://github.com/petrshirin/xerahs-nextcloud-share/issues): что делал, что
получилось, версии ОС, XerahS или ShareX и Nextcloud, а ещё строки `[nc-share]` из лога XerahS или
`%TEMP%\nc-share.log` на Windows.

## Разработка

Нужен .NET 10 SDK. Плагин собирается против библиотек XerahS (по умолчанию `/Applications/XerahS.app`, путь
меняется свойством `XerahSDir`):

- `xerahs-plugin/install.sh` — собрать и положить в `~/Documents/XerahS/Plugins/macos64/ncshare`, затем
  перезапустить XerahS;
- `xerahs-plugin/pack.sh` — архив для релиза `dist/nc-share-xerahs.zip`.

Лог — `~/Documents/XerahS/Logs/<год-месяц>/`, строки плагина начинаются с `[nc-share]`.

**Windows.** `nc-share.exe` собирает сам установщик компилятором C# из .NET Framework; со своими исходниками —
`$env:NCSHARE_SRC = "$PWD\nc-share"; iex (Get-Content -Raw -Encoding UTF8 .\install-windows.ps1)`. Подробнее —
[«Сборка из исходников»](docs/install.md#сборка-из-исходников-1).

**Выпуск.** Подними версию в `xerahs-plugin/plugin.json` и `NcShareProvider.Version`, затем
`git tag v<версия> && git push origin v<версия>`. GitHub Actions соберёт плагин против библиотек из архива XerahS
и приложит `nc-share-xerahs.zip` к релизу; установщик берёт архив из последнего релиза. Тег должен совпадать с
версией в `plugin.json`, иначе сборка остановится. Проверить сборку без релиза: Actions → release → Run workflow.
