# MSFS2024 InstrumentPanel – Projektdokumentation

## Überblick

Rahmenlose, transparente, immer-im-Vordergrund WPF-Anwendung (.NET 4.7.2 / C#) für Windows, die klassische Rundinstrumente aus Microsoft Flight Simulator 2024 per SimConnect abgreift und auf einem (meist zweiten) Monitor als Overlay anzeigt. Gedacht für Simulator-Cockpits (z. B. AirSimRig/MobiFlight-Setup), bei denen physische oder virtuelle Zusatz-Instrumente neben dem eigentlichen Sim-Fenster sinnvoll sind.

**Fliegerischer Hintergrund:** Entwickelt mit Bezug auf eine FK9 Mark4 SPL (Basis der Kalibrierungen für EGT/Öldruck/-temperatur).

## Architektur

```
InstrumentPanel/
├── App.xaml/.cs                   – Mehrfenster-Bootstrapper, eine gemeinsame SimConnectService-Instanz
├── MainWindow.xaml/.cs            – Fenster pro layout.json-Eintrag, Grid-Aufbau aus cells[], Live-Reload
├── IGauge.cs                      – Interface: Initialize(SimConnectService), UpdateStatus()
├── SimConnectService.cs           – zentrale SimConnect-Verbindung, Debug-Werte-Tracking
├── AppSettings.cs                 – lädt/speichert settings.json
├── DebugLog.cs                    – zentrale Logging-Klasse
├── GaugeDrawing.cs                – gemeinsame Zeichen-Hilfsfunktionen (PointOnCircle, AddCenteredText, …)
├── WindowBackgroundBrushes.cs     – Fenster-Hintergrundmuster (Alu, Carbon, Vignette, eigenes Bild)
├── LayoutDesignerWindow.xaml/.cs  – Setup-Fenster: Layout-Editor, Einstellungen, Debug-Werte (3 Reiter)
├── [Gauge-Controls]               – je eine .xaml + .xaml.cs pro Instrument (siehe unten)
├── layout.json                    – Fenster-/Zellen-Konfiguration
└── settings.json                  – Anwendungseinstellungen
```

### Gemeinsames Geometrie-Grundgerüst

Alle Rundinstrumente teilen sich dieselbe Bezel-Geometrie: Canvas 300×300, `CenterX=CenterY=150`, `OuterRadius=132` (3 konzentrische Kreise: Außenring, Innenring, Face). Zweiteilige Anzeigen (zwei Halbskalen nebeneinander, z. B. Fuel, EGT/Flow, Oil Temp/Press) nutzen zusätzlich:

- `ArcCenterOffset=28,5`, `ArcRadius=81,6` – Mittelpunkt und Radius jeder Halbskala
- Zwei Varianten für den Zeiger-Drehpunkt:
  - **Fuel-Original-Stil**: Drehpunkt ≠ Skalen-Mittelpunkt (`NeedlePivotOffset=ArcCenterOffset+21`), Zeiger-Winkel wird per **ATAN2** vom Drehpunkt zum tatsächlichen Zielpunkt auf der Skala berechnet – notwendig, weil Drehpunkt und Skalen-Mittelpunkt geometrisch auseinanderfallen.
  - **EGT/Flow-Stil**: Drehpunkt = Skalen-Mittelpunkt (vereinfacht die Formel, da kein ATAN2-Umweg nötig ist).
- `DrawSideSegment` – dunkle Kreissegmente links/rechts, erzeugen den charakteristischen "Doppel-Rundinstrument"-Look und verdecken Zeiger-Enden an den Rändern sauber.

### Stückweise lineare Kalibrierung

Mehrere echte Flugzeug-Skalen sind **nicht linear** über den vollen Winkel verteilt (Beschriftungen sitzen gleichmäßig über den Bogen, obwohl die Werte selbst ungleiche Abstände haben). Dafür gibt es ein wiederverwendetes Muster: Kalibrierpunkte als `(Wert, Winkel)`-Paare, dazwischen linear interpoliert (`PiecewiseFraction`/`AngleForNeedlePiecewise`). Bewährt bei FLOW-Skala (EGT/Flow) und beiden Skalen von Oil Temp/Press.

## Fertige Anzeigen

| Anzeige | Datei | SimConnect-Variable(n) | Besonderheiten |
|---|---|---|---|
| Airspeed | AirspeedGaugeControl | AIRSPEED INDICATED | — |
| Turn Coordinator | TurnCoordinatorGaugeControl | TURN COORDINATOR BALL, SUCTION PRESSURE | Vakuum-Schwellwert & Ball-Teiler einstellbar |
| Altimeter | AltimeterGaugeControl | INDICATED ALTITUDE, KOHLSMAN SETTING HG | Drehbarer Druckeinstell-Knopf |
| Tachometer | TachometerGaugeControl | GENERAL ENG RPM | SL/5/10-Markierungen |
| Vertical Speed | VerticalSpeedGaugeControl | VERTICAL SPEED | — |
| Heading Indicator | HeadingIndicatorGaugeControl | HEADING INDICATOR, HEADING BUG | 2 Drehknöpfe (Gyro-Drift, Heading Bug), absichtlich gegenläufige optische Rückmeldung beim Gyro-Drift-Knopf (entspricht dem echten Sim-Verhalten) |
| Attitude Indicator | AttitudeIndicatorGaugeControl | ATTITUDE INDICATOR PITCH/BANK | Smoothing nur bei Ausfall/Wiederanlauf |
| Fuel | FuelGaugeControl | FUEL LEFT/RIGHT QUANTITY | Ursprüngliches Zweikreis-Template (separate Drehpunkte) |
| VOR | VorGaugeControl | NAV OBS, CDI, GLIDESLOPE | OBS-Drehknopf, GPS/VLOC, TO/FROM |
| Ruler | RulerGaugeControl | — | Reines Mess-Werkzeug (zwei Lineale), Canvas 300×300 – muss bei künftigen Maßstabsänderungen IMMER synchron zu den anderen Anzeigen bleiben |
| EGT/Fuel Flow | EgtFlowGaugeControl | GENERAL ENG EXHAUST GAS TEMPERATURE:1, ENG FUEL FLOW GPH:1 | Links EGT (°F, Kalibrierung s.u.), rechts Flow (GAL/HR, nicht-linear 0–5 gestaucht); drehbarer, rein visueller "EGT REF"-Referenzzeiger (gelb/rot) mit Anschlägen, Maus-Rad-Unterstützung |
| Oil Temp/Press | OilTempPressGaugeControl | GENERAL ENG OIL TEMPERATURE:1, GENERAL ENG OIL PRESSURE:1 | Fuel-Klon als Ausgangspunkt; beide Skalen stückweise linear kalibriert (5 Messpunkte je Skala), Öldruck-Atmosphärenoffset einstellbar |

### Wichtige Kalibrierungs-Fallstricke (gelernt)

- **Einheiten-Verwechslung Rankine/°F**: `GENERAL ENG EXHAUST GAS TEMPERATURE` liefert Rankine; Debug-Rohwerte sind NICHT automatisch °F. Bei jeder neuen Temperatur-Kalibrierung zuerst die Einheit im Debug-Reiter prüfen.
- **EGT-Skala, `EgtMinF`/`EgtMaxF`**: Das sind die **absoluten Skalenenden** (Index 0 und 19 im 20-Tick-Muster), NICHT die Werte der großen Ticks (die liegen 2 Teilstriche weiter innen, bei Index 2 bzw. 17). Aktuell: EgtMinF=1260,33°F, EgtMaxF=1640,33°F (aus Tick-Werten 1760/2060 Rankine hergeleitet).
- **Öldruck-Offset**: `GENERAL ENG OIL PRESSURE` könnte absolut statt als Gauge-Druck geliefert werden – Standard-Korrektur 14,5 PSI (= 1 bar) abgezogen, einstellbar.
- **Drehpunkt ≠ Skalen-Mittelpunkt**: Sobald beide Punkte auseinanderfallen (Fuel-Original-Stil), muss der Zeiger-Zielradius bei JEDER Tick-Repositionierung mit angepasst werden (ATAN2 zielt sonst auf eine Position, die nicht mehr zu den sichtbaren Ticks passt).

## Einstellungen (settings.json)

```json
{
  "refreshIntervalMs": 50,
  "gaugeCellSize": 375,
  "debugLogging": false,
  "turnCoordinator": { "ballDivisor": 1.0, "vacuumThreshold": 3.0 },
  "oilPressAtmosphericOffsetPsi": 14.5,
  "egtMinF": 1260.33,
  "egtMaxF": 1640.33,
  "windowBackground": { "mode": "none", "imagePath": "" }
}
```

Alle Werte sind über den "Einstellungen"-Reiter im Layout-Designer editierbar (kein manuelles JSON-Bearbeiten nötig).

## Fenster-Hintergrund

Vier wählbare Modi (`WindowBackgroundBrushes.cs`): kein Hintergrund (Standard, transparent), gebürstetes Aluminium, Carbon-Faser, dunkles Panel (Vignette), sowie eigenes Bild (Dateiauswahl im Designer). Liegt als eigene Ebene hinter allen Anzeigen, blockiert keine Klicks.

## Layout-Designer – Kernfunktionen

- **Rekursives Split-Modell**: Jede Fläche lässt sich beliebig oft in Zeilen/Spalten teilen (`SplitNode`-Baum), inkl. "+ Fläche hinzufügen" (übernimmt automatisch das Gewicht der Geschwister, kein Dominanz-Bug).
- **"Breite an Zeilen koppeln"**: Automatische Breitenanpassung geteilter Flächen, damit alle Anzeigen quadratisch bleiben – Referenz ist die am wenigsten unterteilte Fläche (nicht die häufigste), Normalisierung nur beim Umschalten (kein Aufschaukel-Effekt bei wiederholtem Neuzeichnen).
- **Mehrfenster-Unterstützung**: Beliebig viele unabhängige Fenster, Live-Reload ohne Neustart.
- **Debug-Werte-Reiter**: Alle SimConnect-Variablen live (1×/Sekunde), alphabetisch.
- **Mausrad + größerer Klickbereich** bei allen Drehknöpfen (VOR OBS, Heading-Knöpfe, Altimeter, Attitude, EGT REF).

## Build

```powershell
cd InstrumentPanel
dotnet build -c Release
.\bin\Release\net472\InstrumentPanel.exe
```

## Offene Punkte / mögliche nächste Schritte

- Oil Press-Kalibrierung: ein beobachteter Versatz (Rohwert 65→zeigt ~55, 78→~65) konnte mangels zuverlässiger Live-Ablesung nicht bestätigt werden – bleibt vorerst unverändert, Atmosphärendruck als Ursache wurde ausgeschlossen (würde konstanten, nicht linear wachsenden Versatz ergeben).
- Weitere Anzeigen folgen demselben Muster: Fuel-/EGT-Template klonen → SimConnect-Variablen umstellen → Kalibrierpunkte (Wert/Winkel-Paare) ermitteln → ATAN2-Zielradius ggf. anpassen → Feinjustage von Zahlen-/Textpositionen iterativ per Screenshot-Feedback.

---
*Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen, Kalibrierung und Tests durch Gesiima. Lizenz: AGPL-3.0, siehe [LICENSE](../LICENSE).*
