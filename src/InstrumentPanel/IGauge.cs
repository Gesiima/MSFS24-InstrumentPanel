// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
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
    }
}
