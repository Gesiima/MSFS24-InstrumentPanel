// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace InstrumentPanel
{
    /// <summary>
    /// Liest settings.json (optional, muss neben der .exe liegen) für Werte, die
    /// der Nutzer selbst anpassen möchte - z.B. Aktualisierungsrate oder
    /// Anzeige-Empfindlichkeiten. Fehlt die Datei oder ein einzelner Wert, gilt
    /// der jeweils unten angegebene Standardwert. Wird einmalig beim ersten
    /// Zugriff geladen (App-Neustart nötig nach Änderungen an der Datei).
    /// </summary>
    public static class AppSettings
    {
        /// <summary>Wie oft (in Millisekunden) neue Werte von MSFS angefragt werden.</summary>
        public static int RefreshIntervalMs { get; private set; } = 100;

        /// <summary>
        /// Kantenlänge (Pixel) einer "normalen" Anzeige, die sich im cells-Schema
        /// über den größten vorkommenden rowSpan/colSpan erstreckt. Startgröße des
        /// Fensters: Breite = Summe der Spaltenbreiten / größter colSpan × Wert,
        /// Höhe = rowUnits / größter rowSpan × Wert (aus layout.json). Gültig 50..2000.
        /// </summary>
        public static double GaugeCellSize { get; private set; } = 375;

        /// <summary>
        /// Schaltet das Debug-Logging (debug.log) ein/aus. Standardmäßig AUS,
        /// damit im Normalbetrieb kein unnötiges Disk-I/O anfällt - nur bei
        /// Fehlersuche in settings.json auf true setzen.
        /// </summary>
        public static bool DebugLoggingEnabled { get; private set; } = false;

        /// <summary>
        /// Teiler für die Rohwerte von TURN COORDINATOR BALL, bevor sie auf den
        /// sichtbaren Anzeigebereich abgebildet werden. SimConnect liefert diesen
        /// Wert bei Einheit "position" bereits normalisiert im Bereich -1 bis 1
        /// (trotz anderslautender Dokumentation für den internen Rohwert) - daher
        /// ist 1.0 (keine zusätzliche Teilung) der richtige Standardwert.
        /// </summary>
        public static double TurnCoordinatorBallDivisor { get; private set; } = 1.0;

        /// <summary>
        /// Schwellwert (in inHg) für SUCTION PRESSURE, ab dem der Kreisel als
        /// "läuft" gilt (ON-Anzeige). Ein realer C172-Vakuumkreisel braucht ca.
        /// 4.5-5.4 inHg im Normalbetrieb; darunter (z.B. bei stehendem Motor)
        /// läuft die Pumpe nicht mehr und der Kreisel läuft aus.
        /// </summary>
        public static double TurnCoordinatorVacuumThreshold { get; private set; } = 3.0;

        /// <summary>
        /// Schwellwert (in inHg) für SUCTION PRESSURE beim künstlichen Horizont
        /// (Schlüssel "attitudeVacuumThreshold"). Wert &lt;= 0 schaltet die
        /// Vakuum-Logik ab - der Kreisel gilt dann immer als an.
        /// </summary>
        public static double AttitudeVacuumThreshold { get; private set; } = 3.0;

        /// <summary>
        /// Wird vom SimConnect-Rohwert für GENERAL ENG OIL PRESSURE abgezogen
        /// (in PSI), da dieser ggf. absolut statt als Gauge-Druck (relativ zur
        /// Umgebung) geliefert wird. Standard: 1 bar = 14,5 PSI.
        /// </summary>
        public static double OilPressAtmosphericOffsetPsi { get; private set; } = 14.5;

        /// <summary>
        /// EGT-Skalengrenzen in °F (unterer/oberer Skalenrand). Ursprünglich aus
        /// realen Kalibrierpunkten (Tick-Werte in Rankine, umgerechnet)
        /// hergeleitet - kann je nach Flugzeug/Triebwerk abweichen, daher hier
        /// einstellbar statt fest im Code.
        /// </summary>
        public static double EgtMinF { get; private set; } = 1260.33;
        public static double EgtMaxF { get; private set; } = 1640.33;

        /// <summary>
        /// Hintergrund-Muster für das gesamte Fenster (hinter allen Anzeigen).
        /// "none" = weiterhin transparent (Standard, unverändertes Verhalten -
        /// Anzeigen schweben frei über Desktop/Sim). Weitere Werte: siehe
        /// WindowBackgroundBrushes.cs für die verfügbaren Muster-Namen.
        /// </summary>
        public static string WindowBackgroundMode { get; private set; } = "none";

        /// <summary>
        /// Pfad zu einer eigenen Bilddatei, falls WindowBackgroundMode="custom".
        /// </summary>
        public static string WindowBackgroundImagePath { get; private set; } = "";

        private static readonly string SettingsPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        static AppSettings()
        {
            Load();
        }

        private static void Load()
        {
            Dictionary<string, object> root;
            try
            {
                if (!File.Exists(SettingsPath)) return;

                var json = File.ReadAllText(SettingsPath);
                var serializer = new JavaScriptSerializer();
                root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return;
            }
            catch (Exception ex)
            {
                // Bei fehlerhafter Datei einfach die Standardwerte behalten,
                // statt die App am Start scheitern zu lassen.
                DebugLog.Write("AppSettings: settings.json nicht lesbar - " + ex.Message);
                return;
            }

            // debugLogging zuerst, damit die Meldungen zu allen weiteren Werten schon geloggt werden.
            ReadKey("debugLogging", () =>
            {
                if (root.TryGetValue("debugLogging", out var debugObj))
                    DebugLoggingEnabled = ToBool(debugObj);
            });

            // Jeder Schlüssel einzeln: ein kaputter Wert verwirft nicht alle folgenden.
            ReadKey("refreshIntervalMs", () =>
            {
                if (root.TryGetValue("refreshIntervalMs", out var obj))
                {
                    if (TryToDouble(obj, out var value) && value >= 10 && value <= 5000)
                        RefreshIntervalMs = (int)Math.Round(value);
                    else
                        DebugLog.Write("AppSettings: refreshIntervalMs außerhalb 10..5000 oder ungültig - Standard 100 bleibt");
                }
            });

            ReadKey("gaugeCellSize", () =>
            {
                if (root.TryGetValue("gaugeCellSize", out var obj))
                {
                    if (TryToDouble(obj, out var value) && value >= 50 && value <= 2000)
                        GaugeCellSize = value;
                    else
                        DebugLog.Write("AppSettings: gaugeCellSize außerhalb 50..2000 oder ungültig - Standard 375 bleibt");
                }
            });

            ReadKey("turnCoordinator", () =>
            {
                if (root.TryGetValue("turnCoordinator", out var tcObj) && tcObj is Dictionary<string, object> tc)
                {
                    ReadKey("turnCoordinator.ballDivisor", () =>
                    {
                        if (tc.TryGetValue("ballDivisor", out var divisorObj))
                        {
                            if (TryToDouble(divisorObj, out var value) && value != 0)
                                TurnCoordinatorBallDivisor = value;
                            else
                                DebugLog.Write("AppSettings: ballDivisor ist 0 oder ungültig - Standard 1.0");
                        }
                    });
                    ReadKey("turnCoordinator.vacuumThreshold", () =>
                    {
                        if (tc.TryGetValue("vacuumThreshold", out var vacuumObj))
                        {
                            if (TryToDouble(vacuumObj, out var value) && value >= 0)
                                TurnCoordinatorVacuumThreshold = value;
                            else
                                DebugLog.Write("AppSettings: turnCoordinator.vacuumThreshold negativ oder ungültig - Standard bleibt");
                        }
                    });
                }
            });

            ReadKey("attitudeVacuumThreshold", () =>
            {
                if (root.TryGetValue("attitudeVacuumThreshold", out var obj))
                {
                    // Wert <= 0 ist erlaubt und bedeutet: Vakuum-Logik aus.
                    if (TryToDouble(obj, out var value))
                        AttitudeVacuumThreshold = value;
                    else
                        DebugLog.Write("AppSettings: attitudeVacuumThreshold ungültig - Standard 3.0 bleibt");
                }
            });

            ReadKey("oilPressAtmosphericOffsetPsi", () =>
            {
                if (root.TryGetValue("oilPressAtmosphericOffsetPsi", out var obj))
                {
                    if (TryToDouble(obj, out var value))
                        OilPressAtmosphericOffsetPsi = value;
                    else
                        DebugLog.Write("AppSettings: oilPressAtmosphericOffsetPsi ungültig - Standard bleibt");
                }
            });

            ReadKey("egtMinF/egtMaxF", () =>
            {
                double min = EgtMinF, max = EgtMaxF;
                bool hasMin = root.TryGetValue("egtMinF", out var egtMinObj) && TryToDouble(egtMinObj, out min);
                bool hasMax = root.TryGetValue("egtMaxF", out var egtMaxObj) && TryToDouble(egtMaxObj, out max);
                if (!hasMin) min = EgtMinF;
                if (!hasMax) max = EgtMaxF;

                // Beide Grenzen gemeinsam prüfen: Max muss über Min liegen, sonst Standardwerte.
                if (max > min)
                {
                    EgtMinF = min;
                    EgtMaxF = max;
                }
                else
                {
                    DebugLog.Write("AppSettings: egtMaxF muss größer als egtMinF sein - Standardwerte 1260.33/1640.33");
                    EgtMinF = 1260.33;
                    EgtMaxF = 1640.33;
                }
            });

            ReadKey("windowBackground", () =>
            {
                if (root.TryGetValue("windowBackground", out var bgObj) && bgObj is Dictionary<string, object> bg)
                {
                    if (bg.TryGetValue("mode", out var modeObj))
                        WindowBackgroundMode = modeObj?.ToString() ?? "none";
                    if (bg.TryGetValue("imagePath", out var imgObj))
                        WindowBackgroundImagePath = imgObj?.ToString() ?? "";
                }
            });
        }

        /// <summary>Führt das Lesen eines einzelnen Schlüssels aus; ein Fehler wird nur geloggt.</summary>
        private static void ReadKey(string key, Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex)
            {
                DebugLog.Write("AppSettings: Wert '" + key + "' konnte nicht gelesen werden - " + ex.Message);
            }
        }

        /// <summary>
        /// Wandelt einen JSON-Wert (Zahl oder Text wie "375,5" / "375.5") kulturunabhängig
        /// in double um; NaN und Unendlich werden abgewiesen.
        /// </summary>
        private static bool TryToDouble(object raw, out double value)
        {
            value = 0;
            if (raw == null || raw is bool) return false;

            try
            {
                if (raw is string text)
                {
                    text = text.Trim().Replace(',', '.');
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        return false;
                }
                else
                {
                    value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                return false;
            }

            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool ToBool(object raw)
        {
            if (raw is string text) return bool.Parse(text.Trim());
            return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }
    }
}
