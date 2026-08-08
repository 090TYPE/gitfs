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
$busy = Get-Process -Name 'Gitfs.App', 'gitfs' -ErrorAction SilentlyContinue
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
#
# Проверка идёт «код не 0 и не 1», а НЕ «код больше единицы». Коды
# аварийного завершения Windows больше 0x7FFFFFFF и приезжают сюда
# отрицательными: нарушение доступа даёт -1073741819, отсутствующий
# фреймворк -2147450730, необработанное исключение -532462766. Все они
# «меньше единицы» и прежнюю проверку проходили — то есть ровно те случаи,
# ради которых она написана, она и пропускала.
$cli = Join-Path $out 'gitfs.exe'
$doctor = & $cli doctor $root
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) {
    Write-Error "published cli is broken: doctor exited $LASTEXITCODE"
}
# И вывод обязан быть: молчаливый ноль означает, что бинарник не дошёл до
# собственного кода.
if (-not $doctor) { Write-Error "published cli printed nothing: doctor produced no output" }

Get-ChildItem $out -Filter *.exe | ForEach-Object {
    "{0,-16} {1,10:N0} bytes" -f $_.Name, $_.Length
}
Write-Host "ok published to dist/$Runtime"
