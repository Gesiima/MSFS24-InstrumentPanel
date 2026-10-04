#Requires -Version 7.0
<#
.SYNOPSIS
    Erzeugt das Anwendungs-Icon (icon/app.ico) mit allen Windows-Größen neu.
.DESCRIPTION
    Zeichnet das Rundinstrument-Symbol (Bezel, schwarzes Zifferblatt, Strichmarkierungen,
    roter Zeiger, Nabe) für jede Größe einzeln mit Kantenglättung - so bleiben auch die
    kleinen Größen (16/24/32 px) lesbar, statt eine große Grafik herunterzuskalieren.
    Enthaltene Größen: 16, 24, 32, 48, 64, 128, 256 px (256 px als PNG, kleinere als klassische BMP-Ebenen im ICO-Container).
    Zusätzlich wird icon/app_256.png als Vorschau geschrieben.
    Hinweis: Das Skript nutzt System.Drawing und läuft deshalb nur unter Windows.
.PARAMETER OutputDirectory
    Zielordner (Standard: src/InstrumentPanel/icon). Relative Pfade werden gegen das aktuelle
    Arbeitsverzeichnis aufgelöst; fehlt der Ordner, wird er angelegt.
.EXAMPLE
    ./scripts/New-AppIcon.ps1 -Verbose
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'src') 'InstrumentPanel') 'icon')
)

$ErrorActionPreference = 'Stop'

# System.Drawing (GDI+) steht nur unter Windows zur Verfügung
if (-not $IsWindows) { throw 'New-AppIcon.ps1 läuft nur unter Windows (System.Drawing).' }
Add-Type -AssemblyName System.Drawing

# Zeichnet das Icon in der gewünschten Kantenlänge und liefert das Bitmap (Aufrufer gibt es frei)
function New-IconPng {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    # Vorab auf $null, damit das finally nur Objekte freigibt, die auch erzeugt wurden
    $bezelOuter = $null; $bezelInner = $null; $face = $null
    $tickPen = $null; $needlePen = $null; $hub = $null
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $center = $Size / 2.0
        $outerRadius = $Size / 2.0 - [Math]::Max(0.5, $Size * 0.02)

        # Bezel (außen hellgrau, innen dunkler) und schwarzes Zifferblatt
        $bezelOuter = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 62, 62, 66))
        $bezelInner = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 34, 34, 36))
        $face = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::Black)
        $graphics.FillEllipse($bezelOuter, $center - $outerRadius, $center - $outerRadius, 2 * $outerRadius, 2 * $outerRadius)
        $innerRadius = $outerRadius * 0.90
        $graphics.FillEllipse($bezelInner, $center - $innerRadius, $center - $innerRadius, 2 * $innerRadius, 2 * $innerRadius)
        $faceRadius = $outerRadius * 0.80
        $graphics.FillEllipse($face, $center - $faceRadius, $center - $faceRadius, 2 * $faceRadius, 2 * $faceRadius)

        # Strichmarkierungen: bei kleinen Größen nur die vier Hauptstriche, sonst alle 12
        $tickStep = if ($Size -lt 32) { 90 } else { 30 }
        $tickWidth = [Math]::Max(1.0, $Size * 0.03)
        $tickPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [float]$tickWidth)
        for ($angle = 0; $angle -lt 360; $angle += $tickStep) {
            $radians = ($angle - 90) * [Math]::PI / 180
            $r1 = $faceRadius * 0.80
            $r2 = $faceRadius * 0.97
            $graphics.DrawLine($tickPen,
                [float]($center + $r1 * [Math]::Cos($radians)), [float]($center + $r1 * [Math]::Sin($radians)),
                [float]($center + $r2 * [Math]::Cos($radians)), [float]($center + $r2 * [Math]::Sin($radians)))
        }

        # Roter Zeiger (kräftig genug, damit er auch bei 16 px sichtbar bleibt) und Nabe
        $needleWidth = [Math]::Max(1.5, $Size * 0.05)
        $needlePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 230, 40, 40), [float]$needleWidth)
        $needlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $needleAngle = (-50 - 90) * [Math]::PI / 180
        $needleLength = $faceRadius * 0.85
        $graphics.DrawLine($needlePen, [float]$center, [float]$center,
            [float]($center + $needleLength * [Math]::Cos($needleAngle)), [float]($center + $needleLength * [Math]::Sin($needleAngle)))
        $hubRadius = [Math]::Max(1.5, $Size * 0.09)
        $hub = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 220, 220, 220))
        $graphics.FillEllipse($hub, $center - $hubRadius, $center - $hubRadius, 2 * $hubRadius, 2 * $hubRadius)
    }
    finally {
        foreach ($disposable in $bezelOuter, $bezelInner, $face, $tickPen, $needlePen, $hub, $graphics) {
            if ($disposable) { $disposable.Dispose() }
        }
    }

    return $bitmap
}

# PNG-Bytes eines Bitmaps (für die 256-px-Ebene und die Vorschau)
function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $stream = [System.IO.MemoryStream]::new()
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

# Klassisches ICO-Bild (DIB, 32 bpp BGRA + AND-Maske): von allen Windows-Komponenten lesbar,
# PNG-Einträge sind bei kleinen Größen nicht überall zuverlässig.
function ConvertTo-IconDibBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $size = $Bitmap.Width
    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = [byte[]]::new($data.Stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $maskRowBytes = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    # BITMAPINFOHEADER: Höhe = 2x (Farbdaten + Maske)
    $writer.Write([uint32]40); $writer.Write([int32]$size); $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
    $writer.Write([uint32]($data.Stride * $size)); $writer.Write([int32]0); $writer.Write([int32]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    # Pixelzeilen von unten nach oben
    for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels, $row * $data.Stride, $size * 4) }
    # AND-Maske komplett 0 (Transparenz kommt aus dem Alpha-Kanal)
    $writer.Write([byte[]]::new($maskRowBytes * $size))
    $writer.Flush()
    return , $stream.ToArray()
}

try {
    # Relativen Zielordner gegen das aktuelle Arbeitsverzeichnis auflösen (.NET kennt das PowerShell-Verzeichnis nicht)
    $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        Write-Verbose "Lege Zielordner an: $OutputDirectory"
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    }

    $sizes = 16, 24, 32, 48, 64, 128, 256
    $imageBySize = @{}   # Hashtable (kein [ordered]: dort würde ein int-Schlüssel als Position gelesen)
    foreach ($size in $sizes) {
        Write-Verbose "Zeichne ${size}x${size}"
        $bitmap = New-IconPng -Size $size
        try {
            # Nur die 256-px-Ebene als PNG (Windows-Standard), alle kleineren als DIB
            $imageBySize[$size] = if ($size -ge 256) { ConvertTo-PngBytes -Bitmap $bitmap } else { ConvertTo-IconDibBytes -Bitmap $bitmap }
            if ($size -eq 256) { $previewPng = ConvertTo-PngBytes -Bitmap $bitmap }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    # ICO-Container: Header (6 Byte) + je Bild ein Verzeichniseintrag (16 Byte) + Bilddaten (256 px PNG, kleinere DIB)
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $dimensionByte = if ($size -ge 256) { [byte]0 } else { [byte]$size }   # 0 bedeutet 256
        $writer.Write($dimensionByte); $writer.Write($dimensionByte)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$imageBySize[$size].Length); $writer.Write([uint32]$offset)
        $offset += $imageBySize[$size].Length
    }
    foreach ($size in $sizes) { $writer.Write($imageBySize[$size]) }
    $writer.Flush()

    [System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'app.ico'), $stream.ToArray())
    [System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'app_256.png'), $previewPng)
    Write-Verbose "Icon geschrieben nach $OutputDirectory"
}
catch {
    # Beendet das Skript mit Exitcode ungleich 0 (statt Write-Error + totem "exit 1")
    $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
        [System.Exception]::new("Icon-Erzeugung fehlgeschlagen: $($_.Exception.Message)"),
        'IconCreationFailed',
        [System.Management.Automation.ErrorCategory]::OperationStopped,
        $null))
}
