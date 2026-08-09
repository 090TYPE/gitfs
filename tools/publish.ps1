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
# Дымовой прогон возможен только для своей платформы: чужой бинарник эта
# машина не запустит. Раньше `-Runtime linux-x64` собирал всё как надо, а
# потом падал на попытке выполнить несуществующий gitfs.exe — публикация
# уже прошла, а скрипт сообщал об ошибке.
$exe = if ($Runtime -like 'win-*') { 'gitfs.exe' } else { 'gitfs' }
$cli = Join-Path $out $exe
if (-not (Test-Path $cli)) { Write-Error "publish produced no $exe in $out" }

$hostIsWindows = $Runtime -like 'win-*'
if ($hostIsWindows) {
    $doctor = & $cli doctor $root
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) {
        Write-Error "published cli is broken: doctor exited $LASTEXITCODE"
    }
    # И вывод обязан быть: молчаливый ноль означает, что бинарник не дошёл
    # до собственного кода.
    if (-not $doctor) { Write-Error "published cli printed nothing: doctor produced no output" }
} else {
    Write-Host "skipping the smoke test: $Runtime binaries do not run on this host"
}

Get-ChildItem $out | Where-Object { -not $_.PSIsContainer -and -not $_.Extension } |
    ForEach-Object { "{0,-16} {1,10:N0} bytes" -f $_.Name, $_.Length }
Get-ChildItem $out -Filter *.exe | ForEach-Object {
    "{0,-16} {1,10:N0} bytes" -f $_.Name, $_.Length
}

# Контрольные суммы в том формате, который понимает sha256sum -c: два
# пробела между хешем и именем, ничего больше, и перевод строки LF. Первая,
# собранная руками версия несла приписку с размером и CRLF — она выглядела
# как стандартный файл, называлась как стандартный файл и не проверялась
# ничем. Поэтому теперь её пишет скрипт.
# -Exclude обязателен: dotnet publish -o каталог не чистит, поэтому со
# второго запуска файл сумм лежит здесь же и хеширует сам себя — записывая
# хеш ПРОШЛОЙ своей версии. `sha256sum -c` после этого печатает
# «SHA256SUMS.txt: FAILED» и не сойдётся никогда. В CI это невидимо: там
# всегда первый запуск на чистом раннере.
$sums = Get-ChildItem $out -File -Exclude 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
}
$sumsPath = Join-Path $out 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText($sumsPath, ($sums -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
"wrote $sumsPath"
Write-Host "ok published to dist/$Runtime"
