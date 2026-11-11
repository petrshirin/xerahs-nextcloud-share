#!/bin/bash
# Установка nc-share на macOS одной командой:
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/petrshirin/xerahs-nextcloud-share/main/install-macos.sh)"
#
# Ставит XerahS (если его нет) и плагин nc-share из последнего релиза, отдаёт XerahS сочетания Cmd+Shift+3/4
# и настраивает сценарии: снимок → редактор → меню nc-share. Перед правкой настройки XerahS и сочетания macOS
# копируются в ~/Documents/XerahS/nc-share-backup/<дата>. Повторный запуск обновляет плагин; сервер и папки
# Nextcloud сохраняются, если их не поменять.
#
# --link-account — только один этап: учётка для публичных ссылок. Нужна, если публичные ссылки без пароля сервер
# разрешает только в одной папке и только отдельной учётке: файл туда загружает своя учётка, а ссылку создаёт эта.
# Спрашивает папку, логин и пароль приложения этой учётки, проверяет их на сервере; остальное не трогает:
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/petrshirin/xerahs-nextcloud-share/main/install-macos.sh)" - --link-account
#
# Переменные — для проверки и установки без вопросов:
#   NCSHARE_REPO          репозиторий GitHub «владелец/имя», из релизов которого берётся плагин
#   NCSHARE_ZIP           свой архив плагина вместо скачивания (собирает xerahs-plugin/pack.sh)
#   NCSHARE_BASE_FOLDER   папка команды в Nextcloud: адрес из браузера (заодно задаёт сервер) или путь /Папка.
#                         Если задана, папка для всех не спрашивается: остаётся прежней или берётся из переменной ниже
#   NCSHARE_EVERYONE_FOLDER  папка, доступная всем пользователям Nextcloud (пункт «Ссылка для всех»): адрес или путь;
#                         «-» — убрать пункт
#   NCSHARE_SERVER        адрес Nextcloud, если папка задана путём, например https://cloud.example.com
#   NCSHARE_REMOVE_OTHER  1 — убрать другие подключения для картинок и файлов без вопроса, 0 — оставить
# С --link-account:
#   NCSHARE_PUBLIC_FOLDER папка для публичных ссылок: адрес или путь; «-» — убрать учётку для ссылок
#   NCSHARE_LINK_LOGIN    логин учётки, которая создаёт ссылки
#   NCSHARE_LINK_PASSWORD её пароль приложения
set -euo pipefail

REPO="${NCSHARE_REPO:-petrshirin/xerahs-nextcloud-share}"
# XerahS, с которым проверен и собран плагин
XERAHS_VERSION=0.33.6
XERAHS_URL="https://github.com/ShareX/XerahS/releases/download/v$XERAHS_VERSION"
XERAHS_SHA256_arm64=c06ed75a8a51cc1b45a2dc861408c544a37e5876ce69f6e4fda5647907a654af
XERAHS_SHA256_x64=2d450078e3c51a870ff1cc414cee7c7b061a6b92cd49a4e7d1f3d72e3164dfe0

APP=/Applications/XerahS.app
DATA="$HOME/Documents/XerahS"
SETTINGS="$DATA/Settings"
PLUGIN_DIR="$DATA/Plugins/macos64/ncshare"
BACKUP="$DATA/nc-share-backup/$(date +%Y-%m-%d_%H%M%S)"
ACTIVATE=/System/Library/PrivateFrameworks/SystemAdministration.framework/Resources/activateSettings
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

title() { printf '\n\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
warn() { printf '  \033[33m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }
# Ответ читается с терминала: при запуске через curl | bash stdin занят самим скриптом
ask() { printf '  %s' "$1"; REPLY=""; { IFS= read -r REPLY < /dev/tty; } 2>/dev/null || echo; }
# То же без эха — для пароля
ask_secret() { printf '  %s' "$1"; REPLY=""; { IFS= read -rs REPLY < /dev/tty; } 2>/dev/null || true; echo; }

mode=install
for arg in "$@"; do
    case "$arg" in
        --link-account) mode=link ;;
        *) fail "Не знаю параметр «$arg». Есть только --link-account." ;;
    esac
done

# С этой версии плагин умеет создавать публичные ссылки отдельной учёткой
LINK_SINCE=0.8.0

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

# Сценарии XerahS: WorkflowsConfig-<имя компьютера>.json, а до первого запуска — WorkflowsConfig.json, который создал
# установщик (XerahS при запуске сам копирует его в файл с именем компьютера)
workflows_file() {
    { ls -t "$SETTINGS"/WorkflowsConfig-*.json 2>/dev/null || ls "$SETTINGS"/WorkflowsConfig.json 2>/dev/null; } | head -1 || true
}

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

// Текущая настройка подключения nc-share: ServerUrl, BaseFolder или EveryoneFolder
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
    return set.length ? set.join(", ") : "None";   // пустую строку XerahS заменит своими флагами по умолчанию
}

const hotkey = ([key, mods]) => ({ Key: key, Modifiers: mods, IsValid: true, IsOnlyModifiers: false,
                                   HasControl: mods.includes("Control"), HasAlt: mods.includes("Alt"),
                                   HasShift: mods.includes("Shift"), HasMeta: mods.includes("Meta") });
const noHotkey = () => ({ Key: "None", Modifiers: "None", IsValid: false, IsOnlyModifiers: false,
                          HasControl: false, HasAlt: false, HasShift: false, HasMeta: false });

// Первая установка: настроек XerahS ещё нет. Сам XerahS пишет их только после мастера первого запуска или при выходе
// через меню, поэтому заготовку создаёт установщик, а недостающее XerahS при запуске дополнит значениями по умолчанию.
// ApplicationVersion — признак, что первый запуск позади: мастер не появится
function seed(settingsDir, version) {
    const appPath = settingsDir + "/ApplicationConfig.json";
    if (!$.NSFileManager.defaultManager.fileExistsAtPath(appPath)) writeJson(appPath, { ApplicationVersion: version });
    const workflow = (job, name, task) => ({ Id: ObjC.unwrap($.NSUUID.UUID.UUIDString).toLowerCase(), HotkeyInfo: noHotkey(),
                                             Name: name, Enabled: true, PinnedToTray: false,
                                             TaskSettings: Object.assign({ Job: job, Description: name }, task) });
    // Только сценарии nc-share; первые три XerahS показывает в меню значка. Видео пишет системная панель Cmd+Shift+5
    writeJson(settingsDir + "/WorkflowsConfig.json", { Hotkeys: [
        workflow("PrintScreen", "Full screen capture"),
        workflow("ActiveWindow", "Active window capture"),
        workflow("RectangleRegion", "Region capture"),
        // Без копирования в буфер: буфером занимается плагин (у XerahS по умолчанию CopyImageToClipboard, SaveImageToFile)
        workflow("FileUpload", "File upload", { AfterCaptureJob: "SaveImageToFile" })
    ] });
    return "созданы настройки XerahS: весь экран, активное окно, область, загрузка файла";
}

function apply(instancesPath, workflowsPath, appPath, serverUrl, baseFolder, everyoneFolder, removeOthers) {
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
        // Остальные настройки (PathTemplate, PublicDays…) остаются как были, в том числе от прежних версий
        if (settings.ServerUrl !== serverUrl || settings.BaseFolder !== baseFolder || (settings.EveryoneFolder || "") !== everyoneFolder) {
            settings.ServerUrl = serverUrl;
            settings.BaseFolder = baseFolder;
            if (everyoneFolder) settings.EveryoneFolder = everyoneFolder; else delete settings.EveryoneFolder;
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
            h.PinnedToTray = true;   // в меню значка; иначе загрузка файла — только из главного окна XerahS
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

    // «Start minimized to tray»: на macOS XerahS тогда живёт только в строке меню — без окна при запуске и
    // без значка в Dock. Значок в строке меню нужен, иначе программу не видно совсем
    const app = readJson(appPath, null);
    if (app) {
        app.ShowTray = true;
        app.SilentRun = true;
        // Assistant (Ctrl+Shift+Space) к снимкам не относится, а сочетание занимает
        app.AssistantEnabled = false;
    }

    writeJson(workflowsPath, wf);
    writeJson(instancesPath, cfg);
    out.push("сценарии: весь экран, область, активное окно → редактор → nc-share");
    if (app) {
        writeJson(appPath, app);
        out.push("XerahS запускается свёрнутым: только значок в строке меню; Assistant выключен");
    } else out.push("ВНИМАНИЕ: нет " + appPath);
    return out.join("\n");
}

// --link-account: папка для публичных ссылок и признак, что их создаёт отдельная учётка, — в обоих подключениях
// nc-share. Пустая папка — убрать и то и другое. Остальные настройки не трогаются
function link(instancesPath, publicFolder) {
    const cfg = readJson(instancesPath, null);
    const ours = ((cfg && cfg.Instances) || []).filter(isOurs);
    if (!ours.length) throw new Error("нет подключений nc-share в " + instancesPath);
    const now = new Date().toISOString();
    for (const inst of ours) {
        let settings = {};
        try { settings = JSON.parse(inst.SettingsJson || "{}"); } catch (e) {}
        if (publicFolder) {
            settings.PublicFolder = publicFolder;
            settings.LinkAccount = true;
        } else {
            delete settings.PublicFolder;
            delete settings.LinkAccount;
        }
        inst.SettingsJson = JSON.stringify(settings);
        inst.ModifiedAt = now;
    }
    writeJson(instancesPath, cfg);
    return publicFolder ? "публичные ссылки — из " + publicFolder + ", создаёт их учётка для ссылок"
                        : "учётка для ссылок убрана: публичные ссылки — снова из папки команды";
}

// /Папка/Подпапка → путь для WebDAV, каждая часть закодирована
const davPath = path => path.split("/").filter(Boolean).map(encodeURIComponent).join("/");

// Ответ /ocs/v2.php/cloud/user: логин и имя
function ocsUser(path) {
    const d = (readJson(path, {}).ocs || {}).data || {};
    return d.id ? d.id + (d.displayname && d.displayname !== d.id ? " (" + d.displayname + ")" : "") : "";
}

// Правила публичных ссылок для вошедшей учётки: «обязателен пароль (1/0) предел срока в днях или пусто»
function linkRules(path) {
    const caps = ((readJson(path, {}).ocs || {}).data || {}).capabilities || {};
    const pub = (caps.files_sharing || {}).public || {};
    const exp = pub.expire_date || {};
    if (pub.enabled === false) return "off";
    return ((pub.password || {}).enforced ? "1" : "0") + " " + (exp.enforced && exp.days ? exp.days : "");
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
        case "current-everyone": return current(args[0], "EveryoneFolder");
        case "current": return String(current(args[0], args[1]));
        case "link": return link(args[0], args[1]);
        case "dav-path": return davPath(args[0]);
        case "ocs-user": return ocsUser(args[0]);
        case "link-rules": return linkRules(args[0]);
        case "foreign": return foreign(args[0]);
        case "seed": return seed(args[0], args[1]);
        case "apply": return apply(args[0], args[1], args[2], args[3], args[4], args[5], args[6] === "1");
        case "hotkeys-off": return hotkeysOff(args.map(Number));
    }
    throw new Error("неизвестная команда " + cmd);
}
JS
jxa() { osascript -l JavaScript "$tmp/configure.js" "$@"; }

# Запуск XerahS и проверка по логу, что плагин загрузился
start_xerahs() {
    local log before loaded=""
    log="$DATA/Logs/$(date +%Y-%m)/XerahS-$(date +%Y%m%d).log"
    before=$([ -f "$log" ] && wc -c < "$log" || echo 0)
    open "$APP"
    for _ in $(seq 1 40); do
        [ -f "$log" ] && loaded=$(tail -c +$((before + 1)) "$log" | grep -a -o "Discovered plugin: nc-share v[0-9.]*" | tail -1 || true)
        [ -n "$loaded" ] && break
        sleep 0.5
    done
    if [ -n "$loaded" ]; then info "XerahS загрузил ${loaded#Discovered plugin: }"; else warn "Не вижу в логе, что XerahS загрузил плагин: $log"; fi
}

# Секрет из Связки ключей — так же, как его читает XerahS: /usr/bin/security, сервис XerahS
secret() { security find-generic-password -a "$1" -s XerahS -w 2>/dev/null || true; }

# Запрос от имени учётки для ссылок. Пароль — в файле настроек curl, а не в командной строке: её видят все процессы
link_curl() { curl -sS --max-time 20 -K "$tmp/link.curl" -H "OCS-APIRequest: true" "$@" 2>/dev/null || true; }

# Адрес или путь папки для публичных ссылок → /Папка. Подпапку с логином nc-share добавляет сам, поэтому адрес из
# браузера вида /Папка/<логин>/screenshots/2026/11 обрезается до /Папка. Не подошло — пусто и предупреждение
public_folder() {
    local f s g
    f=$(jxa folder "$1")
    s=$(jxa folder-server "$1")
    if [ -z "$f" ]; then
        warn "Не понял. Нужен адрес вида https://cloud.example.com/apps/files/…?dir=/Public или путь /Public." >&2
    elif [ -n "$s" ] && [ "$(jxa server "$s")" != "$server" ]; then
        warn "Эта папка на другом сервере ($s), а nc-share работает с одним: $server." >&2
    else
        g="$f/"
        if [ -n "$own_login" ] && [ "${g%%/"$own_login"/*}" != "$g" ] && [ -n "${g%%/"$own_login"/*}" ]; then
            f="${g%%/"$own_login"/*}"
            info "Подпапку $own_login nc-share добавит сам — беру папку выше: $f" >&2
        fi
        printf '%s' "$f"
    fi
}

# --link-account: сервер разрешает публичные ссылки без пароля только отдельной учётке и только в своей папке.
# Файл туда загружает своя учётка, а ссылку создаёт эта. Её логин и пароль приложения ложатся в Связку ключей рядом
# со своим входом (ncshare:<ключ>-links:…), папка и признак LinkAccount — в подключение nc-share
link_account() {
    local instances="$SETTINGS/uploader-instances.json"
    local server have key own_login cur_public cur_login public="" login pass code who rules perms days
    server=$(jxa current "$instances" ServerUrl)
    have=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$PLUGIN_DIR/plugin.json" 2>/dev/null || true)
    [ -n "$server" ] && [ -n "$have" ] || fail "nc-share ещё не установлен. Сначала запусти установщик без --link-account."
    ! older "$have" "$LINK_SINCE" || fail "Установлен nc-share $have, учётку для ссылок он не знает — нужен $LINK_SINCE или новее. Сначала обнови его: запусти установщик без --link-account."
    server="${server%/}"
    key=$(jxa current "$instances" SecretKey)
    key="ncshare:${key:-default}"
    own_login=$(secret "$key:loginName")
    cur_public=$(jxa current "$instances" PublicFolder)
    cur_login=""
    [ "$(jxa current "$instances" LinkAccount)" != true ] || cur_login=$(secret "$key-links:loginName")
    days=$(jxa current "$instances" PublicDays)

    title "1/3. Папка для публичных ссылок"
    info "Для сервера, где публичные ссылки без пароля можно создавать только в одной папке и только отдельной учёткой."
    info "Файл туда загрузит твоя учётка, а ссылку создаст та. Остальные пункты меню не меняются."
    info "Внутри папки у каждого своя подпапка с логином: <папка>/${own_login:-<логин>}/screenshots/2026/11."
    [ -z "$cur_public" ] || info "Сейчас: $cur_public, ссылки создаёт ${cur_login:-учётка не задана}"
    if [ -n "${NCSHARE_PUBLIC_FOLDER:-}" ]; then
        if [ "$NCSHARE_PUBLIC_FOLDER" != "-" ]; then
            public=$(public_folder "$NCSHARE_PUBLIC_FOLDER")
            [ -n "$public" ] || fail "Не понял, какая это папка: «$NCSHARE_PUBLIC_FOLDER»."
        fi
    else
        for _ in 1 2 3; do
            if [ -n "$cur_public" ]; then ask "Enter — оставить, «-» — убрать, или адрес другой папки: "
            else ask "Открой папку в браузере и вставь адрес (Enter — выйти): "; fi
            if [ -z "$REPLY" ]; then
                [ -n "$cur_public" ] || { info "Ничего не менял."; return 0; }
                public="$cur_public"; break
            fi
            [ "$REPLY" != "-" ] || break
            public=$(public_folder "$REPLY")
            [ -z "$public" ] || break
        done
        [ -n "$public" ] || [ "$REPLY" = "-" ] || fail "Папка не задана."
    fi

    if [ -n "$public" ]; then
        title "2/3. Учётка для ссылок"
        info "Логин учётки, которой сервер разрешает публичные ссылки в этой папке, и её пароль приложения"
        info "(в Nextcloud под этой учёткой: «Настройки» → «Безопасность» → «Создать пароль приложения»)."
        login="${NCSHARE_LINK_LOGIN:-}"
        pass="${NCSHARE_LINK_PASSWORD:-}"
        if [ -z "$login" ]; then
            if [ -n "$cur_login" ]; then ask "Логин (Enter — $cur_login): "; login="${REPLY:-$cur_login}"
            else ask "Логин: "; login="$REPLY"; fi
        fi
        login=$(printf '%s' "$login" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
        [ -n "$login" ] || fail "Логин не задан."
        [ "$login" != "$own_login" ] || fail "Это твоя учётка — та, что загружает файлы. Нужна другая: та, которой сервер разрешает ссылки."
        if [ -z "$pass" ]; then
            if [ "$login" = "$cur_login" ]; then
                ask_secret "Пароль приложения (Enter — оставить прежний): "
                pass="${REPLY:-$(secret "$key-links:appPassword")}"
            else
                ask_secret "Пароль приложения (не отображается): "
                pass="$REPLY"
            fi
        fi
        pass="${pass//[[:space:]]/}"   # при вставке из браузера бывают пробелы по краям
        [ -n "$pass" ] || fail "Пароль приложения не задан."
        case "$login$pass" in *'"'* | *'\'*) fail "В логине или пароле есть кавычка или обратная косая черта — так сохранить не получится." ;; esac

        title "3/3. Проверка"
        (umask 077; printf 'user = "%s:%s"\n' "$login" "$pass" > "$tmp/link.curl")
        code=$(link_curl -o "$tmp/user.json" -w '%{http_code}' "$server/ocs/v2.php/cloud/user?format=json")
        case "$code" in
            200) who=$(jxa ocs-user "$tmp/user.json"); info "Вход: ${who:-$login}" ;;
            401) fail "Nextcloud не принял логин «$login» с этим паролем приложения." ;;
            000) fail "Нет связи с $server." ;;
            *) fail "Вход учётки для ссылок — HTTP $code." ;;
        esac
        link_curl -o "$tmp/caps.json" "$server/ocs/v2.php/cloud/capabilities?format=json" >/dev/null
        rules=$(jxa link-rules "$tmp/caps.json")
        case "$rules" in
            off) fail "Публичные ссылки для учётки $login на сервере выключены." ;;
            1*) warn "Для учётки $login пароль публичных ссылок тоже обязателен: ссылки будут с паролем." ;;
            *) info "Пароль для публичных ссылок не обязателен." ;;
        esac
        [ -z "${rules#* }" ] || info "Предел срока ссылки на сервере — ${rules#* } дн."
        code=$(link_curl -X PROPFIND -H "Depth: 0" -H "Content-Type: application/xml" -o "$tmp/folder.xml" -w '%{http_code}' \
            --data '<?xml version="1.0"?><d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns"><d:prop><oc:permissions/></d:prop></d:propfind>' \
            "$server/remote.php/webdav/$(jxa dav-path "$public")")
        case "$code" in
            207) perms=$(sed -n 's/.*<oc:permissions>\([A-Z]*\)<\/oc:permissions>.*/\1/p' "$tmp/folder.xml" | head -1)
                 case "$perms" in
                     *R*) info "Папка $public: учётка её видит, право делиться есть." ;;
                     *) warn "Папка $public: учётка её видит, но права делиться в ней нет (права: ${perms:-?})."
                        ask "Всё равно сохранить? [y/N] "
                        case "$REPLY" in [yYдД]*) ;; *) fail "Без права делиться учётка не создаст ссылку." ;; esac ;;
                 esac ;;
            404) fail "Учётка $login не видит папку $public. Ей нужен доступ к этой папке по тому же пути, с правом делиться." ;;
            000) fail "Нет связи с $server." ;;
            *) fail "Проверка папки $public — HTTP $code." ;;
        esac
    fi

    title "Настройка"
    stop_xerahs
    mkdir -p "$BACKUP"
    cp -R "$SETTINGS" "$BACKUP/Settings"
    info "Копия настроек: $BACKUP"
    jxa link "$instances" "$public" | sed 's/^/  /'
    if [ -n "$public" ]; then
        # security -i читает команды со stdin: пароль не попадает в командную строку
        printf 'add-generic-password -a "%s" -s "XerahS" -w "%s" -U\n' \
            "$key-links:loginName" "$login" "$key-links:appPassword" "$pass" "$key-links:server" "$server" | security -i >/dev/null 2>&1 || true
        [ "$(secret "$key-links:appPassword")" = "$pass" ] && [ "$(secret "$key-links:server")" = "$server" ] \
            || fail "Не удалось сохранить вход учётки $login в Связке ключей."
        info "вход учётки $login — в Связке ключей (XerahS, $key-links:…)"
    else
        printf 'delete-generic-password -a "%s" -s "XerahS"\n' "$key-links:loginName" "$key-links:appPassword" "$key-links:server" \
            | security -i >/dev/null 2>&1 || true
        info "вход учётки для ссылок удалён из Связки ключей"
    fi
    start_xerahs

    title "Готово"
    if [ -n "$public" ]; then
        info "Публичные ссылки (пункты на ${days:-1, 7, 30} дней): файл — в $public/${own_login:-<логин>}/…, ссылку создаёт $login."
        info "Убрать: запусти установщик с --link-account и введи «-»."
    fi
    info "Вернуть настройки как были: pkill -x XerahS; cp -R \"$BACKUP/Settings/\" \"$SETTINGS/\""
}

[ "$(uname -s)" = Darwin ] || fail "Этот установщик — для macOS."
# Под Rosetta uname -m говорит x86_64 и на Apple Silicon, поэтому смотрим на процессор
if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then arch=arm64; else arch=x64; fi

if [ "$mode" = link ]; then
    link_account
    exit 0
fi

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
    # Подпись XerahS без сертификата разработчика, и macOS помнит разрешение на запись экрана для прежней сборки:
    # переключатель в настройках включён, а новой сборке снимать нельзя. Сброс — и при первом снимке macOS спросит снова.
    # tccutil ищет программу по bundle id через LaunchServices, поэтому сначала регистрируем новую сборку
    "$LSREGISTER" -f "$APP" >/dev/null 2>&1 || true
    tccutil reset ScreenCapture com.xerahs.app >/dev/null 2>&1 || warn "Не удалось сбросить разрешение на запись экрана (tccutil)."
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
    info "Папка команды — для ссылки «для команды» (её откроют те, у кого есть доступ к папке) и публичных ссылок."
    if [ -n "$cur_server" ] && [ -n "$cur_folder" ]; then
        info "Сейчас: $cur_folder на $cur_server"
    else
        info "Открой эту папку в браузере и скопируй адрес из адресной строки."
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
# Папка для всех — пункт «Ссылка для всех». Необязательна: без неё меню прежнее
cur_everyone=$(jxa current-everyone "$instances")
everyone="$cur_everyone"
if [ -n "${NCSHARE_EVERYONE_FOLDER:-}" ]; then
    if [ "$NCSHARE_EVERYONE_FOLDER" = "-" ]; then everyone=""
    else
        everyone=$(jxa folder "$NCSHARE_EVERYONE_FOLDER")
        [ -n "$everyone" ] || fail "Не понял, какая это папка: «$NCSHARE_EVERYONE_FOLDER»."
    fi
elif [ -z "${NCSHARE_BASE_FOLDER:-}" ]; then
    echo
    info "Папка для всех — общая папка, которую видят все пользователи Nextcloud: пункт меню «Ссылка для всех»."
    info "Внутри nc-share заведёт подпапку с твоим логином. Если такой папки на сервере нет — пропусти."
    [ -z "$cur_everyone" ] || info "Сейчас: $cur_everyone"
    for _ in 1 2 3; do
        if [ -n "$cur_everyone" ]; then ask "Enter — оставить, «-» — убрать, или адрес другой папки: "
        else ask "Адрес папки для всех (Enter — пропустить): "; fi
        [ -n "$REPLY" ] || break
        if [ "$REPLY" = "-" ]; then everyone=""; break; fi
        candidate=$(jxa folder "$REPLY")
        ev_server=$(jxa folder-server "$REPLY")
        if [ -z "$candidate" ]; then
            warn "Не понял. Нужен адрес вида https://cloud.example.com/apps/files/…?dir=/Shared или путь /Shared."
        elif [ -n "$ev_server" ] && [ "$(jxa server "$ev_server")" != "$server" ]; then
            warn "Эта папка на другом сервере ($ev_server), а nc-share работает с одним: $server."
        elif [ "$candidate" = "$folder" ]; then
            warn "Это папка команды. Нужна другая — та, что доступна всем."
        else
            everyone="$candidate"
            break
        fi
    done
fi
echo
info "Папка команды: $folder"
if [ -n "$everyone" ]; then info "Папка для всех: $everyone"
else info "Папка для всех не задана — пункта «Ссылка для всех» в меню не будет."; fi

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
[ ! -d "$SETTINGS" ] || cp -R "$SETTINGS" "$BACKUP/Settings"
defaults export com.apple.symbolichotkeys "$BACKUP/com.apple.symbolichotkeys.plist"
[ ! -d "$PLUGIN_DIR" ] || cp -R "$PLUGIN_DIR" "$BACKUP/ncshare-plugin"
info "Копия настроек: $BACKUP"

rm -rf "$PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR"
cp "$tmp/plugin/"* "$PLUGIN_DIR/"
info "плагин установлен в $PLUGIN_DIR"

if [ -z "$(workflows_file)" ]; then
    mkdir -p "$SETTINGS"
    jxa seed "$SETTINGS" "$XERAHS_VERSION" | sed 's/^/  /'
fi
jxa apply "$instances" "$(workflows_file)" "$SETTINGS/ApplicationConfig.json" "$server" "$folder" "$everyone" "$remove" | sed 's/^/  /'

# Системные Cmd+Shift+3/4 (28 и 30): пока включены, macOS перехватывает сочетания раньше XerahS.
# Ctrl+Cmd+Shift+3/4 (снимок в буфер) и панель Cmd+Shift+5 остаются системными
hotkey_off 28 51 20
hotkey_off 30 52 21
"$ACTIVATE" -u 2>/dev/null || true
still=$(jxa hotkeys-off 28 30)
[ -z "$still" ] && info "системные Cmd+Shift+3 и Cmd+Shift+4 выключены" \
    || warn "macOS не дала выключить сочетания $still: выключи вручную — Клавиатура → Сочетания клавиш → Снимки экрана."

title "5/5. Запуск XerahS"
start_xerahs

title "Готово"
info "Cmd+Shift+3 — весь экран, Cmd+Shift+4 — область, Ctrl+Shift+A — активное окно."
if [ -n "$everyone" ]; then
    info "После снимка — редактор, затем меню: 1 — скопировать, 2 — ссылка для всех, 3 — ссылка для команды,"
    info "4–6 — публичная на 1, 7, 30 дней."
else
    info "После снимка — редактор, затем меню: 1 — скопировать, 2 — ссылка для команды, 3–5 — публичная на 1, 7, 30 дней."
fi
info "При первой загрузке откроется браузер: войди в Nextcloud и подтверди доступ."
if [ "$fresh" != 0 ]; then
    info ""
    # XerahS просит разрешение сам, при первом снимке, — до этого его нет в списке настроек
    warn "Сделай первый снимок (Cmd+Shift+3): macOS спросит разрешение на запись экрана."
    warn "Включи XerahS в открывшихся настройках и согласись перезапустить программу."
fi
info "Нет значка XerahS в строке меню? Если стоит менеджер строки меню (Bartender, Ice, Thaw), перезапусти его."
info ""
info "Вернуть как было:"
if [ -d "$BACKUP/Settings" ]; then
    info "  pkill -x XerahS; cp -R \"$BACKUP/Settings/\" \"$SETTINGS/\"; rm -rf \"$PLUGIN_DIR\""
else   # настроек XerahS до установки не было — их создал установщик
    info "  pkill -x XerahS; rm -rf \"$SETTINGS\" \"$PLUGIN_DIR\""
fi
info "  defaults import com.apple.symbolichotkeys \"$BACKUP/com.apple.symbolichotkeys.plist\"; $ACTIVATE -u"
