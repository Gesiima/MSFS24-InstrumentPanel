using System;
using System.Collections.Generic;
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
        /// Startgröße je Anzeige in Pixeln (quadratisch). Das Fenster startet mit
        /// Spaltenanzahl × Zeilenanzahl (aus layout.json) mal diesem Wert.
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
            try
            {
                if (!File.Exists(SettingsPath)) return;

                var json = File.ReadAllText(SettingsPath);
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return;

                if (root.TryGetValue("refreshIntervalMs", out var refreshObj))
                    RefreshIntervalMs = Convert.ToInt32(refreshObj);

                if (root.TryGetValue("gaugeCellSize", out var cellSizeObj))
                    GaugeCellSize = Convert.ToDouble(cellSizeObj);

                if (root.TryGetValue("debugLogging", out var debugObj))
                    DebugLoggingEnabled = Convert.ToBoolean(debugObj);

                if (root.TryGetValue("turnCoordinator", out var tcObj) && tcObj is Dictionary<string, object> tc)
                {
                    if (tc.TryGetValue("ballDivisor", out var divisorObj))
                        TurnCoordinatorBallDivisor = Convert.ToDouble(divisorObj);
                    if (tc.TryGetValue("vacuumThreshold", out var vacuumObj))
                        TurnCoordinatorVacuumThreshold = Convert.ToDouble(vacuumObj);
                }

                if (root.TryGetValue("oilPressAtmosphericOffsetPsi", out var oilOffsetObj))
                    OilPressAtmosphericOffsetPsi = Convert.ToDouble(oilOffsetObj);

                if (root.TryGetValue("egtMinF", out var egtMinObj))
                    EgtMinF = Convert.ToDouble(egtMinObj);
                if (root.TryGetValue("egtMaxF", out var egtMaxObj))
                    EgtMaxF = Convert.ToDouble(egtMaxObj);

                if (root.TryGetValue("windowBackground", out var bgObj) && bgObj is Dictionary<string, object> bg)
                {
                    if (bg.TryGetValue("mode", out var modeObj))
                        WindowBackgroundMode = modeObj?.ToString() ?? "none";
                    if (bg.TryGetValue("imagePath", out var imgObj))
                        WindowBackgroundImagePath = imgObj?.ToString() ?? "";
                }
            }
            catch
            {
                // Bei fehlerhafter Datei einfach die Standardwerte behalten,
                // statt die App am Start scheitern zu lassen.
            }
        }
    }
}
