# Собирает то, что уходит пользователю. Публиковать разовыми командами из
# консоли нельзя: один забытый флаг — и релиз не монтирует ничего (так уже
# было: single-file без самораспаковки оставляет Assembly.Location пустым,
# winfsp.net не находит свою нативную библиотеку, и «Смонтировать» падает
# на машине, где драйвер установлен и doctor его видит).
#
#   powershell -ExecutionPolicy Bypass -File tools/publish.ps1
#   powershell -ExecutionPolicy Bypass -File tools/publish.ps1 -Runtime win-x64

param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist/$Runtime"

# работающий exe нельзя перезаписать: publish упадёт на UnauthorizedAccess
$busy = Get-Process -Name 'Gitfs.App', 'Gitfs.Cli' -ErrorAction SilentlyContinue
if ($busy) {
    $names = ($busy | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
    Write-Error "close these first, they hold the published binaries: $names"
}

$projects = @('src/Gitfs.Cli/Gitfs.Cli.csproj')
if ($Runtime -like 'win-*') { $projects += 'src/Gitfs.App/Gitfs.App.csproj' }

foreach ($project in $projects) {
    Write-Host "publishing $project -> dist/$Runtime"
    & dotnet publish (Join-Path $root $project) `
        -c $Configuration -r $Runtime --self-contained false `
        -p:PublishSingleFile=true -o $out --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error "publish failed: $project" }
}

# Дымовая проверка: бинарник обязан хотя бы отвечать. Полную проверку
# монтирования делает tools/acceptance.ps1 на живом томе.
$cli = Join-Path $out 'Gitfs.Cli.exe'
& $cli doctor $root | Out-Null
if ($LASTEXITCODE -gt 1) { Write-Error "published cli is broken: doctor exited $LASTEXITCODE" }

Get-ChildItem $out -Filter *.exe | ForEach-Object {
    "{0,-16} {1,10:N0} bytes" -f $_.Name, $_.Length
}
Write-Host "ok published to dist/$Runtime"
