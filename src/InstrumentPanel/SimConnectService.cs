// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.FlightSimulator.SimConnect;

namespace InstrumentPanel
{
    /// <summary>
    /// Verwaltet EINE gemeinsame SimConnect-Verbindung für alle Anzeigen im Fenster.
    /// Jede Anzeige registriert über Register&lt;T&gt;() ihre eigenen SimConnect-Variablen
    /// und bekommt per Callback die aktuellen Werte geliefert. Verbindung/Reconnect
    /// laufen Event-basiert (kein nativer Fenster-Message-Hook nötig), das Muster ist
    /// 1:1 aus der ursprünglichen Einzel-Anzeige-Version übernommen.
    /// </summary>
    public class SimConnectService
    {
        public event Action<string, Brush> StatusChanged;

        private enum DefinitionId : uint { }
        private enum RequestId : uint { }
        private enum EventId : uint { }

        internal sealed class Registration
        {
            public Enum DefId;
            public Enum ReqId;
            public Action<SimConnect> DoRegister;
            public Action<SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE> OnData;

            /// <summary>True, sobald die Definition auf der AKTUELLEN Verbindung angelegt ist.</summary>
            public bool Registered;
        }

        /// <summary>
        /// Letzter bekannter Wert JEDER angefragten SimConnect-Variable, über alle
        /// Anzeigen hinweg - für die Debug-Werte-Anzeige im Setup-Fenster. Wird bei
        /// jedem eingehenden Datenpaket aktualisiert (per Reflection über die
        /// jeweiligen Registrierungs-Structs, da diese die eigentlichen Werte
        /// typisiert, aber ohne direkten Namensbezug halten).
        /// </summary>
        private readonly Dictionary<string, double> _latestValues = new Dictionary<string, double>();

        /// <summary>
        /// Momentaufnahme aller zuletzt empfangenen SimConnect-Variablenwerte
        /// (Name -> Wert), unabhängig davon, welche Anzeige sie registriert hat.
        /// Für die Debug-Werte-Anzeige im Setup-Fenster gedacht.
        /// </summary>
        public Dictionary<string, double> GetLatestValuesSnapshot()
        {
            lock (_latestValues)
                return new Dictionary<string, double>(_latestValues);
        }

        private readonly List<Registration> _registrations = new List<Registration>();
        private readonly Dictionary<uint, Registration> _registrationsByReqId = new Dictionary<uint, Registration>();
        private readonly Dictionary<string, EventId> _mappedEvents = new Dictionary<string, EventId>();
        private readonly ManualResetEvent _simConnectEvent = new ManualResetEvent(false);
        private readonly DispatcherTimer _reconnectTimer = new DispatcherTimer();
        private readonly DispatcherTimer _requestTimer = new DispatcherTimer();
        private SimConnect _simConnect;
        private bool _isConnected;
        private uint _nextId;

        // Kopie von _registrations zum gefahrlosen Durchlaufen (eine Registrierung
        // darf entfernt werden, während gerade iteriert wird); null = neu aufbauen.
        private Registration[] _registrationSnapshot;

        // Gruppe, der neue Register<T>()-Aufrufe gerade zugeordnet werden (siehe BeginRegistrationGroup).
        private RegistrationGroup _currentGroup;

        // OnRecvQuit kommt mitten aus ReceiveMessage - dort darf die Verbindung nicht
        // abgebaut werden, deshalb nur ein Flag; aufgeräumt wird in PumpAndRequest.
        private bool _quitRequested;

        // Verhindert Log-Spam durch den 5-s-Reconnect-Takt (zurückgesetzt bei Verbindung/Abbruch).
        private bool _connectStartLogged;
        private string _lastConnectError;

        // Letzter gemeldeter Status, damit neu erzeugte Fenster ihn sofort anzeigen können.
        private string _lastStatusText;
        private Brush _lastStatusColor;

        public SimConnectService()
        {
            _reconnectTimer.Interval = TimeSpan.FromSeconds(5);
            _reconnectTimer.Tick += (s, e) => TryConnect();

            _requestTimer.Interval = TimeSpan.FromMilliseconds(AppSettings.RefreshIntervalMs);
            _requestTimer.Tick += (s, e) => PumpAndRequest();
        }

        /// <summary>
        /// Liefert den zuletzt gemeldeten Verbindungsstatus. Gibt false zurück, solange
        /// noch kein Status gemeldet wurde (dann gilt der Standardtext des Fensters).
        /// </summary>
        public bool TryGetLastStatus(out string text, out Brush color)
        {
            text = _lastStatusText;
            color = _lastStatusColor;
            return text != null;
        }

        /// <summary>
        /// Beginnt eine neue Registrierungs-Gruppe: alle Register&lt;T&gt;()-Aufrufe bis
        /// zu group.EndCapture() werden ihr zugeordnet. group.Dispose() meldet sie
        /// wieder ab (z.B. beim Layout-Neuaufbau oder Schließen eines Fensters), damit
        /// keine Anzeigen von gelöschten Gauges weiter beliefert/angefragt werden.
        /// </summary>
        public RegistrationGroup BeginRegistrationGroup()
        {
            var group = new RegistrationGroup(this);
            _currentGroup = group;
            return group;
        }

        internal void EndCapture(RegistrationGroup group)
        {
            if (ReferenceEquals(_currentGroup, group))
                _currentGroup = null;
        }

        /// <summary>Entfernt alle Registrierungen einer Gruppe (nicht mehr anfragen, nicht mehr verteilen).</summary>
        internal void RemoveGroup(RegistrationGroup group)
        {
            EndCapture(group);

            foreach (var reg in group.Registrations)
            {
                _registrations.Remove(reg);
                _registrationsByReqId.Remove(Convert.ToUInt32(reg.ReqId));

                // Definition bei MSFS aufräumen; nicht kritisch, daher Fehler nur loggen.
                if (_simConnect != null && reg.Registered)
                {
                    try { _simConnect.ClearDataDefinition(reg.DefId); }
                    catch (Exception ex) { DebugLog.Write("RemoveGroup: ClearDataDefinition fehlgeschlagen - " + ex.Message); }
                }
                reg.Registered = false;
            }
            group.Registrations.Clear();
            _registrationSnapshot = null;
        }

        private Registration[] GetRegistrationSnapshot()
        {
            return _registrationSnapshot ?? (_registrationSnapshot = _registrations.ToArray());
        }

        /// <summary>
        /// Registriert eine oder mehrere SimConnect-Variablen als eine Datendefinition
        /// vom Typ T (Reihenfolge der Felder in T muss der Reihenfolge in "vars" entsprechen).
        /// onData wird bei jedem neuen Wert aufgerufen (Aufruf erfolgt auf dem UI-Thread).
        /// </summary>
        public void Register<T>(List<(string Name, string Unit, SIMCONNECT_DATATYPE Type)> vars, Action<T> onData)
            where T : struct
        {
            var defId = (DefinitionId)_nextId++;
            var reqId = (RequestId)_nextId++;

            void DoRegister(SimConnect sc)
            {
                foreach (var v in vars)
                    sc.AddToDataDefinition(defId, v.Name, v.Unit, v.Type, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<T>(defId);
            }

            // Per Projekt-Konvention entspricht die Feld-Reihenfolge des Structs der
            // Reihenfolge in "vars" (GetFields() liefert in der Praxis die
            // Deklarationsreihenfolge, garantiert ist das von der CLR aber nicht).
            // Darüber wird jeder Wert für die Debug-Anzeige seinem SimConnect-Namen
            // zugeordnet, ohne dass jede Anzeige das selbst mitpflegen müsste.
            var fields = typeof(T).GetFields();

            var reg = new Registration
            {
                DefId = defId,
                ReqId = reqId,
                DoRegister = DoRegister,
                OnData = data =>
                {
                    T value;
                    try
                    {
                        value = (T)data.dwData[0];
                        onData(value);
                    }
                    catch (Exception ex)
                    {
                        // Ein Fehler in einer Anzeige darf nicht als "Verbindung verloren" enden.
                        DebugLog.Write("Register: Fehler in onData (" + typeof(T).Name + ") - " + ex);
                        return;
                    }
                    UpdateLatestValues(vars, fields, value);
                }
            };
            _registrations.Add(reg);
            _registrationsByReqId[Convert.ToUInt32(reqId)] = reg;
            _registrationSnapshot = null;
            _currentGroup?.Registrations.Add(reg);

            // Schon eine SimConnect-Instanz vorhanden (auch wenn OnRecvOpen noch nicht
            // eingetroffen ist, wie beim Verbindungsaufbau in TryConnect), sofort
            // anmelden. TryConnect meldet nur die bis dahin vorhandenen Registrierungen
            // an, spätere kommen hier - so wird nichts doppelt oder gar nicht registriert.
            var simConnect = _simConnect;
            if (simConnect != null)
            {
                try
                {
                    DoRegister(simConnect);
                    reg.Registered = true;
                }
                catch (Exception ex)
                {
                    // Bleibt für diese Verbindung unregistriert (wird nicht angefragt);
                    // erst ein Neuaufbau der Verbindung versucht es erneut.
                    DebugLog.Write("Register: Anmeldung fehlgeschlagen (" + typeof(T).Name + ") - " + ex.Message);
                }
            }
        }

        private void UpdateLatestValues<T>(List<(string Name, string Unit, SIMCONNECT_DATATYPE Type)> vars,
            System.Reflection.FieldInfo[] fields, T value) where T : struct
        {
            try
            {
                lock (_latestValues)
                {
                    for (int i = 0; i < vars.Count && i < fields.Length; i++)
                    {
                        var raw = fields[i].GetValue(value);
                        _latestValues[vars[i].Name] = Convert.ToDouble(raw);
                    }
                }
            }
            catch
            {
                // Debug-Werte sind rein informativ - ein Fehler hier darf die
                // eigentliche Anzeigen-Aktualisierung (onData oben) nicht stören.
            }
        }

        public void Start()
        {
            TryConnect();
            _reconnectTimer.Start();
        }

        /// <summary>
        /// Sendet ein SimConnect-Client-Event an MSFS (z.B. zum Verstellen eines
        /// Drehknopfs). Events werden beim ersten Gebrauch automatisch gemappt und
        /// danach wiederverwendet. Schlägt die Verbindung gerade fehl, wird der
        /// Aufruf einfach verworfen (kein Absturz, kein Fehler an den Aufrufer).
        /// </summary>
        public void SendEvent(string simEventName, uint data = 0)
        {
            if (_simConnect == null || !_isConnected) return;

            try
            {
                if (!_mappedEvents.TryGetValue(simEventName, out var id))
                {
                    id = (EventId)_nextId++;
                    _simConnect.MapClientEventToSimEvent(id, simEventName);
                    _mappedEvents[simEventName] = id;
                }

                // Wichtig: SIMCONNECT_UNUSED darf hier NICHT verwendet werden - da das Flag
                // GROUPID_IS_PRIORITY gesetzt ist, wird dieser Parameter direkt als Priorität
                // interpretiert. SIMCONNECT_UNUSED (ein riesiger Sentinel-Wert) führte dazu,
                // dass das Event nie ausgeführt wurde. Stattdessen die höchste Priorität (1).
                _simConnect.TransmitClientEvent(
                    0,
                    id,
                    data,
                    (EventId)1u,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
            catch (Exception ex)
            {
                DebugLog.Write("SendEvent: Fehler bei '" + simEventName + "' - " + ex.Message);
            }
        }

        public void Stop()
        {
            _reconnectTimer.Stop();
            _requestTimer.Stop();
            DisposeSimConnect();
            _simConnectEvent.Dispose();
        }

        private void TryConnect()
        {
            // Bereits verbunden: nichts zu tun.
            if (_isConnected && _simConnect != null) return;

            // Ein früherer Versuch hat zwar ein Objekt erzeugt, aber nie "Open"
            // gemeldet: erst freigeben, sonst wird bei jedem Versuch eines liegen gelassen.
            if (_simConnect != null) DisposeSimConnect();

            // Nur den ersten Versuch loggen, nicht den 5-s-Takt der Wiederholungen.
            if (!_connectStartLogged)
            {
                DebugLog.Write("TryConnect: Start (weitere Versuche werden nicht einzeln geloggt)");
                _connectStartLogged = true;
            }

            try
            {
                _simConnectEvent.Reset();
                _quitRequested = false;
                _simConnect = new SimConnect("Instrument Panel", IntPtr.Zero, 0, _simConnectEvent, 0);

                _simConnect.OnRecvOpen += (s, d) =>
                {
                    _isConnected = true;
                    _lastConnectError = null;
                    RaiseStatus("Verbunden", Brushes.LightGreen);
                };
                _simConnect.OnRecvQuit += (s, d) =>
                {
                    // Nicht hier abbauen (wir stecken mitten in ReceiveMessage):
                    // nur merken, PumpAndRequest räumt nach der Schleife auf.
                    _quitRequested = true;
                    _isConnected = false;
                    RaiseStatus("MSFS beendet - warte...", Brushes.Orange);
                };
                _simConnect.OnRecvException += (s, d) =>
                    DebugLog.Write("SimConnect-Exception: Code=" + d.dwException + ", SendID=" + d.dwSendID + ", Index=" + d.dwIndex);
                _simConnect.OnRecvSimobjectDataBytype += Simconnect_OnRecvSimobjectDataBytype;

                foreach (var reg in GetRegistrationSnapshot())
                {
                    reg.DoRegister(_simConnect);
                    reg.Registered = true;
                }

                _requestTimer.Start();
                DebugLog.Write("TryConnect: fertig, " + _registrations.Count + " Registrierungen");
            }
            catch (COMException ex)
            {
                // Normalfall, solange MSFS nicht läuft: nur beim ersten Mal loggen.
                if (_lastConnectError != "COM")
                    DebugLog.Write("TryConnect: COMException - " + ex.Message);
                _lastConnectError = "COM";
                DisposeSimConnect();
                RaiseStatus("Warte auf MSFS...", Brushes.Orange);
            }
            catch (Exception ex)
            {
                // Anderer Fehler: anzeigen, aber weiter versuchen (Reconnect-Timer läuft weiter).
                string key = ex.GetType().Name + ": " + ex.Message;
                if (_lastConnectError != key)
                    DebugLog.Write("TryConnect: Exception - " + ex);
                _lastConnectError = key;
                DisposeSimConnect();
                RaiseStatus("Fehler: " + ex.GetType().Name, Brushes.Red);
            }
        }

        private void Simconnect_OnRecvSimobjectDataBytype(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
        {
            // Dictionary-Lookup statt linearer Suche - skaliert besser, je mehr
            // Anzeigen/Registrierungen dazukommen.
            if (_registrationsByReqId.TryGetValue(data.dwRequestID, out var reg))
                reg.OnData(data);
        }

        private void PumpAndRequest()
        {
            var simConnect = _simConnect;
            if (simConnect == null) return;

            bool connectionLost = false;
            try
            {
                foreach (var reg in GetRegistrationSnapshot())
                {
                    if (_simConnect == null || !reg.Registered) continue;
                    simConnect.RequestDataOnSimObjectType(reg.ReqId, reg.DefId, 0, SIMCONNECT_SIMOBJECT_TYPE.USER);
                }

                // Auf _simConnect != null und _quitRequested prüfen: ein Quit mitten in
                // der Schleife darf nicht zu ReceiveMessage auf einem toten Objekt führen.
                while (_simConnect != null && !_quitRequested && _simConnectEvent.WaitOne(0))
                {
                    _simConnectEvent.Reset();
                    _simConnect.ReceiveMessage();
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("PumpAndRequest: Fehler - " + ex);
                connectionLost = true;
            }

            if (_quitRequested)
            {
                // MSFS wurde beendet: Status "MSFS beendet - warte..." bleibt stehen.
                _quitRequested = false;
                _requestTimer.Stop();
                DisposeSimConnect();
                _connectStartLogged = false;
            }
            else if (connectionLost)
            {
                _requestTimer.Stop();
                RaiseStatus("Verbindung verloren...", Brushes.Orange);
                DisposeSimConnect();
                _connectStartLogged = false;
            }
        }

        private void DisposeSimConnect()
        {
            _isConnected = false;

            // Nach Verbindungsende gilt keine Anmeldung mehr, und die Events müssen
            // bei der nächsten Verbindung (z.B. nach MSFS-Neustart) neu gemappt werden.
            foreach (var reg in _registrations)
                reg.Registered = false;
            _mappedEvents.Clear();

            var simConnect = _simConnect;
            _simConnect = null;
            try { simConnect?.Dispose(); }
            catch (Exception ex) { DebugLog.Write("DisposeSimConnect: " + ex.Message); }
        }

        private void RaiseStatus(string text, Brush color)
        {
            // Unveränderten Status nicht erneut melden (z.B. "Warte auf MSFS..." alle 5 s).
            if (text == _lastStatusText && ReferenceEquals(color, _lastStatusColor)) return;

            _lastStatusText = text;
            _lastStatusColor = color;
            StatusChanged?.Invoke(text, color);
        }
    }

    /// <summary>
    /// Fasst Registrierungen zusammen, die zwischen SimConnectService.BeginRegistrationGroup()
    /// und EndCapture() entstehen. Dispose() meldet sie alle wieder ab - so lassen sich
    /// die Anzeigen eines Fensters beim Layout-Neuaufbau oder Schließen sauber entfernen,
    /// ohne dass die Anzeigen selbst etwas dafür tun müssen.
    /// </summary>
    public sealed class RegistrationGroup : IDisposable
    {
        private readonly SimConnectService _owner;
        private bool _disposed;

        internal readonly List<SimConnectService.Registration> Registrations = new List<SimConnectService.Registration>();

        internal RegistrationGroup(SimConnectService owner)
        {
            _owner = owner;
        }

        /// <summary>Beendet die Zuordnung: spätere Register-Aufrufe gehören nicht mehr zu dieser Gruppe.</summary>
        public void EndCapture() => _owner.EndCapture(this);

        /// <summary>Meldet alle Registrierungen dieser Gruppe ab (mehrfaches Aufrufen ist harmlos).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.RemoveGroup(this);
        }
    }
}
