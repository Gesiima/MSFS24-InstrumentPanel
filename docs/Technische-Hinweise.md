# Technische Hinweise zu einzelnen Anzeigen

Übernommen aus der früheren README. Teilweise historisch (ursprünglich Cessna-172-Fokus); Übersicht und aktueller Stand: [Projektdokumentation](Projektdokumentation.md).

## Anpassung der V-Geschwindigkeiten (Fahrtmesser)

Die Farbbögen (weiß/grün/gelb) und die rote Vne-Linie basieren auf typischen
Werten einer Cessna 172S (KIAS). Falls dein Flugzeug-Setup im Simulator davon
abweicht, lassen sich die Werte am Anfang von `AirspeedGaugeControl.xaml.cs`
anpassen:

```csharp
private const double VFE = 85;   // weißer Bogen: Ende (max. Klappengeschwindigkeit)
private const double VS1 = 44;   // grüner Bogen: Anfang (Stall, sauber)
private const double VNO = 129;  // grüner Bogen: Ende / gelber Bogen: Anfang
private const double VNE = 163;  // rote Linie (nie überschreiten)
```

## Turn Coordinator - technischer Hinweis

Die Neigung des Flugzeug-Symbols basiert auf der direkten SimConnect-Variable
`TURN INDICATOR RATE` (Wenderate in Grad/Sekunde) - **nicht** auf dem
tatsächlichen Roll-Winkel (das wäre der künstliche Horizont). Das ist exakt
das reale Funktionsprinzip: Standard-Kurve (3°/s) entspricht einer
Symbol-Neigung von 20°. Die Kugel kommt aus `TURN COORDINATOR BALL`
(SimConnect-Einheit "position", Bereich -127 bis 127, wird intern auf -1..1
normalisiert).

Die Klapp-Anzeige (ON/OFF) basiert auf `SUCTION PRESSURE` (Vakuum-Systemdruck
in inHg) - der Kreisel der C172 wird vom Vakuumsystem angetrieben, das seinerseits
von der Motordrehzahl abhängt. Fällt der Druck unter den in `settings.json`
konfigurierbaren Schwellwert (`turnCoordinator.vacuumThreshold`, Standard 3.0
inHg), zeigt die Anzeige OFF und das Flugzeug-Symbol springt auf Vollausschlag
links. Andere naheliegende Variablen (`TURN INDICATOR SWITCH`, `BREAKER
TURNCOORD`, `ELECTRICAL AVIONICS BUS VOLTAGE`) wurden getestet, reagierten aber
nachweislich nicht auf einen Motor-Aus/An-Test.

## Höhenmesser - technischer Hinweis

Klassischer 3-Zeiger-Höhenmesser (Hunderter/Tausender/Zehntausender, alle auf
derselben 0-9-Skala, wie beim echten Instrument). Die QNH-Anzeige zeigt
`KOHLSMAN SETTING HG` gleichzeitig in inHg und hPa (zwei separate Anfragen
derselben SimConnect-Variable mit unterschiedlicher Einheit).

Der Drehknopf unten rechts ist eine **echte Zwei-Wege-Steuerung**: Mausrad
oder vertikales Ziehen (wie in MSFS selbst) sendet `KOHLSMAN_INC`/`KOHLSMAN_DEC`
an den Simulator und verstellt damit tatsächlich die QNH-Einstellung des
Flugzeugs - nicht nur eine lokale Anzeige. Bekannte Einschränkung aus der
SimConnect-Community: Bei Flugzeugen mit Glascockpit (G1000 o.ä.) wirkt dieses
Event teils nur auf einen Standby-Höhenmesser, nicht auf das Hauptdisplay -
bei der klassischen C172 mit Rundinstrumenten funktioniert es normal.

## Drehzahlmesser - technischer Hinweis

Skala 500-3000 U/min (270°-90° im Uhrzeigersinn über die Oberseite), grüner
Normalbereich 2100-2800 U/min mit rotem Strich am oberen Ende. Liest
`GENERAL ENG RPM:1` (Motor 1 - bei einmotorigen Flugzeugen wie der C172 der
einzige Motor).

## VSI - technischer Hinweis

Skala nicht linear - per Hand ausgemessene Kalibrierpunkte (0 bei 9-Uhr-Position):
5→35°, 10→80°, 15→140°, 20→173° (Offset vom 0-Punkt, Sinken-Seite spiegelverkehrt).
Dazwischen wird linear interpoliert. Striche: 0-10 alle 100 ft/min, 10-20 alle
250 ft/min. Liest `VERTICAL SPEED` (ft/min).

## Kursanzeiger - technischer Hinweis

Liest `HEADING INDICATOR` (aktuelle Kreisel-Anzeige, nicht der magnetische Kurs -
kann leicht abweichen, bis mit dem linken Knopf synchronisiert wird) und
`AUTOPILOT HEADING LOCK DIR` (Heading-Bug). Zwei echte Zwei-Wege-Drehknöpfe:

- **Links (PUSH)**: sendet `GYRO_DRIFT_INC`/`GYRO_DRIFT_DEC` - gleicht die
  Kreisel-Anzeige mit dem Kompass ab.
- **Rechts (HDG)**: sendet `HEADING_BUG_INC`/`HEADING_BUG_DEC` - stellt den
  Heading-Bug für den Autopiloten.

Beide per Mausrad oder horizontalem Ziehen bedienbar, wie beim Höhenmesser-Knopf.

## Künstlicher Horizont - technischer Hinweis

Liest `PLANE PITCH DEGREES`/`PLANE BANK DEGREES` (nicht die `ATTITUDE INDICATOR
...`-Varianten, die laut mehreren Entwickler-Quellen unzuverlässig sein können)
und `ATTITUDE BARS POSITION` (Stellung des Nick-Trimm-Knopfs). Zwei getrennte,
rotierende/verschiebende Ebenen: die Himmel/Boden-"Kugel" (Translate+Rotate) und
die Rollwinkel-Skala (nur Rotate, bleibt beim Nicken an Ort und Stelle) - beide
mit fixem Kreis-Clip, damit nichts über den Bezel-Rand hinausragt. Drehknopf
sendet `ATTITUDE_BARS_POSITION_UP`/`DOWN`. Einige Werte (Vorzeichen des
Nick-Winkels, Skalierung von `ATTITUDE BARS POSITION`) sind Annahmen ohne
Testmöglichkeit meinerseits - ggf. nach erstem Test anpassen.

---
*Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen, Kalibrierung und Tests durch Gesiima. Lizenz: AGPL-3.0, siehe [LICENSE](../LICENSE).*
