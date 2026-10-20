#!/bin/bash
# Установка nc-share на macOS одной командой:
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/petrshirin/xerahs-nextcloud-share/main/install-macos.sh)"
#
# Ставит XerahS (если его нет) и плагин nc-share из последнего релиза, отдаёт XerahS сочетания Cmd+Shift+3/4
# и настраивает сценарии: снимок → редактор → меню nc-share. Перед правкой настройки XerahS и сочетания macOS
# копируются в ~/Documents/XerahS/nc-share-backup/<дата>. Повторный запуск обновляет плагин; сервер и папка
# Nextcloud сохраняются, если их не поменять.
#
# Переменные — для проверки и установки без вопросов:
#   NCSHARE_REPO          репозиторий GitHub «владелец/имя», из релизов которого берётся плагин
#   NCSHARE_ZIP           свой архив плагина вместо скачивания (собирает xerahs-plugin/pack.sh)
#   NCSHARE_BASE_FOLDER   папка в Nextcloud: адрес из браузера (заодно задаёт сервер) или путь /Папка
#   NCSHARE_SERVER        адрес Nextcloud, если папка задана путём, например https://cloud.example.com
#   NCSHARE_REMOVE_OTHER  1 — убрать другие подключения для картинок и файлов без вопроса, 0 — оставить
set -euo pipefail

REPO="${NCSHARE_REPO:-petrshirin/xerahs-nextcloud-share}"
# XerahS, с которым проверен и собран плагин. Исправления #297 и #298 есть в сборке KovaForge 0.33.3, но она
# на macOS падает при запуске (меню в строке меню), поэтому пока 0.31.4 — их обходит сам плагин
XERAHS_VERSION=0.31.4
XERAHS_URL="https://github.com/ShareX/XerahS/releases/download/v$XERAHS_VERSION"
XERAHS_SHA256_arm64=25d38773c83926d2fd55597f9fb63bed832d3f3954502484dd76af729e96810c
XERAHS_SHA256_x64=3038f00b686ef1474d554f6a5c5234f994824061b3503397ab93b18a3fba84a7

APP=/Applications/XerahS.app
DATA="$HOME/Documents/XerahS"
SETTINGS="$DATA/Settings"
PLUGIN_DIR="$DATA/Plugins/macos64/ncshare"
BACKUP="$DATA/nc-share-backup/$(date +%Y-%m-%d_%H%M%S)"
ACTIVATE=/System/Library/PrivateFrameworks/SystemAdministration.framework/Resources/activateSettings

title() { printf '\n\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
warn() { printf '  \033[33m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }
# Ответ читается с терминала: при запуске через curl | bash stdin занят самим скриптом
ask() { printf '  %s' "$1"; REPLY=""; { IFS= read -r REPLY < /dev/tty; } 2>/dev/null || echo; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

running() { pgrep -x XerahS >/dev/null; }

# XerahS сохраняет настройки при запуске, а не при выходе, поэтому правки при закрытом XerahS не теряются
stop_xerahs() {
    running || return 0
    pkill -TERM -x XerahS || true
    for _ in $(seq 1 30); do running || return 0; sleep 0.5; done
    fail "XerahS не закрылся. Закрой его через значок в строке меню и запусти установку ещё раз."
}

workflows_file() { ls -t "$SETTINGS"/WorkflowsConfig-*.json 2>/dev/null | head -1 || true; }

# Версия $1 старше $2 (вида 0.33.3)
older() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -t. -k1,1n -k2,2n -k3,3n | head -1)" = "$1" ]; }

# Системное сочетание выключается с правильными типами: в виде '{ enabled = 0; }' defaults пишет строки,
# и macOS такую запись не понимает
hotkey_off() {
    defaults write com.apple.symbolichotkeys AppleSymbolicHotKeys -dict-add "$1" \
        "<dict><key>enabled</key><false/><key>value</key><dict><key>parameters</key><array><integer>$2</integer><integer>$3</integer><integer>1179648</integer></array><key>type</key><string>standard</string></dict></dict>"
}

# Настройки XerahS — JSON; правятся через JavaScript for Automation, он есть в любой macOS
cat > "$tmp/configure.js" <<'JS'
ObjC.import("Foundation");

const IMAGE = 0, FILE = 2;
// Сочетания сценариев снимков: Cmd+Shift+3/4 и Ctrl+Shift+A
const CAPTURE = { PrintScreen: ["D3", "Shift, Meta"], RectangleRegion: ["D4", "Shift, Meta"], ActiveWindow: ["A", "Control, Shift"] };

function readJson(path, fallback) {
    if (!$.NSFileManager.defaultManager.fileExistsAtPath(path)) return fallback;
    const text = ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null));
    return text && text.trim() ? JSON.parse(text.replace(/^﻿/, "")) : fallback;
}

function writeJson(path, value) {
    if (!$(JSON.stringify(value, null, 2)).writeToFileAtomicallyEncodingError(path, true, $.NSUTF8StringEncoding, null))
        throw new Error("не удалось записать " + path);
}

const category = i => i.Category === "Image" ? IMAGE : i.Category === "File" ? FILE : i.Category;
const isOurs = i => i.ProviderId === "ncshare";
const isForeign = i => !isOurs(i) && (category(i) === IMAGE || category(i) === FILE);
const instances = path => readJson(path, {}).Instances || [];

// Адрес папки из браузера: https://cloud.example.com[/подкаталог][/index.php]/apps/files/…?dir=/Папка
const FILES_URL = /^(https?:\/\/[^?#]*?)(?:\/index\.php)?\/apps\/files\b[^?#]*\?(?:[^#]*&)?dir=([^&#]*)/i;

// Адрес папки или сам путь → /Папка; корень не годится
function folder(input) {
    let s = (input || "").trim();
    const m = s.match(FILES_URL);
    if (m) s = decodeURIComponent(m[2].replace(/\+/g, "%20"));
    else if (/^[a-z]+:/i.test(s)) return "";
    s = s.replace(/\/+$/, "");
    if (!s) return "";
    return s.startsWith("/") ? s : "/" + s;
}

// Сервер из адреса папки; у простого пути сервера нет
function folderServer(input) {
    const m = (input || "").trim().match(FILES_URL);
    return m ? m[1].replace(/\/+$/, "") : "";
}

// cloud.example.com, https://cloud.example.com/index.php/apps/files/… → https://cloud.example.com
function server(input) {
    let s = (input || "").trim();
    if (!s) return "";
    if (!/^https?:\/\//i.test(s)) s = "https://" + s;
    return s.replace(/[?#].*$/, "").replace(/(\/index\.php)?\/apps\/.*$/i, "").replace(/\/+$/, "");
}

// Текущая настройка подключения nc-share: ServerUrl или BaseFolder
function current(path, field) {
    for (const i of instances(path).filter(isOurs).sort((a, b) => category(a) - category(b))) {
        try { const v = JSON.parse(i.SettingsJson || "{}")[field]; if (v) return v; } catch (e) {}
    }
    return "";
}

function foreign(path) {
    return instances(path).filter(isForeign)
        .map(i => (i.DisplayName || i.ProviderId) + (category(i) === IMAGE ? " (картинки)" : " (файлы)")).join("\n");
}

function newId() {
    let s = "";
    for (let k = 0; k < 40; k++) s += Math.floor(Math.random() * 16).toString(16);
    return s;
}

// Флаги вида "A, B, C": добавить нужные, убрать лишние, остальное не трогать
function flags(value, add, remove) {
    const set = String(value || "").split(",").map(s => s.trim()).filter(s => s && s !== "None" && !remove.includes(s));
    for (const a of add) if (!set.includes(a)) set.push(a);
    return set.join(", ");
}

const hotkey = ([key, mods]) => ({ Key: key, Modifiers: mods, IsValid: true, IsOnlyModifiers: false,
                                   HasControl: mods.includes("Control"), HasAlt: mods.includes("Alt"),
                                   HasShift: mods.includes("Shift"), HasMeta: mods.includes("Meta") });
const noHotkey = () => ({ Key: "None", Modifiers: "None", IsValid: false, IsOnlyModifiers: false,
                          HasControl: false, HasAlt: false, HasShift: false, HasMeta: false });

function apply(instancesPath, workflowsPath, appPath, serverUrl, baseFolder, removeOthers) {
    const out = [];
    const cfg = readJson(instancesPath, {});
    cfg.Instances = cfg.Instances || [];
    cfg.DefaultInstances = cfg.DefaultInstances || {};
    if (removeOthers) {
        const removed = cfg.Instances.filter(isForeign);
        cfg.Instances = cfg.Instances.filter(i => !isForeign(i));
        for (const i of removed) out.push("убрано подключение «" + (i.DisplayName || i.ProviderId) + "»");
    }

    // Два подключения: картинки — для снимков, файлы — для «File upload». XerahS перебирает обе категории
    // как запасные, так что и во второй должен стоять nc-share
    const now = new Date().toISOString();
    const ids = {};
    for (const [cat, key, name] of [[IMAGE, "Image", "nc-share"], [FILE, "File", "nc-share (файлы)"]]) {
        let inst = cfg.Instances.find(i => isOurs(i) && category(i) === cat);
        if (!inst) {
            inst = { InstanceId: newId(), ProviderId: "ncshare", Category: cat, DisplayName: name, SettingsJson: "{}",
                     FileTypeRouting: { AllFileTypes: false, FileExtensions: [] }, CreatedAt: now, ModifiedAt: now,
                     IsAvailable: true };
            cfg.Instances.push(inst);
            out.push("создано подключение «" + name + "»");
        }
        let settings = {};
        try { settings = JSON.parse(inst.SettingsJson || "{}"); } catch (e) {}
        if (settings.ServerUrl !== serverUrl || settings.BaseFolder !== baseFolder) {
            settings.ServerUrl = serverUrl;
            settings.BaseFolder = baseFolder;
            inst.SettingsJson = JSON.stringify(settings);
            inst.ModifiedAt = now;
        }
        cfg.DefaultInstances[key] = inst.InstanceId;
        ids[cat] = inst.InstanceId;
    }

    const wf = readJson(workflowsPath, null);
    if (!wf || !Array.isArray(wf.Hotkeys)) throw new Error("не понял формат " + workflowsPath);
    const done = [], ours = [];
    for (const h of wf.Hotkeys) {
        const ts = h.TaskSettings, job = ts && ts.Job;
        if (!ts) continue;
        if (job in CAPTURE) {
            // Без CopyImageToClipboard и своего уведомления: в буфер кладёт и уведомление показывает плагин,
            // иначе Esc в меню нечего было бы отменять
            ts.AfterCaptureJob = flags(ts.AfterCaptureJob, ["AnnotateImage", "SaveImageToFile", "UploadImageToHost"], ["CopyImageToClipboard"]);
            ts.AfterUploadJob = flags(ts.AfterUploadJob, ["CopyURLToClipboard"], []);
            ts.DestinationInstanceId = ids[IMAGE];
            ts.CaptureSettings = ts.CaptureSettings || {};
            if (job === "PrintScreen") ts.CaptureSettings.UseModernCapture = false;   // системная screencapture: Retina, без курсора
            if (job === "RectangleRegion") ts.CaptureSettings.MacOSRegionSelectorPreference = "NativeCrosshair";
            if (!done.includes(job)) {
                h.HotkeyInfo = hotkey(CAPTURE[job]);
                ours.push(h);
            }
        } else if (job === "FileUpload") {
            ts.AfterCaptureJob = flags(ts.AfterCaptureJob, [], ["CopyImageToClipboard"]);   // буфер — забота плагина
            ts.DestinationInstanceId = ids[FILE];
        } else continue;
        (ts.GeneralSettings = ts.GeneralSettings || {}).ShowToastNotificationAfterTaskCompleted = false;
        done.push(job);
    }
    // Те же сочетания у других сценариев перехватили бы наши
    for (const h of wf.Hotkeys) {
        const k = h.HotkeyInfo;
        if (k && !ours.includes(h) && Object.values(CAPTURE).some(([key, mods]) => k.Key === key && k.Modifiers === mods)) {
            h.HotkeyInfo = noHotkey();
            out.push("сочетание снято со сценария «" + h.Name + "»");
        }
    }
    for (const job of Object.keys(CAPTURE)) if (!done.includes(job)) out.push("ВНИМАНИЕ: нет сценария " + job);
    // Сценарий OCR (Shift+PrintScreen) добавляет мастер первого запуска XerahS, к nc-share он не относится
    const ocr = wf.Hotkeys.filter(h => h.TaskSettings && h.TaskSettings.Job === "OCR");
    if (ocr.length) {
        wf.Hotkeys = wf.Hotkeys.filter(h => !ocr.includes(h));
        out.push("убран сценарий OCR");
    }

    // «Start minimized to tray»: на macOS XerahS тогда живёт только в строке меню — без окна при запуске и
    // без значка в Dock. Значок в строке меню нужен, иначе программу не видно совсем
    const app = readJson(appPath, null);
    if (app) {
        app.ShowTray = true;
        app.SilentRun = true;
        // Мастер первого запуска переносит снимки в выбранную в нём папку — возвращаем папку XerahS по умолчанию
        app.UseCustomScreenshotsPath = false;
        app.CustomScreenshotsPath = "";
        // Assistant (Ctrl+Shift+Space) к снимкам не относится, а сочетание занимает
        app.AssistantEnabled = false;
    }

    writeJson(workflowsPath, wf);
    writeJson(instancesPath, cfg);
    out.push("сценарии: весь экран, область, активное окно → редактор → nc-share");
    if (app) {
        writeJson(appPath, app);
        out.push("XerahS запускается свёрнутым: только значок в строке меню");
        out.push("снимки сохраняются в ~/Documents/XerahS/Screenshots, Assistant выключен");
    } else out.push("ВНИМАНИЕ: нет " + appPath);
    return out.join("\n");
}

// Сразу в системе: одного plist мало, macOS применяет его не всегда. Возвращает сочетания, которые не выключились
function hotkeysOff(keys) {
    $.NSBundle.bundleWithPath("/System/Library/PrivateFrameworks/SkyLight.framework").load;
    ObjC.bindFunction("CGSSetSymbolicHotKeyEnabled", ["int", ["int", "bool"]]);
    ObjC.bindFunction("CGSIsSymbolicHotKeyEnabled", ["bool", ["int"]]);
    for (const k of keys) $.CGSSetSymbolicHotKeyEnabled(k, false);
    return keys.filter(k => $.CGSIsSymbolicHotKeyEnabled(k)).join(" ");
}

function run(argv) {
    const [cmd, ...args] = argv;
    switch (cmd) {
        case "folder": return folder(args[0]);
        case "folder-server": return folderServer(args[0]);
        case "server": return server(args[0]);
        case "current-server": return current(args[0], "ServerUrl");
        case "current-folder": return current(args[0], "BaseFolder");
        case "foreign": return foreign(args[0]);
        case "apply": return apply(args[0], args[1], args[2], args[3], args[4], args[5] === "1");
        case "hotkeys-off": return hotkeysOff(args.map(Number));
    }
    throw new Error("неизвестная команда " + cmd);
}
JS
jxa() { osascript -l JavaScript "$tmp/configure.js" "$@"; }

[ "$(uname -s)" = Darwin ] || fail "Этот установщик — для macOS."
# Под Rosetta uname -m говорит x86_64 и на Apple Silicon, поэтому смотрим на процессор
if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then arch=arm64; else arch=x64; fi

title "1/5. XerahS"
# fresh: 1 — XerahS поставлен впервые, 2 — обновлён; в обоих случаях нужно разрешение на запись экрана
fresh=0
have=""
[ ! -d "$APP" ] || have=$(defaults read "$APP/Contents/Info" CFBundleShortVersionString 2>/dev/null || echo "?")
if [ -n "$have" ] && ! older "$have" "$XERAHS_VERSION"; then
    info "Уже установлен, версия $have."
    [ "$have" = "$XERAHS_VERSION" ] || warn "nc-share проверен с XerahS $XERAHS_VERSION, с версией $have возможны сбои."
else
    # Плагин собран против библиотек этой версии: на более старом XerahS он может не загрузиться
    [ -z "$have" ] || info "Установлен XerahS $have, nc-share нужен $XERAHS_VERSION — обновляю, настройки сохранятся."
    info "Скачиваю XerahS $XERAHS_VERSION для $arch (около 180 МБ)…"
    sha_var="XERAHS_SHA256_$arch"
    curl -fL --progress-bar -o "$tmp/xerahs.tar.gz" "$XERAHS_URL/XerahS-$XERAHS_VERSION-mac-$arch.tar.gz" \
        || fail "Не удалось скачать XerahS."
    echo "${!sha_var}  $tmp/xerahs.tar.gz" | shasum -a 256 -c - >/dev/null 2>&1 || fail "Контрольная сумма архива XerahS не совпала."
    mkdir "$tmp/xerahs"
    tar -xzf "$tmp/xerahs.tar.gz" -C "$tmp/xerahs"
    if [ -n "$have" ]; then
        stop_xerahs
        mv "$APP" "$tmp/XerahS-old.app" 2>/dev/null || fail "Нет прав на запись в /Applications."
    fi
    if ! mv "$tmp/xerahs/XerahS.app" /Applications/ 2>/dev/null; then
        [ -z "$have" ] || mv "$tmp/XerahS-old.app" "$APP"
        fail "Нет прав на запись в /Applications."
    fi
    xattr -cr "$APP"   # XerahS не подписан: без этого macOS считает программу повреждённой
    if [ -n "$have" ]; then fresh=2; info "Обновлён до $XERAHS_VERSION."; else fresh=1; info "Установлен в $APP."; fi
fi

title "2/5. Плагин nc-share"
zip="${NCSHARE_ZIP:-}"
if [ -z "$zip" ]; then
    zip="$tmp/nc-share-xerahs.zip"
    curl -fsSL -o "$zip" "https://github.com/$REPO/releases/latest/download/nc-share-xerahs.zip" \
        || fail "Не удалось скачать плагин из релизов github.com/$REPO."
fi
mkdir "$tmp/plugin"
unzip -q "$zip" -d "$tmp/plugin" || fail "Архив плагина повреждён: $zip"
[ -f "$tmp/plugin/plugin.json" ] && [ -f "$tmp/plugin/NcShare.Plugin.dll" ] || fail "В архиве нет плагина nc-share: $zip"
version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$tmp/plugin/plugin.json")
info "nc-share $version"

if [ -z "$(workflows_file)" ]; then
    info "XerahS ещё не запускался. Запускаю, чтобы он создал свои настройки; мастер первого запуска можно закрыть…"
    open "$APP"
    for _ in $(seq 1 120); do [ -n "$(workflows_file)" ] && break; sleep 1; done
    [ -n "$(workflows_file)" ] || fail "XerahS не создал настройки за 2 минуты. Запусти его вручную и повтори установку."
    sleep 3   # пусть допишет остальные файлы
fi

title "3/5. Nextcloud"
instances="$SETTINGS/uploader-instances.json"
cur_server=$(jxa current-server "$instances")
cur_folder=$(jxa current-folder "$instances")
server="${NCSHARE_SERVER:-}"
folder=""
input="${NCSHARE_BASE_FOLDER:-}"
if [ -n "$input" ]; then
    folder=$(jxa folder "$input")
    [ -n "$folder" ] || fail "Не понял, какая это папка: «$input»."
    [ -n "$server" ] || server=$(jxa folder-server "$input")
else
    if [ -n "$cur_server" ] && [ -n "$cur_folder" ]; then
        info "Сейчас: папка $cur_folder на $cur_server"
    else
        info "Открой в браузере папку Nextcloud, куда класть снимки, и скопируй адрес из адресной строки."
        info "Или введи путь, например /Screenshots: папки, которых нет, создадутся при первой загрузке."
    fi
    warn "Внутри выбранной папки nc-share создаст папку screenshots/ и будет класть снимки туда."
    for _ in 1 2 3; do
        if [ -n "$cur_folder" ]; then ask "Enter — оставить, или вставь адрес другой папки: "; else ask "Адрес папки: "; fi
        if [ -z "$REPLY" ] && [ -n "$cur_folder" ]; then folder="$cur_folder"; break; fi
        folder=$(jxa folder "$REPLY")
        if [ -n "$folder" ]; then server="$(jxa folder-server "$REPLY")"; break; fi
        warn "Не понял. Нужен адрес вида https://cloud.example.com/apps/files/…?dir=/Screenshots или путь /Screenshots."
    done
    [ -n "$folder" ] || fail "Папка не задана."
fi
[ -n "$server" ] || server="$cur_server"
if [ -z "$server" ]; then
    ask "Адрес сервера Nextcloud, например https://cloud.example.com: "
    server="$REPLY"
fi
server=$(jxa server "$server")
[ -n "$server" ] || fail "Сервер Nextcloud не задан."

# status.php отвечает без входа: проверяем адрес и версию. Вход через браузер (Login Flow v2) — с Nextcloud 16
status=$(curl -fsS --max-time 15 "$server/status.php" 2>/dev/null || true)
if printf '%s' "$status" | grep -q '"installed": *true'; then
    nc_version=$(printf '%s' "$status" | sed -n 's/.*"versionstring": *"\([^"]*\)".*/\1/p')
    info "Сервер: $server, Nextcloud ${nc_version:-?}"
    [ "${nc_version%%.*}" -ge 16 ] 2>/dev/null || warn "Нужен Nextcloud 16 или новее: на старых версиях нет входа через браузер."
else
    warn "$server не отвечает как Nextcloud ($server/status.php). Проверь адрес, VPN и сеть."
    ask "Всё равно продолжить? [y/N] "
    case "$REPLY" in [yYдД]*) ;; *) fail "Сервер Nextcloud недоступен: $server" ;; esac
fi
info "Папка: $folder"

others=$(jxa foreign "$instances")
remove=0
if [ -n "$others" ]; then
    warn "В XerahS есть другие подключения для картинок и файлов:"
    printf '%s\n' "$others" | sed 's/^/    · /'
    info "Если в меню выбрать «Скопировать» или Esc, XerahS отправит снимок в них как в запасные."
    case "${NCSHARE_REMOVE_OTHER:-}" in
        1) remove=1 ;;
        0) ;;
        *) ask "Убрать их? [Y/n] "; case "$REPLY" in [nNнН]*) ;; *) remove=1 ;; esac ;;
    esac
fi

title "4/5. Настройка"
stop_xerahs
mkdir -p "$BACKUP"
cp -R "$SETTINGS" "$BACKUP/Settings"
defaults export com.apple.symbolichotkeys "$BACKUP/com.apple.symbolichotkeys.plist"
[ ! -d "$PLUGIN_DIR" ] || cp -R "$PLUGIN_DIR" "$BACKUP/ncshare-plugin"
info "Копия настроек: $BACKUP"

rm -rf "$PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR"
cp "$tmp/plugin/"* "$PLUGIN_DIR/"
info "плагин установлен в $PLUGIN_DIR"

jxa apply "$instances" "$(workflows_file)" "$SETTINGS/ApplicationConfig.json" "$server" "$folder" "$remove" | sed 's/^/  /'

# Системные Cmd+Shift+3/4 (28 и 30): пока включены, macOS перехватывает сочетания раньше XerahS.
# Ctrl+Cmd+Shift+3/4 (снимок в буфер) и панель Cmd+Shift+5 остаются системными
hotkey_off 28 51 20
hotkey_off 30 52 21
"$ACTIVATE" -u 2>/dev/null || true
still=$(jxa hotkeys-off 28 30)
[ -z "$still" ] && info "системные Cmd+Shift+3 и Cmd+Shift+4 выключены" \
    || warn "macOS не дала выключить сочетания $still: выключи вручную — Клавиатура → Сочетания клавиш → Снимки экрана."

title "5/5. Запуск XerahS"
log="$DATA/Logs/$(date +%Y-%m)/XerahS-$(date +%Y%m%d).log"
before=$([ -f "$log" ] && wc -c < "$log" || echo 0)
open "$APP"
loaded=""
for _ in $(seq 1 40); do
    [ -f "$log" ] && loaded=$(tail -c +$((before + 1)) "$log" | grep -a -o "Discovered plugin: nc-share v[0-9.]*" | tail -1 || true)
    [ -n "$loaded" ] && break
    sleep 0.5
done
if [ -n "$loaded" ]; then info "XerahS загрузил ${loaded#Discovered plugin: }"; else warn "Не вижу в логе, что XerahS загрузил плагин: $log"; fi

title "Готово"
info "Cmd+Shift+3 — весь экран, Cmd+Shift+4 — область, Ctrl+Shift+A — активное окно."
info "После снимка — редактор, затем меню: 1 — скопировать, 2 — ссылка для команды, 3–5 — публичная на 1, 7, 30 дней."
info "При первой загрузке откроется браузер: войди в Nextcloud и подтверди доступ."
if [ "$fresh" != 0 ]; then
    info ""
    if [ "$fresh" = 1 ]; then
        warn "Разреши XerahS запись экрана: в открывшихся настройках включи XerahS. macOS перезапустит программу."
    else
        # Подпись XerahS без сертификата разработчика, и разрешение macOS привязано к конкретной сборке
        warn "XerahS обновлён. Если на снимках только обои — в открывшихся настройках выключи и снова включи XerahS."
    fi
    open "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture" || true
fi
info ""
info "Вернуть как было:"
info "  pkill -x XerahS; cp -R \"$BACKUP/Settings/\" \"$SETTINGS/\"; rm -rf \"$PLUGIN_DIR\""
info "  defaults import com.apple.symbolichotkeys \"$BACKUP/com.apple.symbolichotkeys.plist\"; $ACTIVATE -u"
