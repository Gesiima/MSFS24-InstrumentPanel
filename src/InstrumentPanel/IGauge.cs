using System.Windows.Media;

namespace InstrumentPanel
{
    /// <summary>
    /// Gemeinsame Schnittstelle für alle Instrumenten-Anzeigen (Airspeed, Turn
    /// Coordinator, künftig weitere). MainWindow erstellt die Anzeigen anhand
    /// von layout.json und ruft Initialize() einmalig auf, damit sich die
    /// Anzeige bei der gemeinsamen SimConnect-Verbindung registrieren kann.
    /// </summary>
    public interface IGauge
    {
        /// <summary>
        /// Wird einmalig aufgerufen, nachdem die Anzeige zum Fenster hinzugefügt wurde.
        /// Hier registriert die Anzeige ihre benötigten SimConnect-Variablen.
        /// </summary>
        void Initialize(SimConnectService service);

        /// <summary>
        /// Wird bei jeder Änderung des Verbindungsstatus aufgerufen
        /// (z.B. "Verbinde...", "Verbunden", "Warte auf MSFS...").
        /// </summary>
        void UpdateStatus(string text, Brush color);
    }
}
