# Установка nc-share для ShareX на Windows одной командой — в PowerShell:
#   irm https://raw.githubusercontent.com/petrshirin/xerahs-nextcloud-share/main/install-windows.ps1 | iex
#
# Ставит ShareX (если его нет), собирает nc-share.exe компилятором C# из Windows и настраивает ShareX: снимок →
# редактор → меню nc-share, запись видео → меню nc-share. Перед правкой настройки ShareX копируются в
# <папка ShareX>\nc-share-backup\<дата>. Повторный запуск обновляет nc-share; сервер и папка Nextcloud сохраняются,
# если их не поменять.
#
# Файл — без BOM: irm | iex выполняет его как текст, и BOM в начале стал бы ошибкой. Запуск из файла в Windows
# PowerShell 5.1 прочитает его в кодировке ANSI — для этого: iex (Get-Content -Raw -Encoding UTF8 install-windows.ps1)
#
# Переменные — для проверки и установки без вопросов:
#   NCSHARE_REPO         репозиторий GitHub «владелец/имя», откуда берутся исходники nc-share
#   NCSHARE_REF          ветка или тег в нём, по умолчанию main
#   NCSHARE_SRC          своя папка с nc-share.cs и nc-share.ini вместо скачивания
#   NCSHARE_BASE_FOLDER  папка в Nextcloud: адрес из браузера (заодно задаёт сервер) или путь /Папка
#   NCSHARE_SERVER       адрес Nextcloud, если папка задана путём, например https://cloud.example.com
#   NCSHARE_SHAREX_DIR   папка настроек ShareX, если не находится сама (обычно Документы\ShareX)
#   NCSHARE_TEST         1 — проверка на копии настроек: ShareX не ставится, не закрывается и не запускается,
#                        сочетание PrintScreen в Windows не меняется
& {
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # без полосы прогресса Invoke-WebRequest в Windows PowerShell качает в разы быстрее

$Repo = if ($env:NCSHARE_REPO) { $env:NCSHARE_REPO } else { 'petrshirin/xerahs-nextcloud-share' }
$Ref = if ($env:NCSHARE_REF) { $env:NCSHARE_REF } else { 'main' }
$Test = $env:NCSHARE_TEST -eq '1'
# ShareX из Microsoft Store: имя пакета и приложение в нём
$StorePackage = '19568ShareX.ShareX'
$StoreApp = 'shell:AppsFolder\19568ShareX.ShareX_egrzcvs15399j!ShareX'
# ShareX, на котором проверен nc-share
$TestedShareX = 21

# Сценарии снимков и записи, которые ведут в меню nc-share
$ImageJobs = 'PrintScreen', 'ActiveWindow', 'CustomWindow', 'ActiveMonitor', 'RectangleRegion', 'RectangleLight',
    'RectangleTransparent', 'CustomRegion', 'LastRegion'
$VideoJobs = 'ScreenRecorder', 'ScreenRecorderActiveWindow', 'ScreenRecorderCustomRegion', 'StartScreenRecorder',
    'ScreenRecorderGIF', 'ScreenRecorderGIFActiveWindow', 'ScreenRecorderGIFCustomRegion', 'StartScreenRecorderGIF'
$JobNames = @{
    PrintScreen = 'весь экран'; ActiveWindow = 'активное окно'; CustomWindow = 'выбранное окно'; ActiveMonitor = 'активный монитор'
    RectangleRegion = 'область'; RectangleLight = 'область'; RectangleTransparent = 'область'; CustomRegion = 'заданная область'
    LastRegion = 'последняя область'; ScreenRecorder = 'запись видео'; ScreenRecorderActiveWindow = 'запись видео окна'
    ScreenRecorderCustomRegion = 'запись видео заданной области'; StartScreenRecorder = 'запись видео последней области'
    ScreenRecorderGIF = 'запись GIF'; ScreenRecorderGIFActiveWindow = 'запись GIF окна'
    ScreenRecorderGIFCustomRegion = 'запись GIF заданной области'; StartScreenRecorderGIF = 'запись GIF последней области'
}
# После снимка: редактор, сохранение в файл (его получает nc-share) и действие nc-share
$NeededSteps = 'AnnotateImage', 'SaveImageToFile', 'PerformActions'
# Мешают меню: копируют в буфер поверх ссылки nc-share, загружают на другой хостинг, показывают свои окна до меню.
# «Удалить файл» убрал бы и записи видео — после Esc в меню запись пропала бы
$ConflictingSteps = 'CopyImageToClipboard', 'UploadImageToHost', 'CopyFileToClipboard', 'CopyFilePathToClipboard',
    'CopyFolderPathToClipboard', 'ShowQuickTaskMenu', 'ShowAfterCaptureWindow', 'ShowBeforeUploadWindow', 'DeleteFile'

function Title($text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }
function Info($text) { Write-Host "  $text" }
function Warn($text) { Write-Host "  $text" -ForegroundColor Yellow }
function Fail($text) { throw $text }
function Ask($prompt) { Write-Host "  $prompt" -NoNewline; return Read-Host }

# --- ShareX: где стоит и где его настройки ---

function Find-ShareX {
    $pkg = $null
    try { $pkg = Get-AppxPackage -Name $StorePackage -ErrorAction SilentlyContinue } catch { }
    if ($pkg) { return [pscustomobject]@{ Store = $true; Exe = $null; Version = [string]$pkg.Version } }
    $candidates = @(Get-Process ShareX -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Where-Object { $_ })
    $uninstall = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    foreach ($app in Get-ItemProperty $uninstall -ErrorAction SilentlyContinue) {
        if ($app.DisplayName -like 'ShareX*' -and $app.InstallLocation) { $candidates += Join-Path $app.InstallLocation 'ShareX.exe' }
    }
    $candidates += Join-Path $env:ProgramFiles 'ShareX\ShareX.exe'
    foreach ($exe in $candidates) {
        if (Test-Path $exe) { return [pscustomobject]@{ Store = $false; Exe = $exe; Version = (Get-Item $exe).VersionInfo.ProductVersion } }
    }
    return $null
}

# Как у ShareX: портативная версия, путь из реестра или PersonalPath.cfg, иначе Документы\ShareX
function Get-ShareXFolder($sharex) {
    if ($env:NCSHARE_SHAREX_DIR) { return $env:NCSHARE_SHAREX_DIR }
    $docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ShareX'
    $exeDir = if ($sharex.Exe) { Split-Path $sharex.Exe } else { $null }
    if ($exeDir -and (Test-Path (Join-Path $exeDir 'Portable'))) { return Join-Path $exeDir 'ShareX' }
    foreach ($hive in 'HKLM:', 'HKCU:') {
        $path = (Get-ItemProperty "$hive\SOFTWARE\ShareX" -Name PersonalPath -ErrorAction SilentlyContinue).PersonalPath
        if ($path) { return [Environment]::ExpandEnvironmentVariables($path) }
    }
    $configs = @((Join-Path $docs 'PersonalPath.cfg'))
    if ($exeDir) { $configs = @((Join-Path $exeDir 'PersonalPath.cfg')) + $configs }
    foreach ($cfg in $configs) {
        if (-not (Test-Path $cfg)) { continue }
        $path = "$(Get-Content $cfg -TotalCount 1)".Trim()
        if (-not $path) { continue }
        if ([IO.Path]::IsPathRooted($path) -or -not $exeDir) { return [Environment]::ExpandEnvironmentVariables($path) }
        return Join-Path $exeDir $path
    }
    return $docs
}

function Start-ShareX([switch]$Silent) {
    if ($sharex.Store) { Start-Process $StoreApp }
    elseif ($Silent) { Start-Process $sharex.Exe -ArgumentList '-silent' }
    else { Start-Process $sharex.Exe }
}

# ShareX сохраняет настройки, когда их меняют, поэтому его можно просто завершить: правки на диске не пропадут
function Stop-ShareX {
    $procs = @(Get-Process ShareX, nc-share -ErrorAction SilentlyContinue)
    if (-not $procs) { return $false }
    $procs | Stop-Process -Force
    foreach ($p in $procs) { [void]$p.WaitForExit(10000) }
    return $true
}

# ShareX создаёт настройки, только когда сохраняет их: при первом запуске — когда его окно закрывают в трей
function Initialize-ShareXConfig {
    Info 'ShareX ещё не запускался. Запускаю, чтобы он создал свои настройки…'
    Start-ShareX
    $deadline = (Get-Date).AddSeconds(90)
    $window = $null
    while (-not $window -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $window = Get-Process ShareX -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if ($window) {
        Start-Sleep -Seconds 2
        [void]$window.CloseMainWindow()
    }
    while (-not ((Test-Path $appConfig) -and (Test-Path $hotkeysConfig)) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2   # пусть допишет файлы
    [void](Stop-ShareX)
    if (-not ((Test-Path $appConfig) -and (Test-Path $hotkeysConfig))) {
        Fail 'ShareX не создал настройки. Запусти его, закрой окно (он свернётся в трей) и повтори установку.'
    }
}

# --- Nextcloud: адрес папки из браузера ---

# https://cloud.example.com[/подкаталог][/index.php]/apps/files/…?dir=/Папка
$FilesUrl = '^(https?://[^?#]*?)(?:/index\.php)?/apps/files\b[^?#]*\?(?:[^#]*&)?dir=([^&#]*)'

# Адрес папки или сам путь → /Папка; корень не годится
function ConvertTo-Folder($text) {
    $s = "$text".Trim()
    if ($s -match $FilesUrl) { $s = [Uri]::UnescapeDataString(($Matches[2] -replace '\+', '%20')) }
    elseif ($s -match '^[a-z]+:') { return '' }
    $s = $s.TrimEnd('/')
    if (-not $s) { return '' }
    if (-not $s.StartsWith('/')) { $s = '/' + $s }
    return $s
}

# Сервер из адреса папки; у простого пути сервера нет
function Get-FolderServer($text) {
    if ("$text".Trim() -match $FilesUrl) { return $Matches[1].TrimEnd('/') }
    return ''
}

# cloud.example.com, https://cloud.example.com/index.php/apps/files/… → https://cloud.example.com
function ConvertTo-Server($text) {
    $s = "$text".Trim()
    if (-not $s) { return '' }
    if ($s -notmatch '^https?://') { $s = 'https://' + $s }
    $s = $s -replace '[?#].*$', '' -replace '(/index\.php)?/apps/.*$', ''
    return $s.TrimEnd('/')
}

# --- nc-share.ini: «Ключ = значение», комментарии — ; ---

function Read-Ini($path) {
    $values = @{}
    if (-not (Test-Path $path)) { return $values }
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^\s*([^;#=\s][^=]*?)\s*=(.*)$') { $values[$Matches[1].ToLowerInvariant()] = $Matches[2].Trim() }
    }
    return $values
}

# Новый шаблон с комментариями, значения — прежние там, где они были
function Write-Ini($template, $path, $values) {
    $lines = foreach ($line in [IO.File]::ReadAllLines($template)) {
        if ($line -match '^\s*([^;#=\s][^=]*?)\s*=' -and $values.ContainsKey($Matches[1].ToLowerInvariant())) {
            $value = $values[$Matches[1].ToLowerInvariant()]
            if ($value) { "$($Matches[1]) = $value" } else { "$($Matches[1]) =" }
        }
        else { $line }
    }
    [IO.File]::WriteAllLines($path, [string[]]$lines, (New-Object Text.UTF8Encoding $true))
}

# --- Настройки ShareX: JSON ---

function Read-Json($path) { return [IO.File]::ReadAllText($path) | ConvertFrom-Json }

# Если JSON окажется битым, ShareX молча возьмёт настройки из своей резервной копии — проверяем сразу
function Write-Json($path, $value) {
    $json = ConvertTo-Json -InputObject $value -Depth 100
    [void]($json | ConvertFrom-Json)
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding $false))
}

function Set-Property($object, $name, $value) {
    if ($object.PSObject.Properties[$name]) { $object.$name = $value }
    else { $object | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

# Флаги вида "A, B, C": добавить нужные, убрать лишние, остальное не трогать
function Set-Flags($value, $add, $remove) {
    $set = New-Object System.Collections.Generic.List[string]
    foreach ($flag in "$value".Split(',')) {
        $flag = $flag.Trim()
        if ($flag -and $flag -ne 'None' -and $remove -notcontains $flag -and -not $set.Contains($flag)) { $set.Add($flag) }
    }
    foreach ($flag in $add) { if (-not $set.Contains($flag)) { $set.Add($flag) } }
    if ($set.Count -eq 0) { return 'None' }
    return $set -join ', '
}

function Test-NcShareAction($action) { return $action -and "$($action.Path)" -like '*\nc-share.exe' }

# Настройки задачи: общие (isDefault) или переопределённые у сочетания. Возвращает, что поменялось
function Update-TaskSettings($ts, $isDefault, $exe) {
    $changes = @()
    if ($isDefault -or -not $ts.UseDefaultAfterCaptureJob) {
        $before = @("$($ts.AfterCaptureJob)".Split(',') | ForEach-Object { $_.Trim() })
        $removed = @($ConflictingSteps | Where-Object { $before -contains $_ })
        if ($removed) { $changes += 'убрано после захвата: ' + ($removed -join ', ') }
        Set-Property $ts 'AfterCaptureJob' (Set-Flags $ts.AfterCaptureJob $NeededSteps $ConflictingSteps)
    }
    if ($isDefault -or -not $ts.UseDefaultActions) {
        # Действие nc-share — последним: оно получает файл после остальных действий
        $actions = New-Object System.Collections.ArrayList
        foreach ($action in @($ts.ExternalPrograms)) { if ($action -and -not (Test-NcShareAction $action)) { [void]$actions.Add($action) } }
        [void]$actions.Add([pscustomobject][ordered]@{
            IsActive = $true; Name = 'nc-share'; Path = $exe; Args = '%input'; OutputExtension = ''; Extensions = ''
            HiddenWindow = $true; DeleteInputFile = $false
        })
        Set-Property $ts 'ExternalPrograms' $actions.ToArray()
    }
    # Своё уведомление и звук ShareX не нужны: результат показывает уведомление nc-share
    if (($isDefault -or -not $ts.UseDefaultGeneralSettings) -and $ts.GeneralSettings) {
        Set-Property $ts.GeneralSettings 'ShowToastNotificationAfterTaskCompleted' $false
        Set-Property $ts.GeneralSettings 'PlaySoundAfterUpload' $false
    }
    return $changes
}

# "PrintScreen, Shift, Control" → Ctrl+Shift+PrintScreen
function Format-Hotkey($info) {
    $parts = @("$($info.Hotkey)".Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne 'None' })
    $keys = @()
    if ($info.Win) { $keys += 'Win' }
    foreach ($mod in 'Control', 'Shift', 'Alt') { if ($parts -contains $mod) { $keys += @{ Control = 'Ctrl'; Shift = 'Shift'; Alt = 'Alt' }[$mod] } }
    $key = $parts | Where-Object { 'Control', 'Shift', 'Alt' -notcontains $_ } | Select-Object -First 1
    if (-not $key) { return '' }
    if ($key -match '^D(\d)$') { $key = $Matches[1] }
    return ($keys + $key) -join '+'
}

function Read-LogTail($path, $offset) {
    if (-not (Test-Path $path)) { return '' }
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        if ($offset -gt $stream.Length) { $offset = 0 }
        [void]$stream.Seek($offset, 'Begin')
        return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd()
    }
    finally { $stream.Dispose() }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('nc-share-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { Fail 'Этот установщик — для Windows.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory $tmp | Out-Null

    Title '1/5. ShareX'
    $sharex = Find-ShareX
    if (-not $sharex -and -not $Test) {
        Info 'ShareX не найден — ставлю. Windows может спросить разрешение на установку.'
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            $ErrorActionPreference = 'Continue'
            & winget install --id ShareX.ShareX --exact --source winget --silent --accept-package-agreements --accept-source-agreements
            $ErrorActionPreference = 'Stop'
        }
        else {
            # Без winget — установщик с GitHub
            $release = Invoke-RestMethod 'https://api.github.com/repos/ShareX/ShareX/releases/latest' -UseBasicParsing
            $asset = $release.assets | Where-Object { $_.name -like '*-setup-x64.exe' -or $_.name -like '*-setup.exe' } | Select-Object -First 1
            if (-not $asset) { Fail 'Не нашёл установщик ShareX в github.com/ShareX/ShareX/releases.' }
            Info "Скачиваю $($asset.name)…"
            $setup = Join-Path $tmp $asset.name
            Invoke-WebRequest $asset.browser_download_url -OutFile $setup -UseBasicParsing
            Start-Process $setup -ArgumentList '/VERYSILENT', '/NORUN', '/NORESTART' -Verb RunAs -Wait
        }
        $sharex = Find-ShareX
        if (-not $sharex) { Fail 'ShareX не установился. Поставь его с https://getsharex.com и запусти установку ещё раз.' }
    }
    if ($sharex) {
        Info ("ShareX $($sharex.Version)" + $(if ($sharex.Store) { ' из Microsoft Store' } else { ": $($sharex.Exe)" }))
        $major = 0
        [void][int]::TryParse(("$($sharex.Version)" -split '\.')[0], [ref]$major)
        if ($major -lt $TestedShareX) { Warn "nc-share проверен с ShareX $TestedShareX. Если что-то не работает — обнови ShareX." }
    }
    elseif (-not $env:NCSHARE_SHAREX_DIR) { Fail 'ShareX не найден. Для проверки без ShareX задай NCSHARE_SHAREX_DIR.' }

    $data = Get-ShareXFolder $sharex
    $appConfig = Join-Path $data 'ApplicationConfig.json'
    $hotkeysConfig = Join-Path $data 'HotkeysConfig.json'
    $tools = Join-Path $data 'Tools\nc-share'
    $exe = Join-Path $tools 'nc-share.exe'
    Info "Настройки ShareX: $data"

    Title '2/5. nc-share'
    $src = Join-Path $tmp 'src'
    New-Item -ItemType Directory $src | Out-Null
    foreach ($name in 'nc-share.cs', 'nc-share.ini') {
        if ($env:NCSHARE_SRC) { Copy-Item (Join-Path $env:NCSHARE_SRC $name) $src }
        else {
            try { Invoke-WebRequest "https://raw.githubusercontent.com/$Repo/$Ref/nc-share/$name" -OutFile (Join-Path $src $name) -UseBasicParsing }
            catch { Fail "Не удалось скачать $name из github.com/$Repo ($Ref): $($_.Exception.Message)" }
        }
    }
    # Компилятор C# из .NET Framework 4 есть в любой Windows 10 и 11; собранный здесь exe Windows не считает скачанным
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path $csc)) { Fail "Нет компилятора C# из .NET Framework 4: $csc" }
    $built = Join-Path $tmp 'nc-share.exe'
    $ErrorActionPreference = 'Continue'
    $output = & $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$built" /r:System.Windows.Forms.dll /r:System.Drawing.dll `
        /r:System.Security.dll /r:System.Web.Extensions.dll (Join-Path $src 'nc-share.cs') 2>&1
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Warn "$_" }
        Fail 'Сборка nc-share не удалась.'
    }
    Info 'собран nc-share.exe'

    Title '3/5. Nextcloud'
    $ini = Read-Ini (Join-Path $tools 'nc-share.ini')
    $curServer = ConvertTo-Server $ini['server']
    $curFolder = ConvertTo-Folder $ini['basefolder']
    $server = $env:NCSHARE_SERVER
    $folder = ''
    if ($env:NCSHARE_BASE_FOLDER) {
        $folder = ConvertTo-Folder $env:NCSHARE_BASE_FOLDER
        if (-not $folder) { Fail "Не понял, какая это папка: «$env:NCSHARE_BASE_FOLDER»." }
        if (-not $server) { $server = Get-FolderServer $env:NCSHARE_BASE_FOLDER }
    }
    else {
        if ($curServer -and $curFolder) { Info "Сейчас: папка $curFolder на $curServer" }
        else {
            Info 'Открой в браузере папку Nextcloud, куда класть снимки, и скопируй адрес из адресной строки.'
            Info 'Или введи путь, например /Screenshots: папки, которых нет, создадутся при первой загрузке.'
        }
        Warn 'Внутри выбранной папки nc-share создаст папки screenshots\ и videos\ и будет класть файлы туда.'
        for ($i = 0; $i -lt 3 -and -not $folder; $i++) {
            $reply = if ($curFolder) { Ask 'Enter — оставить, или вставь адрес другой папки: ' } else { Ask 'Адрес папки: ' }
            if (-not $reply -and $curFolder) { $folder = $curFolder; break }
            $folder = ConvertTo-Folder $reply
            if ($folder) { $server = Get-FolderServer $reply }
            else { Warn 'Не понял. Нужен адрес вида https://cloud.example.com/apps/files/…?dir=/Screenshots или путь /Screenshots.' }
        }
        if (-not $folder) { Fail 'Папка не задана.' }
    }
    if (-not $server) { $server = $curServer }
    if (-not $server) { $server = Ask 'Адрес сервера Nextcloud, например https://cloud.example.com: ' }
    $server = ConvertTo-Server $server
    if (-not $server) { Fail 'Сервер Nextcloud не задан.' }

    # status.php отвечает без входа: проверяем адрес и версию. Вход через браузер (Login Flow v2) — с Nextcloud 16
    $status = $null
    try { $status = Invoke-RestMethod "$server/status.php" -TimeoutSec 15 -UseBasicParsing } catch { }
    if ($status -and $status.installed) {
        Info "Сервер: $server, Nextcloud $($status.versionstring)"
        $ncMajor = 0
        [void][int]::TryParse(("$($status.versionstring)" -split '\.')[0], [ref]$ncMajor)
        if ($ncMajor -lt 16) { Warn 'Нужен Nextcloud 16 или новее: на старых версиях нет входа через браузер.' }
    }
    else {
        Warn "$server не отвечает как Nextcloud ($server/status.php). Проверь адрес, VPN и сеть."
        if ((Ask 'Всё равно продолжить? [y/N] ') -notmatch '^[yYдД]') { Fail "Сервер Nextcloud недоступен: $server" }
    }
    Info "Папка: $folder"

    Title '4/5. Настройка'
    if (-not $Test -and (Stop-ShareX)) { Info 'ShareX закрыт на время настройки' }
    if (-not ((Test-Path $appConfig) -and (Test-Path $hotkeysConfig))) {
        if ($Test) { Fail "Нет настроек ShareX в $data" }
        Initialize-ShareXConfig
    }

    $backup = Join-Path $data ('nc-share-backup\' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
    New-Item -ItemType Directory $backup -Force | Out-Null
    Copy-Item $appConfig, $hotkeysConfig $backup
    if (Test-Path $tools) { Copy-Item $tools (Join-Path $backup 'nc-share') -Recurse }
    Info "Копия настроек: $backup"

    New-Item -ItemType Directory $tools -Force | Out-Null
    Copy-Item $built $exe -Force
    # От ручной установки прежних версий: исходники и сборка теперь у установщика
    foreach ($old in 'install.ps1', 'nc-share.cs') { Remove-Item (Join-Path $tools $old) -ErrorAction SilentlyContinue }
    $ini['server'] = $server
    $ini['basefolder'] = $folder
    Write-Ini (Join-Path $src 'nc-share.ini') (Join-Path $tools 'nc-share.ini') $ini
    $ini = Read-Ini (Join-Path $tools 'nc-share.ini')
    Info "nc-share установлен в $tools"

    $app = Read-Json $appConfig
    $hotkeys = Read-Json $hotkeysConfig
    if (-not $app.DefaultTaskSettings -or -not $hotkeys.Hotkeys) { Fail "Не понял формат настроек ShareX в $data" }
    foreach ($change in Update-TaskSettings $app.DefaultTaskSettings $true $exe) { Info "настройки задач: $change" }
    $ours = @()
    foreach ($h in $hotkeys.Hotkeys) {
        $ts = $h.TaskSettings
        if (-not $ts -or ($ImageJobs + $VideoJobs) -notcontains $ts.Job) { continue }
        foreach ($change in Update-TaskSettings $ts $false $exe) { Info "сочетание «$($JobNames[$ts.Job])»: $change" }
        $keys = Format-Hotkey $h.HotkeyInfo
        if ($keys) { $ours += [pscustomobject]@{ Keys = $keys; Job = $ts.Job; Video = $VideoJobs -contains $ts.Job } }
    }
    Write-Json $appConfig $app
    Write-Json $hotkeysConfig $hotkeys
    Info 'после снимка: редактор → nc-share; после записи видео: nc-share'
    Info 'уведомление и звук ShareX после задачи выключены — результат показывает nc-share'
    if (-not $ours) { Warn 'В ShareX нет сочетаний для снимков и записи — назначь их: ShareX → «Горячие клавиши…».' }

    # Windows 11 по умолчанию отдаёт PrintScreen «Ножницам», и ShareX не может занять клавишу
    $keyboard = 'HKCU:\Control Panel\Keyboard'
    $snipping = (Get-ItemProperty $keyboard -Name PrintScreenKeyForSnippingEnabled -ErrorAction SilentlyContinue).PrintScreenKeyForSnippingEnabled
    $win11 = [Environment]::OSVersion.Version.Build -ge 22000
    $prtsc = @($ours | Where-Object { $_.Keys -like '*PrintScreen' }).Count -gt 0
    $snippingOff = $false
    if (-not $Test -and $prtsc -and ($snipping -eq 1 -or ($null -eq $snipping -and $win11))) {
        Set-ItemProperty $keyboard -Name PrintScreenKeyForSnippingEnabled -Value 0 -Type DWord
        $snippingOff = $true
        Info 'PrintScreen больше не открывает «Ножницы» — клавиша достаётся ShareX'
    }

    Title '5/5. Запуск ShareX'
    $failed = @()
    if ($Test) { Info 'проверка: ShareX не запускается' }
    else {
        $log = Join-Path $data ('Logs\ShareX-Log-' + (Get-Date -Format 'yyyy-MM') + '.txt')
        $before = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
        $valid = @($hotkeys.Hotkeys | Where-Object { Format-Hotkey $_.HotkeyInfo }).Count
        Start-ShareX -Silent
        # ShareX пишет в лог, какие сочетания занял, а какие — нет
        $deadline = (Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Seconds 1
            $tail = Read-LogTail $log $before
            $registered = ([regex]::Matches($tail, 'Hotkey regist(ered|er failed)')).Count
        } until ($registered -ge $valid -or (Get-Date) -gt $deadline)
        $failed = @([regex]::Matches($tail, 'Hotkey register failed: Hotkey: ([^,]+)') | ForEach-Object { $_.Groups[1].Value.Trim() })
        if (-not $registered) { Warn "ShareX запущен, но в логе нет сочетаний: $log" }
        elseif ($failed) { Warn 'ShareX запущен, но занял не все сочетания — см. ниже' }
        else { Info 'ShareX запущен, сочетания заняты' }
    }

    Title 'Готово'
    foreach ($o in $ours | Where-Object { -not $_.Video }) { Info "$($o.Keys) — $($JobNames[$o.Job])" }
    foreach ($o in $ours | Where-Object { $_.Video }) { Info "$($o.Keys) — $($JobNames[$o.Job]); то же сочетание — стоп" }
    $days = @("$($ini['publicdays'])" -split '[,;\s]+' | Where-Object { $_ })
    if (-not $days) { $days = '1', '7', '30' }
    $publicKeys = if ($days.Count -gt 1) { "3–$($days.Count + 2)" } else { '3' }
    Info "После снимка — редактор (Enter — дальше), затем меню: 1 — скопировать, 2 — ссылка для команды,"
    Info "$publicKeys — публичная ссылка на $($days -join ', ') дн. После записи видео меню появляется сразу."
    Info 'При первой загрузке откроется браузер: войди в Nextcloud и подтверди доступ.'
    $shots = if ($app.UseCustomScreenshotsPath -and $app.CustomScreenshotsPath) { $app.CustomScreenshotsPath } else { Join-Path $data 'Screenshots' }
    Info "Снимки и записи остаются в $shots, как обычно у ShareX."
    if ($failed) {
        Info ''
        Warn ('ShareX не смог занять: ' + ($failed -join ', ') + '. Их держит другая программа.')
        if ($snippingOff) { Warn 'Если среди них PrintScreen — выйди из Windows и войди снова, чтобы «Ножницы» её отпустили.' }
    }
    Info ''
    Info 'Вернуть как было:'
    Info "  Stop-Process -Name ShareX; Copy-Item '$backup\*.json' '$data' -Force; Remove-Item '$tools' -Recurse"
    if (Test-Path (Join-Path $backup 'nc-share')) { Info "  Copy-Item '$backup\nc-share' '$tools' -Recurse" }
    if ($snippingOff) { Info "  Set-ItemProperty '$keyboard' PrintScreenKeyForSnippingEnabled 1" }
}
catch {
    Write-Host ''
    Write-Host ('Ошибка: ' + $_.Exception.Message) -ForegroundColor Red
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
}
