// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using System.Web.Script.Serialization;

namespace InstrumentPanel
{
    /// <summary>
    /// WPF-Einstiegspunkt. Fängt zusätzlich unbehandelte Ausnahmen ab und zeigt
    /// sie als MessageBox an, damit die App nicht kommentarlos verschwindet.
    ///
    /// Öffnet außerdem, statt nur eines einzelnen MainWindow (früher über
    /// StartupUri), wahlweise MEHRERE unabhängige Fenster - z.B. um Anzeigen auf
    /// mehrere Monitore zu verteilen. Alle Fenster teilen sich EINE gemeinsame
    /// SimConnectService-Verbindung (nicht pro Fenster eine eigene).
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Ein offenes Fenster samt seiner Position im 'windows'-Array von layout.json
        /// (SourceIndex, unabhängig von Lücken/null-Einträgen im Array). Darüber
        /// werden Layout-Designer-Index und Fenster einander zugeordnet.
        /// </summary>
        private class WindowEntry
        {
            public MainWindow Window;
            public int SourceIndex;
            public bool IsClosed;
        }

        private SimConnectService _sharedService;
        private readonly List<WindowEntry> _windows = new List<WindowEntry>();

        // SourceIndex-Werte von Fenstern, die der Nutzer selbst geschlossen hat - sie
        // werden beim Speichern im Designer nicht automatisch wieder geöffnet.
        private readonly HashSet<int> _closedSourceIndexes = new HashSet<int>();

        public App()
        {
            // Manche Umgebungen laden die SimConnect-Assembly nicht zuverlässig über
            // die normale .NET-Bindung, obwohl die Datei direkt neben der .exe liegt.
            // Als Fallback laden wir sie in diesem Fall explizit per Pfad.
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                DebugLog.Write("InstrumentPanel v" + AppVersion.Current + " gestartet");

                // Beim Schließen des letzten Fensters soll die App enden (Standard bei
                // StartupUri), das müssen wir jetzt selbst einstellen, da wir Fenster
                // dynamisch statt über StartupUri erzeugen.
                ShutdownMode = ShutdownMode.OnLastWindowClose;

                _sharedService = new SimConnectService();
                Exit += (s, args) => _sharedService?.Stop();

                var windowLayouts = LoadWindowLayouts();
                foreach (var layout in windowLayouts)
                    CreateWindow(layout.Key, layout.Value);

                // Eine gemeinsame Verbindung für alle Fenster: erst starten, wenn alle
                // Fenster existieren, und erst stoppen, wenn das letzte Fenster zu ist
                // (nicht schon beim ersten geschlossenen von mehreren).
                _sharedService.StatusChanged += (text, color) =>
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var entry in _windows.ToList()) entry.Window.SetStatus(text, color);
                    });
                _sharedService.Start();
            }
            catch (Exception ex)
            {
                // Ohne Fenster bliebe die App sonst als unsichtbarer Prozess hängen.
                DebugLog.Write("OnStartup: Start fehlgeschlagen - " + ex);
                MessageBox.Show(
                    ex.ToString(),
                    "InstrumentPanel - Start fehlgeschlagen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Erzeugt und zeigt ein neues Fenster für den Eintrag sourceIndex des
        /// 'windows'-Arrays und merkt es sich in der Verwaltungsliste.
        /// </summary>
        private void CreateWindow(int sourceIndex, Dictionary<string, object> layout)
        {
            var window = new MainWindow(_sharedService, layout);
            var entry = new WindowEntry { Window = window, SourceIndex = sourceIndex };
            _windows.Add(entry);

            window.Closed += (s, args) =>
            {
                entry.IsClosed = true;
                // Vom Nutzer geschlossen (nicht über CloseWindowAt): Position merken,
                // damit ein Designer-Speichern das Fenster nicht ungefragt neu öffnet.
                if (_windows.Remove(entry))
                    _closedSourceIndexes.Add(entry.SourceIndex);
            };

            window.Show();
        }

        /// <summary>
        /// Liest layout.json und liefert eine Liste von Fenster-Layouts (je ein
        /// Dictionary mit den Schlüsseln wie "cells"/"rows"/"rowUnits"/... für
        /// genau EIN Fenster). Neues Schema: ein optionaler "windows"-Schlüssel im
        /// Wurzelobjekt mit einer Liste solcher Fenster-Layouts - für mehrere
        /// unabhängige Fenster (z.B. auf mehreren Monitoren). Fehlt "windows",
        /// wird das Wurzelobjekt selbst als EIN einzelnes Fenster-Layout benutzt
        /// (Rückwärtskompatibilität zu bisherigen layout.json-Dateien).
        /// Jeder Eintrag trägt als Key seine Position im 'windows'-Array (Lücken/
        /// null-Einträge werden übersprungen und nicht mitgezählt - der Layout-
        /// Designer kompaktiert sie ebenfalls, damit stimmen beide Indizes überein).
        /// </summary>
        private List<KeyValuePair<int, Dictionary<string, object>>> LoadWindowLayouts()
        {
            string layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.json");
            var result = new List<KeyValuePair<int, Dictionary<string, object>>>();

            if (!File.Exists(layoutPath))
            {
                DebugLog.Write("LoadWindowLayouts: layout.json nicht gefunden - Fallback auf leeres Einzelfenster");
                result.Add(new KeyValuePair<int, Dictionary<string, object>>(0, new Dictionary<string, object>()));
                return result;
            }

            try
            {
                var json = File.ReadAllText(layoutPath);
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(json);

                if (root != null && root.TryGetValue("windows", out var windowsObj))
                {
                    int position = 0;
                    foreach (var entry in (ArrayList)windowsObj)
                    {
                        // null-Einträge (Lücken, z.B. von älteren Designer-Versionen geschrieben)
                        // überspringen, statt ein leeres/fehlerhaftes Fenster zu öffnen. Die
                        // Position zählt NUR gültige Fenster: der Layout-Designer kompaktiert
                        // solche Lücken beim Laden/Speichern (CompactWindows), sein Fenster-
                        // Index entspricht damit genau dieser kompakten Position.
                        if (entry is Dictionary<string, object> windowDict)
                        {
                            result.Add(new KeyValuePair<int, Dictionary<string, object>>(position, windowDict));
                            position++;
                        }
                    }
                    DebugLog.Write("LoadWindowLayouts: " + result.Count + " Fenster aus 'windows' geladen");
                }
                else
                {
                    result.Add(new KeyValuePair<int, Dictionary<string, object>>(0, root ?? new Dictionary<string, object>()));
                    DebugLog.Write("LoadWindowLayouts: kein 'windows'-Schlüssel - ein Fenster aus dem Wurzelobjekt");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("LoadWindowLayouts: Fehler beim Laden von layout.json - " + ex);
                result.Clear();
                result.Add(new KeyValuePair<int, Dictionary<string, object>>(0, new Dictionary<string, object>()));
            }

            if (result.Count == 0)
                result.Add(new KeyValuePair<int, Dictionary<string, object>>(0, new Dictionary<string, object>()));
            return result;
        }

        /// <summary>
        /// Schließt GEZIELT das Fenster an dieser Position im 'windows'-Array (0-basiert,
        /// Index VOR dem Löschen, so wie ihn der Layout-Designer meldet) und entfernt es
        /// aus der Verwaltungsliste. Alle anderen offenen Fenster bleiben unangetastet;
        /// nur ihre gemerkte Array-Position rückt um eins nach unten, wie im Designer
        /// nach dem Löschen. Das Schließen selbst wird verzögert, damit ein Handler, der
        /// im zu schließenden Fenster läuft (z.B. der modale Setup-Dialog), nicht
        /// mitten in der Ausführung auf einem sterbenden Fenster weiterläuft.
        /// </summary>
        public void CloseWindowAt(int index)
        {
            if (index < 0) return;

            var entry = _windows.FirstOrDefault(w => w.SourceIndex == index);
            if (entry != null) _windows.Remove(entry);

            // Array-Positionen oberhalb des gelöschten Eintrags rücken nach.
            foreach (var other in _windows)
                if (other.SourceIndex > index) other.SourceIndex--;
            var shiftedClosed = _closedSourceIndexes
                .Where(i => i != index)
                .Select(i => i > index ? i - 1 : i)
                .ToList();
            _closedSourceIndexes.Clear();
            foreach (var i in shiftedClosed) _closedSourceIndexes.Add(i);

            // Nur schließen, wenn es nicht schon geschlossen ist (kein Close() auf totem Fenster).
            if (entry != null && !entry.IsClosed)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!entry.IsClosed) entry.Window.Close();
                }), DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Liest layout.json neu ein und aktualisiert JEDES bereits offene Fenster
        /// mit seinen (ggf. geänderten) eigenen Layout-Daten - so kann der
        /// Layout-Designer nach dem Speichern sofort wirken, ohne dass die App neu
        /// gestartet werden muss. Die Zuordnung läuft über die Position im
        /// 'windows'-Array (nicht über die Reihenfolge der offenen Fenster). Gibt es
        /// ein Layout ohne zugehöriges Fenster (im Designer neu angelegt und
        /// gespeichert), wird sofort ein neues Fenster erzeugt - kein Neustart nötig.
        /// Vom Nutzer selbst geschlossene Fenster werden dabei nicht wieder geöffnet.
        /// Für das gezielte Schließen eines gelöschten Fensters siehe CloseWindowAt.
        /// </summary>
        public void ReloadAllWindows()
        {
            var windowLayouts = LoadWindowLayouts();

            foreach (var layout in windowLayouts)
            {
                var entry = _windows.FirstOrDefault(w => w.SourceIndex == layout.Key && !w.IsClosed);
                if (entry != null)
                    entry.Window.ReloadLayout(layout.Value);
                else if (!_closedSourceIndexes.Contains(layout.Key))
                    CreateWindow(layout.Key, layout.Value);
            }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var simpleName = new AssemblyName(args.Name).Name;
                var candidate = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, simpleName + ".dll");
                if (System.IO.File.Exists(candidate))
                {
                    DebugLog.Write("AssemblyResolve-Fallback: lade " + candidate);
                    return Assembly.LoadFrom(candidate);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("AssemblyResolve-Fallback fehlgeschlagen: " + ex);
            }
            return null;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ShowError(e.Exception);
            e.Handled = true; // App am Laufen halten, statt sie abstürzen zu lassen
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                ShowError(ex);
        }

        private static void ShowError(Exception ex)
        {
            DebugLog.Write("GLOBAL HANDLER - " + ex);

            MessageBox.Show(
                ex.ToString(),
                "InstrumentPanel - unerwarteter Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

    }
}
