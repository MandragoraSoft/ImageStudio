<#
.SYNOPSIS
  Sprawdza spójność słowników tłumaczeń (Strings.pl.json, Strings.en.json) i ich użycie w kodzie.

.DESCRIPTION
  Błędy (kod wyjścia 1):
    - nieprawidłowy JSON, powtórzony klucz w pliku (JSON po cichu bierze ostatni),
    - klucz tylko w jednym języku, pusty tekst, niezgodne parametry ({0}, {1:0.####}) między językami,
    - klucz użyty w XAML ({loc:T klucz}) albo w kodzie (Loc.T("klucz")), którego nie ma w słowniku,
    - klucz o złej nazwie (oczekiwane grupa.nazwa, np. voice.err.save).
  Ostrzeżenia (kod wyjścia 1 tylko z -Strict):
    - klucz nieużywany w XAML ani w kodzie (poza prefiksami składanymi w kodzie, np. lang.),
    - tekst angielski identyczny z polskim (prawdopodobnie nieprzetłumaczony),
    - klucze w pliku nie są posortowane (napraw: -Fix).

.PARAMETER Fix      Sortuje klucze i zapisuje oba pliki w jednolitym formacie (UTF-8, wcięcie 2, nowa linia na końcu).
.PARAMETER Pending  Dodatkowo wypisuje pliki z tekstami po polsku wpisanymi na sztywno (kolejne do przetłumaczenia).
.PARAMETER Strict   Ostrzeżenia też kończą się kodem 1 (np. w CI).
.PARAMETER Root     Katalog repozytorium (domyślnie katalog nadrzędny wobec tools\); do testów na kopii.

.EXAMPLE
  pwsh tools/Check-Translations.ps1
  pwsh tools/Check-Translations.ps1 -Fix
  pwsh tools/Check-Translations.ps1 -Pending
#>
param(
    [switch]$Fix,
    [switch]$Pending,
    [switch]$Strict,
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)   # polskie znaki w logu (CI, konsola)
$dictDir = Join-Path $Root 'ImageStudio.Shared/Localization'
$languages = @('pl', 'en')
$keyPattern = '^[a-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$'
$dynamicPrefixes = @('lang.')                       # klucze składane w kodzie, np. $"lang.{Code}"
$sameInBothLanguages = @('settings.language.pl', 'settings.language.en', 'voice.format', 'voice.stop', 'common.model', 'common.prompt', 'gen.seed', 'presets.auto', 'tools.port', 'chat.incognito', 'chat.stop', 'param.seed', 'param.topk', 'param.topp', 'settings.tab.mcp')   # nazwy własne i skróty

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

$opt = [System.Text.Json.JsonSerializerOptions]::new()
$opt.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping

function Read-Dictionary([string]$lang) {
    $path = Join-Path $dictDir "Strings.$lang.json"
    if (-not (Test-Path $path)) { $errors.Add("Brak pliku: $path"); return $null }
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    try { $parsed = $raw | ConvertFrom-Json -AsHashtable } catch { $errors.Add("Strings.${lang}.json: nieprawidłowy JSON ($($_.Exception.Message))"); return $null }

    # powtórzony klucz: parser zostawia ostatni, więc znajdujemy je w surowym tekście
    $seen = @{}
    foreach ($m in [regex]::Matches($raw, '(?m)^\s*"((?:[^"\\]|\\.)+)"\s*:')) {
        $key = $m.Groups[1].Value
        if ($seen.ContainsKey($key)) { $errors.Add("Strings.${lang}.json: klucz powtórzony: $key") } else { $seen[$key] = $true }
    }

    $ordered = [string[]]($parsed.Keys)
    $sorted = [string[]]([System.Linq.Enumerable]::OrderBy([string[]]$parsed.Keys, [Func[string, string]] { param($k) $k }, [StringComparer]::Ordinal))
    $isSorted = ($ordered -join "`n") -eq ($sorted -join "`n")
    if (-not $isSorted -and -not $Fix) { $warnings.Add("Strings.${lang}.json: klucze nie są posortowane (uruchom z -Fix)") }
    return @{ Path = $path; Data = $parsed; Raw = $raw; Sorted = $sorted }
}

$dict = @{}
foreach ($lang in $languages) { $dict[$lang] = Read-Dictionary $lang }

if ($Fix) {
    foreach ($lang in $languages) {
        $d = $dict[$lang]
        if ($null -eq $d) { continue }
        $lines = foreach ($key in $d.Sorted) {
            '  ' + [System.Text.Json.JsonSerializer]::Serialize($key, $opt) + ': ' + [System.Text.Json.JsonSerializer]::Serialize([string]$d.Data[$key], $opt)
        }
        $text = "{`n" + ($lines -join ",`n") + "`n}`n"
        if ($text -ne $d.Raw) {
            [System.IO.File]::WriteAllText($d.Path, $text, [System.Text.UTF8Encoding]::new($false))
            Write-Host "Zapisano $($d.Path)" -ForegroundColor Cyan
        }
    }
}

if ($null -ne $dict['pl'] -and $null -ne $dict['en']) {   # tylko gdy oba pliki dały się wczytać; reszta błędów jest raportowana razem
    $pl = $dict['pl'].Data
    $en = $dict['en'].Data

    foreach ($key in $pl.Keys | Where-Object { -not $en.ContainsKey($_) }) { $errors.Add("Brak w angielskim: $key") }
    foreach ($key in $en.Keys | Where-Object { -not $pl.ContainsKey($_) }) { $errors.Add("Brak w polskim: $key") }

    function Get-Placeholders([string]$text) {
        ([regex]::Matches($text, '\{(\d+)(?::[^}]*)?\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) -join ','
    }

    foreach ($key in $pl.Keys) {
        if ($key -notmatch $keyPattern) { $errors.Add("Zła nazwa klucza (oczekiwane grupa.nazwa): $key") }
        foreach ($lang in $languages) {
            if ($dict[$lang].Data.ContainsKey($key) -and [string]::IsNullOrWhiteSpace([string]$dict[$lang].Data[$key])) { $errors.Add("Pusty tekst ($lang): $key") }
        }
        if ($en.ContainsKey($key)) {
            if ((Get-Placeholders $pl[$key]) -ne (Get-Placeholders $en[$key])) {
                $errors.Add("Różne parametry w PL i EN: $key (pl: {$(Get-Placeholders $pl[$key])}, en: {$(Get-Placeholders $en[$key])})")
            }
            if ($pl[$key] -ceq $en[$key] -and $key -notin $sameInBothLanguages) { $warnings.Add("Tekst EN identyczny z PL (nieprzetłumaczony?): $key = '$($en[$key])'") }
        }
    }

    # użycie kluczy
    $files = Get-ChildItem -Path $Root -Recurse -Include *.xaml, *.cs -File |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin|\.git|ImageStudio\.Tests)[\\/]' }
    $used = @{}
    $knownKeys = [System.Collections.Generic.HashSet[string]]::new([string[]]$pl.Keys)
    foreach ($f in $files) {
        $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
        $rel = $f.FullName.Substring($Root.TrimEnd('\', '/').Length + 1)
        if ($f.Extension -eq '.xaml') {
            # {loc:T klucz} oraz NullToLoc z kluczem w ConverterParameter
            foreach ($m in [regex]::Matches($text, '\{loc:T ([A-Za-z0-9.]+)\}|NullToLoc\.Instance\}, ConverterParameter=([A-Za-z0-9.]+)')) {
                $k = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
                if (-not $knownKeys.Contains($k)) { $errors.Add("Nieznany klucz w XAML: $k ($rel)") } else { $used[$k] = $true }
            }
        }
        if ($f.Extension -eq '.cs') {
            foreach ($m in [regex]::Matches($text, 'Loc\.T\(\s*"([^"]+)"')) {
                $k = $m.Groups[1].Value
                if (-not $knownKeys.Contains($k)) { $errors.Add("Nieznany klucz w Loc.T: $k ($rel)") } else { $used[$k] = $true }
            }
            # klucze podawane inaczej (np. warunek ? "a" : "b"): każdy literał równy kluczowi liczy się jako użycie
            foreach ($m in [regex]::Matches($text, '"([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)"')) {
                if ($knownKeys.Contains($m.Groups[1].Value)) { $used[$m.Groups[1].Value] = $true }
            }
        }
    }
    foreach ($key in $pl.Keys | Sort-Object) {
        if (-not $used.ContainsKey($key) -and -not ($dynamicPrefixes | Where-Object { $key.StartsWith($_) })) { $warnings.Add("Nieużywany klucz: $key") }
    }

    Write-Host ("Klucze: {0} (pl) / {1} (en); użyte: {2}" -f $pl.Count, $en.Count, $used.Count)
}

if ($Pending) {
    Write-Host "`nTeksty po polsku wpisane na sztywno (do przetłumaczenia):" -ForegroundColor Yellow
    $polish = '[ąćęłńóśźżĄĆĘŁŃÓŚŹŻ]'
    $rows = foreach ($f in Get-ChildItem -Path $Root -Recurse -Include *.xaml, *.cs -File | Where-Object { $_.FullName -notmatch '[\\/](obj|bin|\.git|ImageStudio\.Tests)[\\/]' }) {
        $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
        if ($f.Extension -eq '.xaml') {
            $n = ([regex]::Matches(($lines -join "`n"), '\b(?:Content|Header|Text|ToolTip|Title)="([^"{][^"]*)"') |
                Where-Object { $_.Groups[1].Value -match '[A-Za-ząćęłńóśźżĄĆĘŁŃÓŚŹŻ]{3}' -and $_.Groups[1].Value -notmatch '^(&#|ImageStudio$|Ctrl\+|NSFW$|: $)' }).Count
        } else {
            $n = ($lines | Where-Object { $_ -notmatch '^\s*//' -and $_ -match "`"[^`"]*$polish[^`"]*`"" -and $_ -notmatch 'Loc\.T' }).Count
        }
        if ($n -gt 0) { [pscustomobject]@{ Count = $n; File = $f.FullName.Substring($Root.TrimEnd('\', '/').Length + 1) } }
    }
    $rows | Sort-Object Count -Descending | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host ("Razem: {0} tekstów w {1} plikach" -f ($rows | Measure-Object Count -Sum).Sum, @($rows).Count)
}

foreach ($e in $errors) { Write-Host "BŁĄD: $e" -ForegroundColor Red }
foreach ($w in $warnings) { Write-Host "Ostrzeżenie: $w" -ForegroundColor Yellow }
Write-Host ("Błędy: {0}, ostrzeżenia: {1}" -f $errors.Count, $warnings.Count) -ForegroundColor $(if ($errors.Count -gt 0) { 'Red' } elseif ($warnings.Count -gt 0) { 'Yellow' } else { 'Green' })

if ($errors.Count -gt 0 -or ($Strict -and $warnings.Count -gt 0)) { exit 1 }
exit 0
