# Changelog

Alle nennenswerten Änderungen. Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
Versionierung nach [SemVer](https://semver.org/lang/de/) (`MAJOR.MINOR.PATCH`).

## [Unreleased]

## [1.0.0] - 2026-10-04

Erste Veröffentlichung auf GitHub; fasst die Entwicklung bis hierhin zusammen.

### Hinzugefügt
- **EGT/Fuel Flow** (`EgtFlowGaugeControl`): auf Fuel-Template-Basis neu gebaut. Links EGT (°F, stückweise linear kalibriert, Redline), rechts Fuel Flow (GAL/HR, 0–5 nicht-linear gestaucht, grünes Band, Extrapolation über die Redline hinaus möglich). Drehbarer, rein visueller „EGT REF"-Referenzzeiger (gelb/rot, Anschläge auf den Skalenbereich begrenzt, Mausrad-Unterstützung, Rastung im halben Tick-Abstand).
- **Oil Temp/Press** (`OilTempPressGaugeControl`): als 1:1-Klon von Fuel gestartet, dann auf eigene Kalibrierung umgebaut (5 Messpunkte je Skala, Skalen-Mittelpunkte unabhängig von den Zeiger-Drehpunkten, Grün-Bänder, Redlines beidseitig bei PRESS).
- **Fenster-Hintergrund** (`WindowBackgroundBrushes.cs`): vier Muster-Presets (gebürstetes Aluminium, Carbon-Faser, dunkles Panel, kein Hintergrund) plus eigenes Bild, als eigene Ebene hinter allen Anzeigen.
- Neue Einstellungen, alle im Designer editierbar:
  - `oilPressAtmosphericOffsetPsi` (Standard 14,5 PSI = 1 bar): Korrektur für `GENERAL ENG OIL PRESSURE`
  - `egtMinF` / `egtMaxF` (Standard 1260,33 / 1640,33): absolute EGT-Skalenenden
  - `windowBackground.mode` / `windowBackground.imagePath`
- Versionierung (Version in der csproj, Anzeige im Setup-Fenster und Debug-Log, Release-Skript `scripts/New-Release.ps1`), Lizenz AGPL-3.0, Dokumentation unter `docs/`.

### Geändert
- Klickbereich aller Drehknöpfe (VOR OBS, Heading-Knöpfe, Altimeter, Attitude, EGT REF) auf das 1,5- bis 2,5-fache der sichtbaren Größe vergrößert.
- Cursor bei allen Drehknöpfen auf „Hand" umgestellt.
- Mausrad-Unterstützung für den EGT-REF-Knopf ergänzt (bei den anderen Knöpfen schon vorhanden).
- Reaktionsgeschwindigkeit: EGT 200 → 240 °F/s, Fuel Flow 10 → 12 GAL/HR/s (je +20 %); Oil Temp/Press 15 → 18 Einheiten/s (gemeinsame Rate beider Skalen).
- Projektdokumentation erstellt; Flugzeugbezug korrigiert (Cessna 172, nicht FK9 Mark4).

### Behoben
- Layout-Designer, „Breite an Zeilen koppeln": Referenz war fälschlich die häufigste statt die am wenigsten unterteilte Geschwister-Fläche.
- Layout-Designer, Aufschaukel-Effekt: Geschwister-Normalisierung lief bei jedem Neuzeichnen erneut statt nur beim Einschalten (getrennt in `ApplyAutoWidth` einmalig und `RefreshAutoWidth` laufend).
- Layout-Designer, Normal-Referenz-Kontamination: bereits gekoppelte Geschwister flossen in die Berechnung „was ist Normal" ein, jetzt ausgeschlossen.
- Layout-Designer, „+ Fläche hinzufügen": neue Fläche bekam immer Gewicht 12 statt das der Geschwister und dominierte bei abweichendem Gewicht komplett.
- Ruler-Maßstab: war auf der alten 200×200-Canvas-Größe stehen geblieben, nachdem Fuel & Co. auf 300×300 hochskaliert wurden.
- Cursor-Bug: bei vier Anzeigen saß der Cursor am falschen, verdeckten Element und wurde nie sichtbar.
