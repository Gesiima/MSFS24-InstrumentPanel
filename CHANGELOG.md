# Changelog

Alle nennenswerten Änderungen. Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
Versionierung nach [SemVer](https://semver.org/lang/de/) (`MAJOR.MINOR.PATCH`).

## [Unreleased]

### Hinzugefügt
- Einstellung `attitudeVacuumThreshold` (Standard 3.0 inHg): eigene Vakuum-Schwelle für den künstlichen Horizont, im Setup-Reiter „Einstellungen“ editierbar. `0` ignoriert das Vakuum (z. B. für elektrische Kreisel). Bisher teilte sich der Horizont die Schwelle des Turn Coordinators.
- Setup-Fenster: Rückfrage bei ungespeicherten Änderungen (Fensterwechsel, neues Fenster, Leeren, Schließen) und beim Löschen eines Fensters.

### Behoben
- Nach einem MSFS-Neustart wirkten die Drehknöpfe (QNH, Heading Bug, Gyro-Drift, OBS, Nick-Trimm) nicht mehr, weil die Client-Events nicht neu gemappt wurden.
- Registrierungen von Instrumenten wurden bei jedem Layout-Reload und beim Schließen von Fenstern nie abgemeldet (wachsende CPU-Last, Geisterdaten). Fenster melden ihre Instrumente jetzt beim Neuaufbau und Schließen ab.
- Eine Exception in einem Instrument (z. B. durch NaN) riss die gesamte SimConnect-Verbindung ab („Verbindung verloren“); NaN/Unendlich-Werte werden jetzt verworfen, Fehler im Log festgehalten.
- Reconnect: kein zweites SimConnect-Objekt mehr bei verzögertem Verbindungsaufbau, Reconnect-Timer läuft auch nach unbekannten Fehlern weiter, „MSFS beendet“ wird nicht mehr durch „Verbindung verloren“ überschrieben, neue Fenster zeigen sofort den aktuellen Verbindungsstatus.
- VOR: Kompassscheibe lief nach mehr als 1,5 Umdrehungen gegen den Uhrzeigersinn kurz den falschen Weg (Modulo mit negativen Werten).
- Setup: gekoppelte Spaltenbreiten wurden beim Neuladen verzerrt (z. B. 2:3:3 wurde 8:3:3) und so beim Speichern verändert; Kopplung wird nicht mehr fälschlich erkannt, gemeinsame Referenz bei mehreren gekoppelten Spalten, Kopplung nur bei Spalten-Aufteilung.
- Setup: Dezimalkomma wurde als Tausendertrenner gelesen („14,5“ wurde 145). Zahlenfelder werden jetzt geprüft (Wertebereich, ungültige Zahl, feldbezogene Meldung), der Einstellungen-Reiter zeigt nach dem Speichern die gespeicherten Werte.
- Setup: Aufteilen-Anzahl begrenzt, defekte oder von Hand bearbeitete `layout.json` lässt das Setup nicht mehr abstürzen, `layout.json`/`settings.json` werden atomar geschrieben, Debug-Werte-Liste springt nicht mehr jede Sekunde.
- Fenster: Index-Verwaltung zwischen Setup und laufenden Fenstern (geschlossene Fenster, Lücken im `windows`-Array), unvollständige Zellen im Layout werden einzeln übersprungen statt das ganze Layout zu verwerfen, Fenstergröße bleibt beim Neuladen erhalten, Schließen-Button wird nicht mehr von der Resize-Ecke überdeckt, App beendet sich bei fehlgeschlagenem Start sauber.
- Einstellungen: ungültige Werte in `settings.json` (Aktualisierungsrate ≤ 0, Zellgröße, Teiler 0, EGT-Skala) führten zu Dauerlast, Division durch 0 oder einem fensterlosen Prozess; jeder Schlüssel wird einzeln gelesen und plausibilisiert.
- Fuel und Oil Temp/Press: Zeiger starteten bei 0 und liefen mehrere Sekunden zum Messwert; sie stehen jetzt sofort richtig.
- Altimeter: negative Höhen zeigten dauerhaft 0; Anzeige von QNH kulturunabhängig.
- Vertical Speed: Zeigersprung bei ±2000 ft/min beseitigt.
- Drehknöpfe (Altimeter, Attitude, Heading, VOR, EGT REF): ein Drag blieb hängen, wenn die Mausaufnahme verloren ging (z. B. Alt-Tab); das Mausrad löst pro 120 Delta einen Schritt aus.
- Hintergrundbild: relative Pfade werden gegen den Programmordner aufgelöst, der Modus ist unabhängig von der Groß-/Kleinschreibung.
- Release-Skript: Exitcodes von git/dotnet/gh werden geprüft, Änderungen erst nach erfolgreichem Build, Rollback bei Fehlern; Icon-Skript löst relative Pfade korrekt auf.

### Geändert
- Debug-Logging baut seine Texte nur noch bei aktiviertem Logging (weniger Last pro Update).
- Tote Schnittstellenmethode `IGauge.UpdateStatus` entfernt; Kommentare in mehreren Instrumenten an den tatsächlichen Code angepasst.
- Icon: `app.ico` enthält jetzt 16, 24, 32, 48, 64, 128 und 256 px (vorher nur 16×16), erzeugt per `scripts/New-AppIcon.ps1`; die unbenutzten Vorschaudateien wurden durch `icon/app_256.png` ersetzt.
## [1.0.0] - 2026-10-04

Erste Veröffentlichung auf GitHub; fasst die Entwicklung bis hierhin zusammen.

### Hinzugefügt
- **Grundgerüst:**
  - Zehn klassische Anzeigen: Airspeed, Turn Coordinator, Altimeter, Tachometer, Vertical Speed, Heading Indicator, Attitude Indicator, Fuel, VOR und Ruler.
  - Mehrfenster-Unterstützung: beliebig viele unabhängige Fenster aus `layout.json`, eine gemeinsame `SimConnectService`-Instanz, Live-Reload ohne Neustart.
  - Layout-Designer mit rekursivem Split-Modell (`SplitNode`-Baum): beliebig tiefe Zeilen-/Spalten-Teilung, drei Reiter (Layout, Einstellungen, Debug-Werte).
- **EGT/Fuel Flow** (`EgtFlowGaugeControl`): auf Fuel-Template-Basis neu gebaut. Links EGT (°F, stückweise linear kalibriert, Redline), rechts Fuel Flow (GAL/HR, 0–5 nicht-linear gestaucht, grünes Band, Extrapolation über die Redline hinaus möglich). Drehbarer, rein visueller „EGT REF"-Referenzzeiger (gelb/rot, Anschläge auf den Skalenbereich begrenzt, Mausrad-Unterstützung, Rastung im halben Tick-Abstand).
- **Oil Temp/Press** (`OilTempPressGaugeControl`): als 1:1-Klon von Fuel gestartet, dann auf eigene Kalibrierung umgebaut (5 Messpunkte je Skala, Skalen-Mittelpunkte unabhängig von den Zeiger-Drehpunkten, Grün-Bänder, Redlines beidseitig bei PRESS).
- **Fenster-Hintergrund** (`WindowBackgroundBrushes.cs`): vier Muster-Presets (gebürstetes Aluminium, Carbon-Faser, dunkles Panel, kein Hintergrund) plus eigenes Bild, als eigene Ebene hinter allen Anzeigen.
- Neue Einstellungen, alle im Designer editierbar:
  - `oilPressAtmosphericOffsetPsi` (Standard 14,5 PSI = 1 bar): Korrektur für `GENERAL ENG OIL PRESSURE`
  - `egtMinF` / `egtMaxF` (Standard 1260,33 / 1640,33): absolute EGT-Skalenenden
  - `windowBackground.mode` / `windowBackground.imagePath`
- Versionierung (Version in der csproj, Anzeige im Setup-Fenster und Debug-Log, Release-Skript `scripts/New-Release.ps1`), Lizenz AGPL-3.0, Dokumentation unter `docs/`.

### Geändert
- Fuel-Anzeige als Referenz-Template auf volle Größe hochskaliert (OuterRadius 88 → 132, Canvas 200×200 → 300×300); dient als Vorlage für EGT/Flow und Oil Temp/Press.
- Layout-Designer: „Angepasst"-Erkennung auf Verhältnis- statt Absolutwert-Basis umgestellt.
- Klickbereich aller Drehknöpfe (VOR OBS, Heading-Knöpfe, Altimeter, Attitude, EGT REF) auf das 1,5- bis 2,5-fache der sichtbaren Größe vergrößert.
- Cursor bei allen Drehknöpfen auf „Hand" umgestellt.
- Mausrad-Unterstützung für den EGT-REF-Knopf ergänzt (bei den anderen Knöpfen schon vorhanden).
- Reaktionsgeschwindigkeit: EGT 200 → 240 °F/s, Fuel Flow 10 → 12 GAL/HR/s (je +20 %); Oil Temp/Press 15 → 18 Einheiten/s (gemeinsame Rate beider Skalen).
- Projektdokumentation erstellt; Flugzeugbezug korrigiert (Cessna 172, nicht FK9 Mark4).

### Behoben
- VOR-Anzeige: Bezel-Dicke war nur halb so dick wie bei den anderen Anzeigen.
- Layout-Designer: leere Layout-Felder gingen beim Neuladen verloren (werden jetzt explizit als `"(leer)"` gespeichert).
- Layout-Designer: Fenstergrößen-Berechnung bei ungleichen Spaltenbreiten korrigiert.
- Layout-Designer, „Breite an Zeilen koppeln": Referenz war fälschlich die häufigste statt die am wenigsten unterteilte Geschwister-Fläche.
- Layout-Designer, Aufschaukel-Effekt: Geschwister-Normalisierung lief bei jedem Neuzeichnen erneut statt nur beim Einschalten (getrennt in `ApplyAutoWidth` einmalig und `RefreshAutoWidth` laufend).
- Layout-Designer, Normal-Referenz-Kontamination: bereits gekoppelte Geschwister flossen in die Berechnung „was ist Normal" ein, jetzt ausgeschlossen.
- Layout-Designer, „+ Fläche hinzufügen": neue Fläche bekam immer Gewicht 12 statt das der Geschwister und dominierte bei abweichendem Gewicht komplett.
- Ruler-Maßstab: war auf der alten 200×200-Canvas-Größe stehen geblieben, nachdem Fuel & Co. auf 300×300 hochskaliert wurden.
- Cursor-Bug: bei vier Anzeigen saß der Cursor am falschen, verdeckten Element und wurde nie sichtbar.
