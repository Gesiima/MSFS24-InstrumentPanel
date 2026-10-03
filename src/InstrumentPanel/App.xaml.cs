using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        private SimConnectService _sharedService;
        private readonly List<MainWindow> _windows = new List<MainWindow>();

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

            // Beim Schließen des letzten Fensters soll die App enden (Standard bei
            // StartupUri), das müssen wir jetzt selbst einstellen, da wir Fenster
            // dynamisch statt über StartupUri erzeugen.
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            _sharedService = new SimConnectService();

            var windowLayouts = LoadWindowLayouts();
            foreach (var layout in windowLayouts)
            {
                var window = new MainWindow(_sharedService, layout);
                _windows.Add(window);
                window.Show();
            }

            // Eine gemeinsame Verbindung für alle Fenster: erst starten, wenn alle
            // Fenster existieren, und erst stoppen, wenn das letzte Fenster zu ist
            // (nicht schon beim ersten geschlossenen von mehreren).
            _sharedService.StatusChanged += (text, color) =>
                Dispatcher.Invoke(() =>
                {
                    foreach (var w in _windows) w.SetStatus(text, color);
                });
            _sharedService.Start();
            Exit += (s, args) => _sharedService.Stop();
        }

        /// <summary>
        /// Liest layout.json und liefert eine Liste von Fenster-Layouts (je ein
        /// Dictionary mit den Schlüsseln wie "cells"/"rows"/"rowUnits"/... für
        /// genau EIN Fenster). Neues Schema: ein optionaler "windows"-Schlüssel im
        /// Wurzelobjekt mit einer Liste solcher Fenster-Layouts - für mehrere
        /// unabhängige Fenster (z.B. auf mehreren Monitoren). Fehlt "windows",
        /// wird das Wurzelobjekt selbst als EIN einzelnes Fenster-Layout benutzt
        /// (Rückwärtskompatibilität zu bisherigen layout.json-Dateien).
        /// </summary>
        private List<Dictionary<string, object>> LoadWindowLayouts()
        {
            string layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.json");
            var result = new List<Dictionary<string, object>>();

            if (!File.Exists(layoutPath))
            {
                DebugLog.Write("LoadWindowLayouts: layout.json nicht gefunden - Fallback auf leeres Einzelfenster");
                result.Add(new Dictionary<string, object>());
                return result;
            }

            try
            {
                var json = File.ReadAllText(layoutPath);
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(json);

                if (root != null && root.TryGetValue("windows", out var windowsObj))
                {
                    foreach (var entry in (ArrayList)windowsObj)
                    {
                        if (entry is Dictionary<string, object> windowDict)
                            result.Add(windowDict);
                        // null-Einträge (Lücken, z.B. wenn im Layout-Designer Fenster 2
                        // gespeichert wurde, ohne dass Fenster 1 existiert) überspringen,
                        // statt ein leeres/fehlerhaftes Fenster zu öffnen.
                    }
                    DebugLog.Write("LoadWindowLayouts: " + result.Count + " Fenster aus 'windows' geladen");
                }
                else
                {
                    result.Add(root ?? new Dictionary<string, object>());
                    DebugLog.Write("LoadWindowLayouts: kein 'windows'-Schlüssel - ein Fenster aus dem Wurzelobjekt");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("LoadWindowLayouts: Fehler beim Laden von layout.json - " + ex);
                result.Add(new Dictionary<string, object>());
            }

            if (result.Count == 0) result.Add(new Dictionary<string, object>());
            return result;
        }

        /// <summary>
        /// Liest layout.json neu ein und aktualisiert JEDES bereits offene Fenster
        /// mit seinen (ggf. geänderten) eigenen Layout-Daten - so kann der
        /// Layout-Designer nach dem Speichern sofort wirken, ohne dass die App neu
        /// gestartet werden muss. Ändert sich dabei die ANZAHL der Fenster (ein
        /// neues hinzugefügt/eins entfernt), betrifft das nur die bereits
        /// bestehenden Fenster (Index für Index) - für ein wirklich NEUES Fenster
        /// ist weiterhin ein Neustart nötig, da WPF-Fenster nicht "nachträglich"
        /// aus dem Nichts erzeugt werden, ohne dass der Nutzer das explizit anstößt.
        /// </summary>
        /// <summary>
        /// Schließt GEZIELT das Fenster an dieser Position (0-basiert, entspricht
        /// dem Index im 'windows'-Array VOR dem Löschen) und entfernt es aus der
        /// Verwaltungsliste - alle anderen bereits offenen Fenster bleiben dabei
        /// unangetastet (ihr eigener Inhalt ändert sich nicht, nur ihr Index
        /// verschiebt sich intern um eins nach unten, was beim nächsten Speichern
        /// im Designer automatisch berücksichtigt wird).
        /// </summary>
        public void CloseWindowAt(int index)
        {
            if (index < 0 || index >= _windows.Count) return;
            var toClose = _windows[index];
            _windows.RemoveAt(index);
            toClose.Close();
        }

        /// <summary>
        /// Liest layout.json neu ein und aktualisiert JEDES bereits offene Fenster
        /// mit seinen (ggf. geänderten) eigenen Layout-Daten - so kann der
        /// Layout-Designer nach dem Speichern sofort wirken, ohne dass die App neu
        /// gestartet werden muss. Für das gezielte Schließen eines gelöschten
        /// Fensters siehe CloseWindowAt - diese Methode geht nur von einer
        /// GLEICHBLEIBENDEN Fensteranzahl aus (reines Inhalts-Update).
        /// </summary>
        /// <summary>
        /// Liest layout.json neu ein und aktualisiert JEDES bereits offene Fenster
        /// mit seinen (ggf. geänderten) eigenen Layout-Daten - so kann der
        /// Layout-Designer nach dem Speichern sofort wirken, ohne dass die App neu
        /// gestartet werden muss. Gibt es jetzt MEHR Fenster als vorher (ein neues
        /// wurde im Designer angelegt und gespeichert), wird für jedes davon sofort
        /// ein neues, echtes Fenster erzeugt und angezeigt - kein Neustart nötig.
        /// Für das gezielte Schließen eines gelöschten Fensters siehe CloseWindowAt.
        /// </summary>
        public void ReloadAllWindows()
        {
            var windowLayouts = LoadWindowLayouts();

            for (int i = 0; i < _windows.Count && i < windowLayouts.Count; i++)
                _windows[i].ReloadLayout(windowLayouts[i]);

            for (int i = _windows.Count; i < windowLayouts.Count; i++)
            {
                var newWindow = new MainWindow(_sharedService, windowLayouts[i]);
                _windows.Add(newWindow);
                newWindow.Show();
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
