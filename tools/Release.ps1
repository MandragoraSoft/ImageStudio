#requires -Version 7.0
<#
.SYNOPSIS
  Weryfikuje, wypycha i publikuje wydanie ImageStudio przez GitHub Actions.
.DESCRIPTION
  Wymaga gotowych notatek docs/release-notes/<wersja>.md. Uruchamia kontrolę tłumaczeń,
  build i testy, a po potwierdzeniu zatwierdza notatki, wypycha main, uruchamia pipeline
  i sprawdza, czy wydanie ma trzy oczekiwane pliki.
.PARAMETER Version
  Wersja bez prefiksu v, np. 1.6.0 albo 1.6.0-beta.1.
.EXAMPLE
  pwsh -NoProfile -File tools/Release.ps1 -Version 1.6.0
.EXAMPLE
  pwsh -NoProfile -File tools/Release.ps1 -Version 1.6.0 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$githubRepo = 'MandragoraSoft/ImageStudio'
$notesGitPath = "docs/release-notes/$Version.md"
$expectedAssets = @(
    "ImageStudio-$Version-Setup.exe"
    "ImageStudio-$Version-win-x64-samodzielna.exe"
    "ImageStudio-$Version-win-x64-wymaga-dotnet.exe"
)

function Invoke-ReleaseCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,
        [string[]]$Arguments = @(),
        [switch]$Capture
    )

    Write-Host "`n> $Command $($Arguments -join ' ')"
    if ($Capture) {
        $output = @(& $Command @Arguments 2>&1)
    }
    else {
        & $Command @Arguments
    }

    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = if ($Capture) {
            "`n$(($output | ForEach-Object { "$_" }) -join [Environment]::NewLine)"
        }
        else {
            ''
        }
        throw "Polecenie zakończyło się kodem ${exitCode}: $Command $($Arguments -join ' ')$details"
    }

    if ($Capture) {
        return ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    }
}

foreach ($commandName in @('git', 'gh', 'dotnet', 'pwsh')) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Nie znaleziono wymaganego polecenia: $commandName."
    }
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Lokalna walidacja i wydawanie ImageStudio wymagają systemu Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repoRoot
try {
    $gitRoot = Invoke-ReleaseCommand -Command 'git' -Arguments @('rev-parse', '--show-toplevel') -Capture
    $gitRootPath = [IO.Path]::GetFullPath($gitRoot.TrimEnd())
    $normalizedRepoRoot = $repoRoot.TrimEnd([char[]]@('\', '/'))
    if ($gitRootPath -ine $normalizedRepoRoot) {
        throw "Skrypt musi być uruchomiony z repozytorium ImageStudio: $repoRoot"
    }

    $branch = Invoke-ReleaseCommand -Command 'git' -Arguments @('branch', '--show-current') -Capture
    if ($branch.Trim() -ne 'main') {
        throw "Wydanie można przygotować tylko na gałęzi main (bieżąca: $($branch.Trim()))."
    }

    $originUrl = Invoke-ReleaseCommand -Command 'git' -Arguments @('remote', 'get-url', 'origin') -Capture
    if ($originUrl.Trim() -notmatch "(?i)github\.com[:/]$([regex]::Escape($githubRepo))(?:\.git)?$") {
        throw "origin nie wskazuje na https://github.com/$githubRepo (obecnie: $($originUrl.Trim()))."
    }

    $remoteRepo = Invoke-ReleaseCommand -Command 'gh' -Arguments @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner') -Capture
    if ($remoteRepo.Trim() -ne $githubRepo) {
        throw "gh wskazuje repozytorium $($remoteRepo.Trim()), a oczekiwane jest $githubRepo."
    }
    Invoke-ReleaseCommand -Command 'gh' -Arguments @('auth', 'status', '--hostname', 'github.com')

    $notesPath = Join-Path $repoRoot "docs\release-notes\$Version.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "Brak notatek wydania: $notesGitPath. Przygotuj je przed uruchomieniem skryptu."
    }
    $notes = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($notes)) {
        throw "Notatki wydania są puste: $notesGitPath."
    }
    if ($notes -notmatch "(?m)^# ImageStudio $([regex]::Escape($Version))(?::|\s*$)") {
        throw "Tytuł notatek musi zaczynać się od '# ImageStudio ${Version}'."
    }
    if ($notes -notmatch '(?m)^## (Nowe|Poprawki)(?:\s|$)' -or
        $notes -notmatch '(?m)^## Znane ograniczenia(?:\s|$)' -or
        $notes -notmatch '(?m)^## Który plik pobrać(?:\s|$)') {
        throw 'Notatki muszą zawierać sekcje Nowe lub Poprawki, Znane ograniczenia oraz Który plik pobrać.'
    }
    foreach ($asset in $expectedAssets) {
        if (-not $notes.Contains($asset)) {
            throw "W notatkach brakuje nazwy pliku wydania: $asset."
        }
    }

    Invoke-ReleaseCommand -Command 'git' -Arguments @('fetch', 'origin', 'refs/heads/main:refs/remotes/origin/main')
    $countsText = Invoke-ReleaseCommand -Command 'git' -Arguments @('rev-list', '--left-right', '--count', 'origin/main...HEAD') -Capture
    $counts = @($countsText.Trim() -split '\s+')
    if ($counts.Count -ne 2) {
        throw "Nie można ustalić rozjazdu z origin/main: $countsText"
    }
    $behind = [int]$counts[0]
    $ahead = [int]$counts[1]
    $initialHead = (Invoke-ReleaseCommand -Command 'git' -Arguments @('rev-parse', 'HEAD') -Capture).Trim()
    if ($behind -gt 0) {
        throw "Lokalny main jest opóźniony o $behind commit(y). Zaktualizuj main i ponów próbę."
    }
    if ($ahead -gt 1) {
        throw "Lokalny main wyprzedza origin/main o $ahead commitów. Skrypt dopuszcza najwyżej nieopublikowany commit z notatkami."
    }

    $statusText = Invoke-ReleaseCommand -Command 'git' -Arguments @('status', '--porcelain=v1', '--untracked-files=all') -Capture
    $statusLines = @()
    if (-not [string]::IsNullOrWhiteSpace($statusText)) {
        $statusLines = @($statusText -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    foreach ($line in $statusLines) {
        if ($line.Length -lt 4 -or $line.Substring(3) -ne $notesGitPath) {
            throw "Drzewo robocze zawiera zmiany poza notatkami wydania: $line. Zapisz je lub odłóż i ponów próbę."
        }
    }

    $headFilesText = Invoke-ReleaseCommand -Command 'git' -Arguments @('ls-tree', '-r', '--name-only', 'HEAD', '--', $notesGitPath) -Capture
    $notesInHead = $headFilesText.Trim() -ceq $notesGitPath
    if ($ahead -eq 1) {
        if ($statusLines.Count -gt 0) {
            throw 'Wykryto lokalny commit z notatkami oraz dodatkowe niezapisane zmiany. Sprawdź je ręcznie przed wydaniem.'
        }
        $headMessage = Invoke-ReleaseCommand -Command 'git' -Arguments @('log', '-1', '--format=%s') -Capture
        $headFiles = Invoke-ReleaseCommand -Command 'git' -Arguments @('diff-tree', '--no-commit-id', '--name-only', '-r', 'HEAD') -Capture
        $expectedCommitTitles = @("Dodaj notatki wydania $Version", "Aktualizuj notatki wydania $Version")
        if ($headMessage.Trim() -notin $expectedCommitTitles -or
            $headFiles.Trim() -cne $notesGitPath -or
            -not $notesInHead) {
            throw 'Jedyny lokalny commit przed wydaniem musi zawierać wyłącznie notatki dla tej wersji.'
        }
    }
    elseif (-not $notesInHead -and $statusLines.Count -eq 0) {
        throw "Notatki $notesGitPath nie są śledzone przez Git. Sprawdź, czy plik nie jest ignorowany."
    }

    $releaseListJson = Invoke-ReleaseCommand -Command 'gh' -Arguments @('release', 'list', '--repo', $githubRepo, '--limit', '1000', '--json', 'tagName') -Capture
    $releases = @($releaseListJson | ConvertFrom-Json)
    if ($releases | Where-Object { $_.tagName -ceq "v$Version" }) {
        throw "Wydanie v$Version już istnieje. Nie nadpisuję istniejących wydań."
    }
    $localTag = Invoke-ReleaseCommand -Command 'git' -Arguments @('tag', '--list', "v$Version") -Capture
    if (-not [string]::IsNullOrWhiteSpace($localTag)) {
        throw "Lokalny tag v$Version już istnieje. Nie nadpisuję tagów."
    }
    $remoteTag = Invoke-ReleaseCommand -Command 'git' -Arguments @('ls-remote', '--refs', 'origin', "refs/tags/v$Version") -Capture
    if (-not [string]::IsNullOrWhiteSpace($remoteTag)) {
        throw "Zdalny tag v$Version już istnieje. Nie nadpisuję tagów."
    }
    if ($releases.Count -gt 0) {
        Write-Host "Ostatnie wydanie GitHub: $($releases[0].tagName)"
    }

    Write-Host "`nNotatki, które zostaną opublikowane jako ImageStudio ${Version}:" -ForegroundColor Cyan
    Write-Host $notes

    Invoke-ReleaseCommand -Command 'pwsh' -Arguments @('-NoProfile', '-File', (Join-Path $repoRoot 'tools\Check-Translations.ps1'), '-Strict')
    Invoke-ReleaseCommand -Command 'dotnet' -Arguments @('build', 'ImageStudio.slnx', '-c', 'Release', '-m:1')
    Invoke-ReleaseCommand -Command 'dotnet' -Arguments @('test', 'ImageStudio.slnx', '-c', 'Release', '--no-build')

    $action = "zatwierdź notatki, wypchnij main i opublikuj GitHub Release v$Version"
    if (-not $PSCmdlet.ShouldProcess($githubRepo, $action)) {
        Write-Host "`nPominięto commit, push i publikację. Testy i kontrole lokalne zostały wykonane."
        return
    }

    $currentHead = (Invoke-ReleaseCommand -Command 'git' -Arguments @('rev-parse', 'HEAD') -Capture).Trim()
    if ($currentHead -cne $initialHead) {
        throw 'Gałąź main zmieniła się podczas walidacji. Sprawdź repozytorium i uruchom skrypt ponownie.'
    }
    $currentNotes = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
    if ($currentNotes -cne $notes) {
        throw 'Notatki zmieniły się podczas walidacji. Sprawdź je i uruchom skrypt ponownie, aby zatwierdzić aktualną treść.'
    }
    $currentStatusText = Invoke-ReleaseCommand -Command 'git' -Arguments @('status', '--porcelain=v1', '--untracked-files=all') -Capture
    $statusLines = @()
    if (-not [string]::IsNullOrWhiteSpace($currentStatusText)) {
        $statusLines = @($currentStatusText -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    foreach ($line in $statusLines) {
        if ($line.Length -lt 4 -or $line.Substring(3) -ne $notesGitPath) {
            throw "Drzewo robocze zmieniło się podczas walidacji: $line. Sprawdź je ręcznie przed wydaniem."
        }
    }

    $needsCommit = -not $notesInHead -or $statusLines.Count -gt 0
    if ($needsCommit) {
        Invoke-ReleaseCommand -Command 'git' -Arguments @('add', '--', $notesGitPath)
        $commitTitle = if ($notesInHead) { "Aktualizuj notatki wydania $Version" } else { "Dodaj notatki wydania $Version" }
        Invoke-ReleaseCommand -Command 'git' -Arguments @('commit', '-m', $commitTitle)
    }

    $notesAfterCommit = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
    if ($notesAfterCommit -cne $notes) {
        throw 'Treść notatek zmieniła się podczas commitu. Nie wypycham zmian ani nie uruchamiam wydania.'
    }
    $statusAfterCommit = Invoke-ReleaseCommand -Command 'git' -Arguments @('status', '--porcelain=v1', '--untracked-files=all') -Capture
    if (-not [string]::IsNullOrWhiteSpace($statusAfterCommit)) {
        throw "Po commicie pozostały niezapisane zmiany. Sprawdź repozytorium; nie wypycham zmian ani nie uruchamiam wydania.`n$statusAfterCommit"
    }

    $commitSha = (Invoke-ReleaseCommand -Command 'git' -Arguments @('rev-parse', 'HEAD') -Capture).Trim()
    $originSha = (Invoke-ReleaseCommand -Command 'git' -Arguments @('rev-parse', 'origin/main') -Capture).Trim()
    if ($commitSha -ne $originSha) {
        Invoke-ReleaseCommand -Command 'git' -Arguments @('push', 'origin', 'main')
    }

    $remoteMain = Invoke-ReleaseCommand -Command 'git' -Arguments @('ls-remote', '--heads', 'origin', 'refs/heads/main') -Capture
    $remoteSha = ($remoteMain.Trim() -split '\s+')[0]
    if ($remoteSha -ne $commitSha) {
        throw "origin/main nie wskazuje na zatwierdzony commit $commitSha. Nie uruchamiam wydania."
    }

    $dispatchTime = [DateTimeOffset]::UtcNow
    Invoke-ReleaseCommand -Command 'gh' -Arguments @('workflow', 'run', 'release.yml', '--repo', $githubRepo, '--ref', 'main', '-f', "version=$Version")

    $run = $null
    for ($attempt = 0; $attempt -lt 30 -and $null -eq $run; $attempt++) {
        Start-Sleep -Seconds 2
        $runsJson = Invoke-ReleaseCommand -Command 'gh' -Arguments @(
            'run', 'list', '--repo', $githubRepo, '--workflow', 'release.yml',
            '--branch', 'main', '--event', 'workflow_dispatch', '--limit', '50',
            '--json', 'databaseId,headSha,status,createdAt,url'
        ) -Capture
        $runs = @($runsJson | ConvertFrom-Json)
        $run = $runs |
            Where-Object {
                $_.headSha -ceq $commitSha -and
                # ConvertFrom-Json w PowerShell 7 zwraca już DateTime; Parse z bieżącą kulturą wywracał się na polskim systemie
                ([DateTimeOffset]$_.createdAt).UtcDateTime -ge $dispatchTime.UtcDateTime.AddSeconds(-5)
            } |
            Sort-Object -Property createdAt -Descending |
            Select-Object -First 1
    }
    if ($null -eq $run) {
        throw "Nie znaleziono uruchomionego pipeline'u dla commitu $commitSha. Sprawdź https://github.com/$githubRepo/actions/workflows/release.yml; nie uruchamiam go ponownie automatycznie."
    }

    Write-Host "`nPipeline wydania: $($run.url)" -ForegroundColor Cyan
    Invoke-ReleaseCommand -Command 'gh' -Arguments @('run', 'watch', "$($run.databaseId)", '--repo', $githubRepo, '--exit-status')

    $releaseJson = Invoke-ReleaseCommand -Command 'gh' -Arguments @('release', 'view', "v$Version", '--repo', $githubRepo, '--json', 'url,tagName,assets') -Capture
    $release = $releaseJson | ConvertFrom-Json
    if ($release.tagName -cne "v$Version") {
        throw "GitHub zwrócił nieoczekiwany tag wydania: $($release.tagName)."
    }
    $assetNames = @($release.assets | ForEach-Object { $_.name })
    $missingAssets = @($expectedAssets | Where-Object { $_ -notin $assetNames })
    $unexpectedAssets = @($assetNames | Where-Object { $_ -notin $expectedAssets })
    if ($assetNames.Count -ne $expectedAssets.Count -or $missingAssets.Count -gt 0 -or $unexpectedAssets.Count -gt 0) {
        throw "Wydanie v$Version ma nieoczekiwane pliki. Otrzymano: $($assetNames -join ', ')."
    }

    Write-Host "`nOpublikowano: $($release.url)" -ForegroundColor Green
    foreach ($asset in $assetNames) {
        Write-Host " - $asset"
    }
}
finally {
    Pop-Location
}
