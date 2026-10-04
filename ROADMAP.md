# Roadmap

Geplante und offene Punkte. Erledigtes wandert in den [CHANGELOG](CHANGELOG.md).
Alle Punkte unter „Zu prüfen" sind **ungetestete Annahmen oder unbestätigte Beobachtungen**, keine bestätigten Fehler.

## Zu prüfen

- **Öldruck-Versatz (Oil Temp/Press):** Beobachtet wurde ein Versatz (Rohwert 65 zeigt ca. 55, 78 zeigt ca. 65), der mangels zuverlässiger Live-Ablesung nicht bestätigt werden konnte. Der Atmosphärendruck ist als Ursache ausgeschlossen (er würde einen konstanten, keinen linear wachsenden Versatz ergeben). Die Kalibrierung bleibt bis zur Klärung unverändert.
- **Künstlicher Horizont:** Vorzeichen des Nick-Winkels und Skalierung von `ATTITUDE BARS POSITION` sind Annahmen und müssen im Simulator geprüft werden (siehe [Technische Hinweise](docs/Technische-Hinweise.md)).
- **Höhenmesser-Knopf bei Glascockpit-Flugzeugen:** Laut SimConnect-Community wirkt `KOHLSMAN_INC`/`KOHLSMAN_DEC` dort teils nur auf den Standby-Höhenmesser. Im MSFS 2024 zu prüfen.

## Geplant

- Weitere Anzeigen nach dem bekannten Muster (Fuel-/EGT-Template klonen, SimConnect-Variablen umstellen, Kalibrierpunkte ermitteln, ATAN2-Zielradius anpassen, Feinjustage per Screenshot-Feedback). Welche Anzeigen, ist noch offen.

## Ideen

- _(hier Wünsche und Ideen sammeln)_

---
*Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen, Kalibrierung und Tests durch Gesiima. Lizenz: AGPL-3.0, siehe [LICENSE](LICENSE).*
