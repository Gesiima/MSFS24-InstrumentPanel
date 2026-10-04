#Requires -Version 7.0
<#
.SYNOPSIS
    Erstellt ein Release: Version erhöhen, CHANGELOG fortschreiben, bauen, ZIP packen, committen, taggen.
.DESCRIPTION
    Ablauf:
      1. Prüft, dass der Git-Arbeitsbaum sauber ist.
      2. Bestimmt die neue Version (muss größer als die aktuelle sein) und prüft, dass das Tag lokal
         (mit -Push auch auf origin) noch nicht existiert.
      3. Baut im Release-Modus (mit der neuen Version) und packt dist/InstrumentPanel-vX.Y.Z.zip.
      4. Erst nach erfolgreichem Build: setzt <Version> in src/InstrumentPanel/InstrumentPanel.csproj
         und überführt den Abschnitt [Unreleased] im CHANGELOG.md in die neue Version.
      5. Committet "Release vX.Y.Z" und setzt das Tag vX.Y.Z.
      6. Mit -Push: pusht Commit und Tag; ist die gh-CLI vorhanden, wird zusätzlich ein GitHub-Release angelegt.
    Nach jedem Aufruf von git, dotnet und gh wird der Exitcode geprüft; bei einem Fehler bricht das Skript
    mit Exitcode ungleich 0 ab. Wurden csproj/CHANGELOG schon geändert, aber noch nicht committet,
    werden sie per "git checkout" zurückgesetzt.
    Die SimConnect-DLLs aus dem MSFS SDK werden NICHT ins ZIP gelegt (Lizenz Microsoft).
.PARAMETER Bump
    Welche Stelle erhöht wird: Major, Minor oder Patch (Standard).
.PARAMETER Version
    Explizite Version (X.Y.Z); überschreibt -Bump und muss größer als die aktuelle Version sein.
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

# Bricht ab, wenn der zuletzt ausgeführte native Befehl (git, dotnet, gh) einen Fehler-Exitcode lieferte
function Assert-ExitCode {
    param([Parameter(Mandatory)][string]$Description)
    if ($LASTEXITCODE -ne 0) { throw "$Description ist fehlgeschlagen (Exitcode $LASTEXITCODE)." }
}

# Zustand für den Rollback im Fehlerfall
$filesModified = $false
$committed     = $false

try {
    $repoRoot      = Split-Path -Parent $PSScriptRoot
    $projectDir    = Join-Path (Join-Path $repoRoot 'src') 'InstrumentPanel'
    $projectFile   = Join-Path $projectDir 'InstrumentPanel.csproj'
    $changelogFile = Join-Path $repoRoot 'CHANGELOG.md'
    $distDir       = Join-Path $repoRoot 'dist'
    $utf8NoBom     = [System.Text.UTF8Encoding]::new($false)

    # 1. Arbeitsbaum muss sauber sein, sonst landen fremde Änderungen im Release
    $dirty = git -C $repoRoot status --porcelain
    Assert-ExitCode 'git status'
    if ($dirty) { throw "Arbeitsbaum ist nicht sauber - erst committen oder verwerfen:`n$($dirty -join "`n")" }

    # 2. Neue Version bestimmen
    $projectXml = [System.IO.File]::ReadAllText($projectFile)
    if ($projectXml -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw 'Keine <Version> in der csproj gefunden.' }
    $current = [int[]]@($Matches[1], $Matches[2], $Matches[3])
    $currentVersion = $current -join '.'
    if ($Version) {
        $newVersion = $Version
        # Nur aufwärts: eine gleiche oder kleinere Version wäre ein Rückschritt
        if ([version]$newVersion -le [version]$currentVersion) {
            throw "Die Version $newVersion muss größer sein als die aktuelle Version $currentVersion."
        }
    } else {
        switch ($Bump) {
            'Major' { $current = @(($current[0] + 1), 0, 0) }
            'Minor' { $current = @($current[0], ($current[1] + 1), 0) }
            'Patch' { $current = @($current[0], $current[1], ($current[2] + 1)) }
        }
        $newVersion = $current -join '.'
    }
    $tag = "v$newVersion"

    $localTag = git -C $repoRoot tag --list $tag
    Assert-ExitCode 'git tag --list'
    if ($localTag) { throw "Tag $tag existiert bereits." }
    if ($Push) {
        # Auch auf dem Remote prüfen, sonst scheitert erst der Push am Ende
        $remoteTag = git -C $repoRoot ls-remote --tags origin $tag
        Assert-ExitCode 'git ls-remote'
        if ($remoteTag) { throw "Tag $tag existiert bereits auf origin." }
    }
    Write-Verbose "Neue Version: $newVersion (bisher $currentVersion)"

    # 3. Bauen (die neue Version kommt per Property, die Dateien bleiben bis zum erfolgreichen Build unverändert)
    Write-Verbose 'Baue Release...'
    $buildVerbosity = if ($VerbosePreference -eq 'Continue') { 'normal' } else { 'quiet' }
    dotnet build $projectFile -c Release --nologo -v $buildVerbosity "-p:Version=$newVersion"
    Assert-ExitCode 'dotnet build'

    $outputDir = Join-Path (Join-Path (Join-Path $projectDir 'bin') 'Release') 'net472'
    if (-not (Test-Path (Join-Path $outputDir 'InstrumentPanel.exe'))) { throw "Build-Ergebnis nicht gefunden: $outputDir" }
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

    # 4. Erst jetzt csproj und CHANGELOG fortschreiben
    $changelog = [System.IO.File]::ReadAllText($changelogFile)
    if ($changelog -notmatch '## \[Unreleased\]') { throw 'CHANGELOG.md enthält keinen Abschnitt [Unreleased].' }
    # Zeilenenden der Datei übernehmen (CRLF oder LF)
    $eol = if ($changelog.Contains("`r`n")) { "`r`n" } else { "`n" }
    $today = Get-Date -Format 'yyyy-MM-dd'

    $filesModified = $true
    $versionPattern = [regex]'<Version>[\d\.]+</Version>'
    $projectXml = $versionPattern.Replace($projectXml, "<Version>$newVersion</Version>", 1)
    [System.IO.File]::WriteAllText($projectFile, $projectXml, $utf8NoBom)

    $unreleasedPattern = [regex]'## \[Unreleased\]'
    $changelog = $unreleasedPattern.Replace($changelog, "## [Unreleased]$eol$eol## [$newVersion] - $today", 1)
    [System.IO.File]::WriteAllText($changelogFile, $changelog, $utf8NoBom)

    # 5. Commit und Tag
    git -C $repoRoot add CHANGELOG.md $projectFile
    Assert-ExitCode 'git add'
    git -C $repoRoot commit -m "Release $tag" -m 'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>'
    Assert-ExitCode 'git commit'
    $committed = $true
    git -C $repoRoot tag -a $tag -m "Release $tag"
    Assert-ExitCode 'git tag'

    # 6. Optional veröffentlichen
    if ($Push) {
        git -C $repoRoot push
        Assert-ExitCode 'git push'
        git -C $repoRoot push origin $tag
        Assert-ExitCode 'git push (Tag)'
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            gh release create $tag $zipPath --title $tag --generate-notes
            Assert-ExitCode 'gh release create'
        } else {
            Write-Warning "gh-CLI nicht gefunden - Release bitte manuell auf GitHub anlegen und $zipPath anhängen."
        }
    } else {
        Write-Host "Fertig: $tag lokal erstellt. Mit 'git push; git push origin $tag' veröffentlichen."
    }
}
catch {
    $errorMessage = $_.Exception.Message

    # Noch nicht committete Änderungen an csproj/CHANGELOG zurücknehmen
    if ($filesModified -and -not $committed) {
        try {
            git -C $repoRoot checkout -- $projectFile $changelogFile
            if ($LASTEXITCODE -eq 0) { Write-Warning 'csproj und CHANGELOG.md wurden zurückgesetzt.' }
            else { Write-Warning 'Rollback von csproj/CHANGELOG.md fehlgeschlagen - bitte "git status" prüfen.' }
        }
        catch {
            Write-Warning "Rollback von csproj/CHANGELOG.md fehlgeschlagen: $($_.Exception.Message)"
        }
    }

    # Beendet das Skript mit Exitcode ungleich 0 (statt Write-Error + totem "exit 1")
    $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
        [System.Exception]::new("Release fehlgeschlagen: $errorMessage"),
        'ReleaseFailed',
        [System.Management.Automation.ErrorCategory]::OperationStopped,
        $null))
}
