// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.IO;

namespace InstrumentPanel
{
    /// <summary>
    /// Zentrales Debug-Logging für die ganze App (ersetzt die zuvor 4x duplizierte
    /// Log()-Methode in App, MainWindow, SimConnectService und
    /// TurnCoordinatorGaugeControl). Standardmäßig AUS - nur wenn
    /// settings.json → "debugLogging": true gesetzt ist, wird tatsächlich
    /// geschrieben. So läuft im Normalbetrieb kein unnötiges Disk-I/O mit.
    /// </summary>
    public static class DebugLog
    {
        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");

        /// <summary>True, wenn Debug-Logging aktiv ist (lässt teure Meldungen vorab überspringen).</summary>
        public static bool Enabled => AppSettings.DebugLoggingEnabled;

        /// <summary>
        /// Wie Write(string), baut die Meldung aber nur, wenn das Logging aktiv ist -
        /// für Aufrufe in heißen Pfaden (z.B. pro Frame), damit nichts umsonst
        /// zusammengesetzt wird.
        /// </summary>
        public static void Write(Func<string> messageFactory)
        {
            if (!Enabled || messageFactory == null) return;

            try
            {
                Write(messageFactory());
            }
            catch
            {
                // Eine fehlerhafte Meldungs-Factory darf die App nicht stören.
            }
        }

        public static void Write(string message)
        {
            if (!Enabled) return;

            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
                // Logging darf die App niemals zum Absturz bringen.
            }
        }
    }
}
