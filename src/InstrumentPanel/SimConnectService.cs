using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private class Registration
        {
            public Enum DefId;
            public Enum ReqId;
            public Action<SimConnect> DoRegister;
            public Action<SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE> OnData;
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

        public SimConnectService()
        {
            _reconnectTimer.Interval = TimeSpan.FromSeconds(5);
            _reconnectTimer.Tick += (s, e) => TryConnect();

            _requestTimer.Interval = TimeSpan.FromMilliseconds(AppSettings.RefreshIntervalMs);
            _requestTimer.Tick += (s, e) => PumpAndRequest();
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

            // Feld-Reihenfolge des Structs entspricht laut Projekt-Konvention immer
            // der Reihenfolge in "vars" - das nutzen wir hier per Reflection, um
            // jeden Wert für die Debug-Anzeige seinem SimConnect-Namen zuzuordnen,
            // ohne dass jede Anzeige das selbst mitpflegen müsste.
            var fields = typeof(T).GetFields();

            var reg = new Registration
            {
                DefId = defId,
                ReqId = reqId,
                DoRegister = DoRegister,
                OnData = data =>
                {
                    var value = (T)data.dwData[0];
                    onData(value);
                    UpdateLatestValues(vars, fields, value);
                }
            };
            _registrations.Add(reg);
            _registrationsByReqId[Convert.ToUInt32(reqId)] = reg;

            if (_isConnected)
            {
                try { DoRegister(_simConnect); }
                catch { /* wird beim nächsten Reconnect erneut versucht */ }
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
            if (_isConnected) return;
            DebugLog.Write("TryConnect: Start");

            try
            {
                _simConnectEvent.Reset();
                _simConnect = new SimConnect("Instrument Panel", IntPtr.Zero, 0, _simConnectEvent, 0);

                _simConnect.OnRecvOpen += (s, d) =>
                {
                    _isConnected = true;
                    RaiseStatus("Verbunden", Brushes.LightGreen);
                };
                _simConnect.OnRecvQuit += (s, d) =>
                {
                    _isConnected = false;
                    _requestTimer.Stop();
                    RaiseStatus("MSFS beendet - warte...", Brushes.Orange);
                    DisposeSimConnect();
                };
                _simConnect.OnRecvException += (s, d) =>
                    DebugLog.Write("SimConnect-Exception: Code=" + d.dwException + ", SendID=" + d.dwSendID + ", Index=" + d.dwIndex);
                _simConnect.OnRecvSimobjectDataBytype += Simconnect_OnRecvSimobjectDataBytype;

                foreach (var reg in _registrations)
                    reg.DoRegister(_simConnect);

                _requestTimer.Start();
                DebugLog.Write("TryConnect: fertig, " + _registrations.Count + " Registrierungen");
            }
            catch (COMException ex)
            {
                DebugLog.Write("TryConnect: COMException - " + ex.Message);
                DisposeSimConnect();
                RaiseStatus("Warte auf MSFS...", Brushes.Orange);
            }
            catch (Exception ex)
            {
                DebugLog.Write("TryConnect: Exception - " + ex);
                DisposeSimConnect();
                _reconnectTimer.Stop();
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
            if (_simConnect == null) return;

            try
            {
                foreach (var reg in _registrations)
                    _simConnect.RequestDataOnSimObjectType(reg.ReqId, reg.DefId, 0, SIMCONNECT_SIMOBJECT_TYPE.USER);

                while (_simConnectEvent.WaitOne(0))
                {
                    _simConnectEvent.Reset();
                    _simConnect.ReceiveMessage();
                }
            }
            catch (Exception)
            {
                _isConnected = false;
                _requestTimer.Stop();
                RaiseStatus("Verbindung verloren...", Brushes.Orange);
                DisposeSimConnect();
            }
        }

        private void DisposeSimConnect()
        {
            _simConnect?.Dispose();
            _simConnect = null;
        }

        private void RaiseStatus(string text, Brush color) => StatusChanged?.Invoke(text, color);

    }
}
