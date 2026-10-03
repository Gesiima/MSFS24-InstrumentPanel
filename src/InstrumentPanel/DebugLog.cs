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

        public static void Write(string message)
        {
            if (!AppSettings.DebugLoggingEnabled) return;

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
