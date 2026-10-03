#Requires -Version 7.0
<#
.SYNOPSIS
    Erstellt ein Release: Version erhöhen, CHANGELOG fortschreiben, bauen, ZIP packen, committen, taggen.
.DESCRIPTION
    Ablauf:
      1. Prüft, dass der Git-Arbeitsbaum sauber ist.
      2. Setzt <Version> in src/InstrumentPanel/InstrumentPanel.csproj.
      3. Überführt den Abschnitt [Unreleased] im CHANGELOG.md in die neue Version.
      4. Baut im Release-Modus und packt dist/InstrumentPanel-vX.Y.Z.zip.
      5. Committet "Release vX.Y.Z" und setzt das Tag vX.Y.Z.
      6. Mit -Push: pusht Commit und Tag; ist die gh-CLI vorhanden, wird zusätzlich ein GitHub-Release angelegt.
    Die SimConnect-DLLs aus dem MSFS SDK werden NICHT ins ZIP gelegt (Lizenz Microsoft).
.PARAMETER Bump
    Welche Stelle erhöht wird: Major, Minor oder Patch (Standard).
.PARAMETER Version
    Explizite Version (X.Y.Z); überschreibt -Bump.
.PARAMETER Push
    Pusht Commit/Tag und legt (falls gh vorhanden) ein GitHub-Release mit dem ZIP an.
.EXAMPLE
    ./scripts/New-Release.ps1 -Bump Minor -Verbose
.EXAMPLE
    ./scripts/New-Release.ps1 -Version 1.2.0 -Push
#>
[CmdletBinding()]
param(
    [ValidateSet('Major', 'Minor', 'Patch')]
    [string]$Bump = 'Patch',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Push
)

$ErrorActionPreference = 'Stop'

try {
    $repoRoot      = Split-Path -Parent $PSScriptRoot
    $projectDir    = Join-Path (Join-Path $repoRoot 'src') 'InstrumentPanel'
    $projectFile   = Join-Path $projectDir 'InstrumentPanel.csproj'
    $changelogFile = Join-Path $repoRoot 'CHANGELOG.md'
    $distDir       = Join-Path $repoRoot 'dist'
    $utf8NoBom     = [System.Text.UTF8Encoding]::new($false)

    # 1. Arbeitsbaum muss sauber sein, sonst landen fremde Änderungen im Release
    $dirty = git -C $repoRoot status --porcelain
    if ($dirty) { throw "Arbeitsbaum ist nicht sauber - erst committen oder verwerfen:`n$dirty" }

    # 2. Neue Version bestimmen
    $projectXml = [System.IO.File]::ReadAllText($projectFile)
    if ($projectXml -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw 'Keine <Version> in der csproj gefunden.' }
    $current = [int[]]@($Matches[1], $Matches[2], $Matches[3])
    if ($Version) {
        $newVersion = $Version
    } else {
        switch ($Bump) {
            'Major' { $current = @(($current[0] + 1), 0, 0) }
            'Minor' { $current = @($current[0], ($current[1] + 1), 0) }
            'Patch' { $current = @($current[0], $current[1], ($current[2] + 1)) }
        }
        $newVersion = $current -join '.'
    }
    $tag = "v$newVersion"
    if (git -C $repoRoot tag --list $tag) { throw "Tag $tag existiert bereits." }
    Write-Verbose "Neue Version: $newVersion"

    # 3. csproj und CHANGELOG fortschreiben
    $projectXml = $projectXml -replace '<Version>[\d\.]+</Version>', "<Version>$newVersion</Version>"
    [System.IO.File]::WriteAllText($projectFile, $projectXml, $utf8NoBom)

    $changelog = [System.IO.File]::ReadAllText($changelogFile)
    if ($changelog -notmatch '## \[Unreleased\]') { throw 'CHANGELOG.md enthält keinen Abschnitt [Unreleased].' }
    $today = Get-Date -Format 'yyyy-MM-dd'
    $changelog = $changelog -replace '## \[Unreleased\]', "## [Unreleased]`n`n## [$newVersion] - $today"
    [System.IO.File]::WriteAllText($changelogFile, $changelog, $utf8NoBom)

    # 4. Bauen und ZIP packen
    Write-Verbose 'Baue Release...'
    dotnet build (Join-Path $projectDir 'InstrumentPanel.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build ist fehlgeschlagen.' }

    $outputDir = Join-Path (Join-Path (Join-Path $projectDir 'bin') 'Release') 'net472'
    $stagingDir = Join-Path $distDir "InstrumentPanel-$tag"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    # Nur Programmdateien + Standard-Konfiguration; keine persönlichen Einstellungen, keine SDK-DLLs
    Copy-Item (Join-Path $outputDir 'InstrumentPanel.exe*') $stagingDir
    Copy-Item (Join-Path $projectDir 'layout.json') $stagingDir
    Copy-Item (Join-Path $projectDir 'settings.json') $stagingDir
    Copy-Item (Join-Path $repoRoot 'LICENSE') $stagingDir

    $zipPath = Join-Path $distDir "InstrumentPanel-$tag.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath
    Write-Verbose "ZIP erstellt: $zipPath"

    # 5. Commit und Tag
    git -C $repoRoot add CHANGELOG.md $projectFile
    git -C $repoRoot commit -m "Release $tag" -m 'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>'
    git -C $repoRoot tag -a $tag -m "Release $tag"

    # 6. Optional veröffentlichen
    if ($Push) {
        git -C $repoRoot push
        git -C $repoRoot push origin $tag
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            gh release create $tag $zipPath --title $tag --generate-notes
        } else {
            Write-Warning "gh-CLI nicht gefunden - Release bitte manuell auf GitHub anlegen und $zipPath anhängen."
        }
    } else {
        Write-Host "Fertig: $tag lokal erstellt. Mit 'git push; git push origin $tag' veröffentlichen."
    }
}
catch {
    Write-Error "Release fehlgeschlagen: $($_.Exception.Message)"
    exit 1
}
