# Сборка и установка nc-share: компилирует nc-share.cs в nc-share.exe и переносит сохранённый вход.
# Запускать один раз и после изменения nc-share.cs (nc-share.ini читается при каждом запуске, пересборка не нужна):
#   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'

$src  = Join-Path $PSScriptRoot 'nc-share.cs'
$exe  = Join-Path $PSScriptRoot 'nc-share.exe'
$data = Join-Path $env:APPDATA 'nc-share'

# 1. Сборка компилятором C# из .NET Framework — он есть в любой Windows
$csc = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$out = & $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Web.Extensions.dll $src 2>&1
if ($LASTEXITCODE -ne 0) {
    $out
    throw 'Сборка не удалась. Если nc-share.exe сейчас запущен — закройте его меню и повторите.'
}
"Собрано: $exe"

# 2. Перенос входа из версии на PowerShell (credential.xml → credential.dat в формате nc-share.exe)
$oldCred = Join-Path $data 'credential.xml'
$newCred = Join-Path $data 'credential.dat'
if ((Test-Path $oldCred) -and -not (Test-Path $newCred)) {
    Add-Type -AssemblyName System.Security
    $c = Import-Clixml $oldCred
    $plain = [Text.Encoding]::UTF8.GetBytes($c.UserName + "`n" + $c.GetNetworkCredential().Password)
    $entropy = [Text.Encoding]::UTF8.GetBytes('nc-share')
    [IO.File]::WriteAllBytes($newCred, [Security.Cryptography.ProtectedData]::Protect($plain, $entropy, 'CurrentUser'))
    'Сохранённый вход перенесён'
}

''
'ShareX → Настройки задач → Действия → действие nc-share:'
"  Путь:      $exe"
'  Аргументы: %input'
