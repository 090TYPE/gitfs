# Acceptance for a live gitfs volume (milestones M3/M5).
#   powershell -ExecutionPolicy Bypass -File tools\acceptance.ps1 G:
# Every check returns $null on success or a message describing the failure.
param([string]$Drive = "G:", [string]$Repo = "C:\Users\090\Documents\GitHub\gitfs")

$ErrorActionPreference = "Stop"
$script:pass = 0
$script:fail = 0
$script:failures = @()

function Check($name, [scriptblock]$body) {
    $problem = $null
    try { $problem = & $body } catch { $problem = "EXCEPTION: " + $_.Exception.Message }
    if ($problem) {
        $script:fail++
        $script:failures += "$name -> $problem"
        Write-Host ("fail  " + $name) -ForegroundColor Red
        Write-Host ("      " + $problem) -ForegroundColor DarkGray
    } else {
        $script:pass++
        Write-Host ("ok    " + $name) -ForegroundColor Green
    }
}

function RunGit([string[]]$a) { $out = & git -C $Repo @a 2>$null; return ($out -join "`n") }

Write-Host "=== gitfs acceptance on $Drive ===" -ForegroundColor Cyan

# ---------- volume and tree ----------
Check "volume is mounted" {
    if (-not (Test-Path "$Drive\")) { return "drive $Drive missing" }
    return $null
}
Check "root lists five views" {
    $names = @((Get-ChildItem "$Drive\").Name | Sort-Object)
    $want = @("branches","commits","dates","history","tags")
    if (Compare-Object $names $want) { return "got: $($names -join ', ')" }
    return $null
}
Check "branch main is listed" {
    $found = @(Get-ChildItem "$Drive\branches" | Where-Object { $_.Name -eq "main" })
    if ($found.Count -eq 0) { return "main not listed" }
    return $null
}
Check "file from a branch matches git size exactly" {
    $disk = [System.IO.File]::ReadAllBytes("$Drive\branches\main\LICENSE")
    $gitSize = [int]((RunGit @("cat-file","-s","HEAD:LICENSE")).Trim())
    if ($disk.Length -ne $gitSize) { return "sizes differ: disk $($disk.Length), git $gitSize" }
    return $null
}
Check "nested path reads" {
    $p = "$Drive\branches\main\src\Gitfs.Core\Objects\PackFile.cs"
    if (-not (Test-Path $p)) { return "missing $p" }
    if ((Get-Item $p).Length -lt 1000) { return "suspicious size" }
    return $null
}

# ---------- history: a file is a folder ----------
Check "history: a file opens as a folder" {
    $p = "$Drive\history\src\Gitfs.Core\Objects\PackFile.cs"
    if (-not (Test-Path $p -PathType Container)) { return "not a directory" }
    return $null
}
Check "history: versions and latest are present" {
    $items = @((Get-ChildItem "$Drive\history\src\Gitfs.Core\Objects\PackFile.cs").Name)
    if ($items.Count -lt 2) { return "versions: $($items.Count)" }
    $hasLatest = @($items | Where-Object { $_ -like "latest*" })
    if ($hasLatest.Count -eq 0) { return "no latest in: $($items -join ', ')" }
    return $null
}
Check "history: an old version differs from the newest" {
    $dir = "$Drive\history\src\Gitfs.Core\Objects\PackFile.cs"
    $vs = @(Get-ChildItem $dir | Where-Object { $_.Name -match '^\d{4}-' } | Sort-Object Name)
    if ($vs.Count -lt 2) { return "only $($vs.Count) versions" }
    if ($vs[0].Length -eq $vs[-1].Length) { return "same size $($vs[0].Length)" }
    return $null
}
Check "history: version content equals git cat-file" {
    $dir = "$Drive\history\LICENSE"
    $v = @(Get-ChildItem $dir | Where-Object { $_.Name -match '^0001-' })
    if ($v.Count -eq 0) { return "no 0001- version" }
    $sha = ($v[0].BaseName -split '-')[1]
    $disk = [System.IO.File]::ReadAllBytes($v[0].FullName)
    $gitSize = [int]((RunGit @("cat-file","-s",$sha)).Trim())
    if ($disk.Length -ne $gitSize) { return "sizes differ: disk $($disk.Length), git $gitSize" }
    return $null
}

# ---------- other views ----------
Check "commits: recent commits are listed" {
    $c = @((Get-ChildItem "$Drive\commits").Name)
    if ($c.Count -lt 5) { return "commits: $($c.Count)" }
    return $null
}
Check "commits: a full SHA resolves" {
    $sha = (RunGit @("rev-parse","HEAD")).Trim()
    if (-not (Test-Path "$Drive\commits\$sha")) { return "missing $sha" }
    return $null
}
Check "dates: days are listed in ISO form" {
    $d = @((Get-ChildItem "$Drive\dates").Name)
    if ($d.Count -lt 1) { return "no days" }
    if ($d[0] -notmatch '^\d{4}-\d{2}-\d{2}$') { return "format: $($d[0])" }
    return $null
}
Check "tags view is reachable" {
    $null = Get-ChildItem "$Drive\tags" -ErrorAction SilentlyContinue
    return $null
}
Check "three views agree on the same file" {
    $sha = (RunGit @("rev-parse","HEAD")).Trim()
    $day = @(Get-ChildItem "$Drive\dates" | Sort-Object Name)[-1].Name
    $a = (Get-FileHash "$Drive\branches\main\LICENSE").Hash
    $b = (Get-FileHash "$Drive\commits\$sha\LICENSE").Hash
    $c = (Get-FileHash "$Drive\dates\$day\LICENSE").Hash
    if ($a -ne $b -or $b -ne $c) { return "hashes differ" }
    return $null
}

# ---------- filesystem behaviour ----------
Check "search across the volume (findstr)" {
    $m = findstr /s /m "OBJ_OFS_DELTA" "$Drive\branches\main\src\*.cs" 2>$null
    if (-not $m) { return "nothing found" }
    return $null
}
Check "two handles at once" {
    $p = "$Drive\branches\main\LICENSE"
    $a = [System.IO.File]::OpenRead($p)
    $b = [System.IO.File]::OpenRead($p)
    $x = New-Object byte[] 8
    $y = New-Object byte[] 8
    $null = $a.Read($x,0,8)
    $null = $b.Read($y,0,8)
    $a.Close(); $b.Close()
    if (Compare-Object $x $y) { return "handles disagree" }
    return $null
}
Check "random access (seek)" {
    $p = "$Drive\branches\main\src\Gitfs.Core\Objects\PackFile.cs"
    $fs = [System.IO.File]::OpenRead($p)
    $fs.Position = 4000
    $buf = New-Object byte[] 32
    $n = $fs.Read($buf,0,32)
    $fs.Close()
    if ($n -ne 32) { return "read $n of 32" }
    return $null
}
Check "copying a file off the volume" {
    $tmp = Join-Path $env:TEMP "gitfs-copy-test.bin"
    Copy-Item "$Drive\branches\main\LICENSE" $tmp -Force
    $len = (Get-Item $tmp).Length
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    if ($len -lt 100) { return "copy is $len bytes" }
    return $null
}
Check "missing path is reported missing" {
    if (Test-Path "$Drive\branches\main\no-such-file.txt") { return "path exists" }
    return $null
}
Check "a directory is not readable as a file" {
    try {
        $null = [System.IO.File]::ReadAllBytes("$Drive\branches\main\src")
        return "directory read as a file"
    } catch { return $null }
}

# ---------- overlay ----------
Check "overwrite yields exactly what was written" {
    $p = "$Drive\branches\main\LICENSE"
    $text = "OVERLAY-WRITE-CHECK"
    [System.IO.File]::WriteAllText($p, $text)
    $back = [System.IO.File]::ReadAllText($p)
    if ($back -ne $text) { return "read back '$($back.Substring(0,[Math]::Min(40,$back.Length)))' len=$($back.Length)" }
    return $null
}
Check "creating a new file" {
    $p = "$Drive\branches\main\brand-new-file.txt"
    [System.IO.File]::WriteAllText($p, "created")
    $back = [System.IO.File]::ReadAllText($p)
    if ($back -ne "created") { return "read back '$back'" }
    return $null
}
Check "created file shows up in the listing" {
    $found = @(Get-ChildItem "$Drive\branches\main" | Where-Object { $_.Name -eq "brand-new-file.txt" })
    if ($found.Count -eq 0) { return "not listed" }
    return $null
}
Check "deleting a file" {
    $p = "$Drive\branches\main\brand-new-file.txt"
    Remove-Item -LiteralPath $p -Force
    if (Test-Path $p) { return "still there" }
    return $null
}
Check "overlay covers an immutable view (commits)" {
    $sha = (RunGit @("rev-parse","HEAD")).Trim()
    $p = "$Drive\commits\$sha\LICENSE"
    [System.IO.File]::WriteAllText($p, "EDITED-IN-COMMIT-VIEW")
    $back = [System.IO.File]::ReadAllText($p)
    if ($back -ne "EDITED-IN-COMMIT-VIEW") { return "read back '$back'" }
    return $null
}

# ---------- the invariant that matters ----------
Check "REPOSITORY IS UNTOUCHED" {
    $status = RunGit @("status","--porcelain")
    if ($status -match "LICENSE") { return "git status shows LICENSE changed: $status" }
    $len = [int]((RunGit @("cat-file","-s","HEAD:LICENSE")).Trim())
    if ($len -lt 1000) { return "LICENSE in git is now $len bytes" }
    return $null
}

Write-Host ""
$color = if ($script:fail -eq 0) { "Green" } else { "Yellow" }
Write-Host "=== RESULT: $script:pass ok, $script:fail fail ===" -ForegroundColor $color
if ($script:fail -gt 0) {
    foreach ($f in $script:failures) { Write-Host "  $f" -ForegroundColor DarkYellow }
}
exit $script:fail
