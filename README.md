# MSFS24 InstrumentPanel

Rahmenlose, transparente, immer-im-Vordergrund liegende WPF-Anwendung (.NET Framework 4.7.2, C#), die klassische Rundinstrumente aus **Microsoft Flight Simulator 2024** per SimConnect live darstellt – ideal für einen zweiten Monitor oder ein Cockpit-Setup.

## Funktionen

- Instrumente: Airspeed, Turn Coordinator, Altimeter, Tachometer, Vertical Speed, Heading Indicator, Attitude Indicator, Fuel, VOR, EGT/Fuel Flow, Oil Temp/Press sowie ein Ruler-Messwerkzeug
- Layout-Designer: beliebig viele Fenster, rekursive Zeilen-/Spaltenteilung, Live-Reload ohne Neustart
- Auswählbarer Fensterhintergrund (transparent, Aluminium, Carbon, Panel, eigenes Bild)
- Zwei-Wege-Drehknöpfe (QNH, Heading Bug, Gyro Drift, OBS, …) schreiben zurück in den Simulator
- Debug-Reiter mit allen SimConnect-Werten live

Details: [Projektdokumentation](docs/Projektdokumentation.md) · [Technische Hinweise je Anzeige](docs/Technische-Hinweise.md)

## Voraussetzungen

- Windows 10/11 (x64)
- Microsoft Flight Simulator 2024
- .NET SDK 6.0 oder neuer (nur zum Bauen; die fertige `.exe` braucht nur .NET Framework 4.7.2, das in Windows enthalten ist)
- **MSFS SDK** (für die SimConnect-Dateien; aus Lizenzgründen *nicht* im Repository):
  1. MSFS 2024 → Optionen → Allgemein → Entwicklermodus aktivieren
  2. Im Entwicklermenü das SDK installieren
  3. Aus `SimConnect SDK\lib` zwei Dateien nach `src/InstrumentPanel/lib/` kopieren:
     - `managed\Microsoft.FlightSimulator.SimConnect.dll`
     - `SimConnect.dll`

## Bauen

```powershell
cd src/InstrumentPanel
dotnet build -c Release
./bin/Release/net472/InstrumentPanel.exe
```

## Verwendung

1. MSFS 2024 starten und einen Flug beginnen.
2. `InstrumentPanel.exe` starten – die App verbindet sich automatisch (Neuversuch alle 5 s).
3. Fenster per Ziehen verschieben, an Rand/Ecke skalieren; Setup über den Layout-Designer.

Konfiguration liegt neben der `.exe`: `layout.json` (Fenster/Zellen) und `settings.json` (Aktualisierungsrate, Kalibrierung, Hintergrund). Beide sind im Designer editierbar; Vorlagen mit Standardwerten liegen in [src/InstrumentPanel/](src/InstrumentPanel/).

## Projektstruktur

```
src/InstrumentPanel/
├── Gauges/            je Instrument eine XAML + Code-behind
├── App, MainWindow, LayoutDesignerWindow, SimConnectService, ...
├── icon/              Anwendungs-Icon
├── lib/               MSFS-SDK-DLLs (lokal, nicht versioniert)
├── layout.json        Standard-Layout
└── settings.json      Standard-Einstellungen
docs/                  Projekt- und Technikdokumentation
```

## Versionen und Releases

- Versionsnummer: `<Version>` in `InstrumentPanel.csproj` (SemVer), Release = Git-Tag `vX.Y.Z`.
- Build-Ausgaben (`bin/`, `obj/`) werden **nicht** eingecheckt, sondern als ZIP unter [Releases](https://github.com/Gesiima/MSFS24-InstrumentPanel/releases) veröffentlicht.