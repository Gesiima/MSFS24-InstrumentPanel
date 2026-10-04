// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace InstrumentPanel
{
    public partial class MainWindow : Window
    {
        private static readonly string LayoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.json");

        private readonly SimConnectService _service;
        private Dictionary<string, object> _layoutData;

        // Registrierungen der Anzeigen dieses Fensters; werden beim Neuaufbau und beim
        // Schließen wieder abgemeldet, damit keine Anzeigen von gelöschten Gauges
        // weiter von SimConnect beliefert werden.
        private RegistrationGroup _registrationGroup;

        // Die Startgröße wird nur beim ersten Aufbau gesetzt - ein späteres Neuladen
        // des Layouts soll manuell veränderte Fenstergrößen nicht überschreiben.
        private bool _startSizeApplied;

        /// <summary>
        /// windowLayout ist das Layout-Datenobjekt NUR für dieses eine Fenster
        /// (siehe App.xaml.cs: LoadWindowLayouts) - bei mehreren Fenstern
        /// (layout.json mit "windows"-Array) bekommt jedes Fenster sein eigenes
        /// Dictionary; ohne "windows" ist es einfach das ganze layout.json.
        /// service ist IMMER dieselbe, geteilte Instanz für alle Fenster - es gibt
        /// nur eine MSFS-Verbindung, unabhängig davon, wie viele Fenster offen sind.
        /// </summary>
        public MainWindow(SimConnectService service, Dictionary<string, object> windowLayout)
        {
            InitializeComponent();

            _service = service;
            _layoutData = windowLayout ?? new Dictionary<string, object>();

            // Bekanntes WPF-Problem bei rahmenlosen Fenstern (WindowStyle="None"):
            // Maximieren würde sonst über die Taskleiste hinausragen. Fix: die
            // maximale Größe explizit auf den verfügbaren Arbeitsbereich begrenzen.
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;

            WindowBackgroundLayer.Fill = WindowBackgroundBrushes.Create(
                AppSettings.WindowBackgroundMode, AppSettings.WindowBackgroundImagePath);

            // Zuletzt gemeldeten Verbindungsstatus sofort anzeigen (ein neu erzeugtes
            // Fenster bekäme sonst bis zur nächsten Statusänderung nur "Verbinde...").
            if (_service != null && _service.TryGetLastStatus(out var statusText, out var statusColor))
                SetStatus(statusText, statusColor);

            BuildLayout();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Anzeigen dieses Fensters bei SimConnect abmelden.
            _registrationGroup?.Dispose();
            _registrationGroup = null;
            base.OnClosed(e);
        }

        /// <summary>Setzt die Fenstergröße nur beim allerersten Aufbau (siehe _startSizeApplied).</summary>
        private void ApplyStartSize(double width, double height)
        {
            if (_startSizeApplied) return;
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) return;

            Width = width;
            Height = height;
            _startSizeApplied = true;
        }

        /// <summary>
        /// Wird von App.xaml.cs aufgerufen, wenn sich der Verbindungsstatus der
        /// (gemeinsamen) SimConnect-Verbindung ändert - jedes offene Fenster zeigt
        /// ihn in seiner eigenen Statuszeile an.
        /// </summary>
        public void SetStatus(string text, Brush color)
        {
            GlobalStatusText.Text = text;
            GlobalStatusText.Foreground = color;
        }

        /// <summary>
        /// Ersetzt die eigenen Layout-Daten und baut das Raster neu auf - wird von
        /// App.xaml.cs aufgerufen, nachdem der Layout-Designer gespeichert hat,
        /// damit die Änderung sofort wirkt, ohne die App neu starten zu müssen.
        /// </summary>
        public void ReloadLayout(Dictionary<string, object> windowLayout)
        {
            _layoutData = windowLayout ?? new Dictionary<string, object>();
            BuildLayout();
        }

        // Größenverhältnis pro Anzeigen-Name (1.0 = normale Größe). Neue kleinere
        // Anzeigen hier eintragen - Zeilen-/Spaltenhöhe im Raster richten sich
        // automatisch danach (z.B. 3 Anzeigen mit Faktor 2/3 nehmen zusammen genau
        // die Höhe von 2 normalen Zeilen ein).
        private static readonly Dictionary<string, double> GaugeScales =
            new Dictionary<string, double>
            {
                ["Fuel"] = 2.0 / 3.0
            };

        private static double GetGaugeScale(string name) =>
            GaugeScales.TryGetValue(name?.Trim() ?? "", out double scale) ? scale : 1.0;

        /// <summary>
        /// Liest das Layout dieses Fensters (neues "cells"-Schema, sonst das ältere
        /// "rows"-Schema) und baut das Raster auf. Meldet vorher die Anzeigen des
        /// alten Aufbaus bei SimConnect ab und fasst die neu erzeugten in einer
        /// frischen Registrierungs-Gruppe zusammen. Fehlt die Datei oder ist sie
        /// ungültig, wird als Fallback nur die Airspeed-Anzeige alleine angezeigt.
        /// </summary>
        private void BuildLayout()
        {
            _registrationGroup?.Dispose();
            _registrationGroup = _service.BeginRegistrationGroup();
            try
            {
                BuildLayoutCore();
            }
            finally
            {
                _registrationGroup.EndCapture();
            }
        }

        private void BuildLayoutCore()
        {
            List<CellDef> cells = null;
            try
            {
                cells = LoadCells();
            }
            catch (Exception ex)
            {
                DebugLog.Write("BuildLayout: Exception beim Laden von 'cells' aus layout.json - " + ex);
            }

            if (cells != null)
            {
                try
                {
                    BuildLayoutFromCells(cells);
                    return;
                }
                catch (Exception ex)
                {
                    // Lieber das einfache Fallback-Layout als ein Fenster, das gar nicht startet.
                    DebugLog.Write("BuildLayout: Exception beim Aufbau aus 'cells' - " + ex);

                    // Bereits angemeldete Gauges des abgebrochenen Aufbaus abmelden (sie werden
                    // gleich aus dem Grid entfernt) und für das Fallback-Layout neu beginnen.
                    _registrationGroup.EndCapture();
                    _registrationGroup.Dispose();
                    _registrationGroup = _service.BeginRegistrationGroup();
                }
            }

            List<List<string>> rows;
            double[] explicitColumnScales = null;
            double[] explicitRowScales = null;
            try
            {
                rows = LoadLayoutRows();
                explicitColumnScales = LoadScaleArray("columnScales");
                explicitRowScales = LoadScaleArray("rowScales");
                DebugLog.Write("BuildLayout: LoadLayoutRows ergab " + (rows == null ? "null" : rows.Count + " Zeile(n): " +
                    string.Join(" | ", rows.Select(r => string.Join(",", r))))
                    + ", columnScales = " + (explicitColumnScales == null ? "keine (Fallback über Anzeigen-Namen)" : string.Join(",", explicitColumnScales))
                    + ", rowScales = " + (explicitRowScales == null ? "keine (volle Höhe)" : string.Join(",", explicitRowScales)));
            }
            catch (Exception ex)
            {
                DebugLog.Write("BuildLayout: Exception beim Laden von layout.json - " + ex);
                rows = new List<List<string>> { new List<string> { "Airspeed" } };
            }

            if (rows == null || rows.Count == 0)
            {
                DebugLog.Write("BuildLayout: Fallback auf Airspeed (rows war null/leer)");
                rows = new List<List<string>> { new List<string> { "Airspeed" } };
            }

            GaugesGrid.RowDefinitions.Clear();
            GaugesGrid.ColumnDefinitions.Clear();
            GaugesGrid.Children.Clear();

            int maxCols = rows.Max(r => r.Count);

            var rowScales = new double[rows.Count];
            var colScales = new double[maxCols];

            if (explicitColumnScales != null)
            {
                // Neues Schema: Spaltenbreiten kommen direkt aus "columnScales" in
                // layout.json (z.B. [2/3, 1, 1, 1]) - unabhängig davon, welche
                // Anzeige dort steht. Zeilenhöhen kommen analog aus "rowScales",
                // falls angegeben - sonst bleiben Zeilen voll hoch (1.0), damit sich
                // die Höhe nicht ungewollt an eine kleine Anzeige anpasst.
                for (int c = 0; c < maxCols; c++)
                    colScales[c] = c < explicitColumnScales.Length && explicitColumnScales[c] > 0
                        ? explicitColumnScales[c] : 1.0;
                for (int r = 0; r < rows.Count; r++)
                    rowScales[r] = explicitRowScales != null && r < explicitRowScales.Length && explicitRowScales[r] > 0
                        ? explicitRowScales[r] : 1.0;
            }
            else
            {
                // Altes Schema (Fallback, falls "columnScales" fehlt): Größenverhältnis
                // je Zeile/Spalte = größter Skalierungsfaktor der darin enthaltenen
                // Anzeigen (über das GaugeScales-Dictionary nach Namen).
                for (int r = 0; r < rows.Count; r++)
                {
                    for (int c = 0; c < rows[r].Count; c++)
                    {
                        double scale = GetGaugeScale(rows[r][c]);
                        rowScales[r] = Math.Max(rowScales[r], scale);
                        colScales[c] = Math.Max(colScales[c], scale);
                    }
                }
                for (int r = 0; r < rows.Count; r++)
                    if (rowScales[r] == 0) rowScales[r] = 1.0; // leere Zeile (sollte nicht vorkommen), Fallback
                for (int c = 0; c < maxCols; c++)
                    if (colScales[c] == 0) colScales[c] = 1.0;
            }

            for (int i = 0; i < rows.Count; i++)
                GaugesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowScales[i], GridUnitType.Star) });
            for (int i = 0; i < maxCols; i++)
                GaugesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(colScales[i], GridUnitType.Star) });

            // Startgröße an das Layout anpassen (statt fixem Quadrat, das bei z.B.
            // zwei nebeneinander liegenden Rundinstrumenten unpassend verzerrt wäre).
            // Größe je Anzeige über settings.json (gaugeCellSize) einstellbar - die
            // Summe der Skalierungsfaktoren ersetzt die frühere reine Spalten-/
            // Zeilenanzahl, damit kleinere Anzeigen auch weniger Platz beanspruchen.
            double cellSize = AppSettings.GaugeCellSize;
            ApplyStartSize(colScales.Sum() * cellSize, rowScales.Sum() * cellSize);

            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var control = CreateGauge(rows[r][c]);
                    if (control == null) continue;

                    var element = (UIElement)control;
                    Grid.SetRow(element, r);
                    Grid.SetColumn(element, c);
                    GaugesGrid.Children.Add(element);

                    control.Initialize(_service);
                }
            }
        }

        /// <summary>
        /// Ein frei positionierbares Anzeigen-Feld im neuen, flexiblen
        /// "cells"-Schema: Name der Anzeige, Startposition (row/col) und
        /// Ausdehnung (rowSpan/colSpan) in Raster-Einheiten. Damit lassen sich
        /// "kleine" und "große" Anzeigen beliebig mischen und in jeder Reihenfolge
        /// anordnen (z.B. 3 kleine Anzeigen nebeneinander in der Höhe von 2 großen).
        /// </summary>
        private class CellDef
        {
            public string Name;
            public int Row;
            public int Col;
            public int RowSpan = 1;
            public int ColSpan = 1;
        }

        /// <summary>
        /// Liest das neue, flexible "cells"-Schema aus dem Layout-Datenobjekt
        /// dieses Fensters (optional):
        /// {
        ///   "rowUnits": 6,
        ///   "colUnits": 24,
        ///   "cells": [
        ///     {"name": "Fuel", "row": 0, "col": 0, "rowSpan": 2},
        ///     {"name": "Airspeed", "row": 0, "col": 1, "rowSpan": 3}
        ///   ]
        /// }
        /// "rowUnits"/"colUnits" ist die Gesamtzahl gleich großer Basis-Kacheln;
        /// jede Anzeige nimmt sich per "rowSpan"/"colSpan" so viele, wie sie
        /// braucht - so ergeben z.B. 3 Zeilen mit rowSpan=2 (macht zusammen 6)
        /// genau die gleiche Gesamthöhe wie 2 Zeilen mit rowSpan=3 (macht auch 6).
        /// Fehlt "cells", wird null zurückgegeben und BuildLayout nutzt das ältere
        /// "rows"-Schema als Fallback.
        /// </summary>
        private List<CellDef> LoadCells()
        {
            if (!_layoutData.TryGetValue("cells", out var cellsObj)) return null;

            if (!(cellsObj is ArrayList cellsList))
            {
                DebugLog.Write("LoadCells: 'cells' ist keine Liste - Fallback auf 'rows'");
                return null;
            }

            var result = new List<CellDef>();
            int position = 0;
            foreach (var item in cellsList)
            {
                int currentPosition = position++;
                try
                {
                    // Eine defekte Zelle (kein Objekt, name/row/col fehlt oder keine Zahl)
                    // wird übersprungen, statt das ganze Layout zu verwerfen.
                    var cellObj = item as Dictionary<string, object>;
                    if (cellObj == null
                        || !cellObj.TryGetValue("name", out var nameObj) || nameObj == null
                        || !cellObj.TryGetValue("row", out var rowObj)
                        || !cellObj.TryGetValue("col", out var colObj))
                    {
                        DebugLog.Write("LoadCells: Zelle " + currentPosition + " unvollständig - übersprungen");
                        continue;
                    }

                    var cell = new CellDef
                    {
                        Name = Convert.ToString(nameObj),
                        Row = Convert.ToInt32(rowObj),
                        Col = Convert.ToInt32(colObj)
                    };
                    if (cellObj.TryGetValue("rowSpan", out var rowSpanObj)) cell.RowSpan = Convert.ToInt32(rowSpanObj);
                    if (cellObj.TryGetValue("colSpan", out var colSpanObj)) cell.ColSpan = Convert.ToInt32(colSpanObj);

                    if (cell.Row < 0 || cell.Col < 0)
                    {
                        DebugLog.Write("LoadCells: Zelle " + currentPosition + " ('" + cell.Name + "') hat negative Position - übersprungen");
                        continue;
                    }
                    if (cell.RowSpan < 1) cell.RowSpan = 1;
                    if (cell.ColSpan < 1) cell.ColSpan = 1;
                    result.Add(cell);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("LoadCells: Zelle " + currentPosition + " ungültig - übersprungen (" + ex.Message + ")");
                }
            }
            return result;
        }

        /// <summary>
        /// Baut das Raster nach dem neuen "cells"-Schema auf: "rowUnits" gleich
        /// hohe Basis-Zeilen, Spaltenbreiten optional über "columnScales" (sonst
        /// alle gleich breit), jede Anzeige an ihrer eigenen Position mit eigener
        /// Ausdehnung (RowSpan/ColSpan).
        /// </summary>
        private void BuildLayoutFromCells(List<CellDef> cells)
        {
            GaugesGrid.RowDefinitions.Clear();
            GaugesGrid.ColumnDefinitions.Clear();
            GaugesGrid.Children.Clear();

            int rowUnits = LoadIntSetting("rowUnits", 0);
            if (rowUnits <= 0)
                rowUnits = cells.Count == 0 ? 1 : cells.Max(c => c.Row + c.RowSpan);

            int colCount = LoadIntSetting("colUnits", 0);
            if (colCount <= 0)
                colCount = cells.Count == 0 ? 1 : cells.Max(c => c.Col + c.ColSpan);
            double[] colScales = LoadScaleArray("columnScales");

            // Breite je Spalte: fehlende oder ungültige Einträge in "columnScales"
            // (Array kürzer als die Spaltenzahl) zählen als 1.0 - gilt einheitlich für
            // die Spaltendefinitionen und die Summe für die Fensterbreite.
            var colWidths = new double[colCount];
            for (int c = 0; c < colCount; c++)
                colWidths[c] = colScales != null && c < colScales.Length && colScales[c] > 0 ? colScales[c] : 1.0;

            for (int i = 0; i < rowUnits; i++)
                GaugesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < colCount; c++)
                GaugesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(colWidths[c], GridUnitType.Star) });

            // Startgröße: eine Raster-Zeile entspricht (1/rowUnits) einer normalen
            // vollen Zeile - Gesamthöhe also rowUnits/größteVollzeilen-Anzahl. Da
            // eine "volle" Anzeige typischerweise die meisten Raster-Zeilen einer
            // Spalte belegt, nehmen wir den größten vorkommenden RowSpan als
            // Referenz für "1.0" (eine normale Zeile).
            double cellSize = AppSettings.GaugeCellSize;
            int maxRowSpan = Math.Max(1, cells.Count == 0 ? 1 : cells.Max(c => c.RowSpan));
            int maxColSpan = Math.Max(1, cells.Count == 0 ? 1 : cells.Max(c => c.ColSpan));
            ApplyStartSize((colWidths.Sum() / maxColSpan) * cellSize, ((double)rowUnits / maxRowSpan) * cellSize);

            foreach (var cell in cells)
            {
                // Position und Ausdehnung ins Raster klemmen (z.B. wenn "rowUnits"/"colUnits"
                // kleiner als eine Zelle ist).
                int row = Math.Min(cell.Row, rowUnits - 1);
                int col = Math.Min(cell.Col, colCount - 1);
                int rowSpan = Math.Max(1, Math.Min(cell.RowSpan, rowUnits - row));
                int colSpan = Math.Max(1, Math.Min(cell.ColSpan, colCount - col));

                var control = CreateGauge(cell.Name);
                if (control == null) continue;

                var element = (UIElement)control;
                Grid.SetRow(element, row);
                Grid.SetColumn(element, col);
                Grid.SetRowSpan(element, rowSpan);
                Grid.SetColumnSpan(element, colSpan);
                GaugesGrid.Children.Add(element);

                control.Initialize(_service);
            }
        }

        /// <summary>
        /// Liest eine einzelne optionale Ganzzahl aus dem Layout-Datenobjekt
        /// dieses Fensters (z.B. "rowUnits"). Fehlt der Schlüssel, wird
        /// defaultValue zurückgegeben.
        /// </summary>
        private int LoadIntSetting(string key, int defaultValue)
        {
            if (!_layoutData.TryGetValue(key, out var valueObj)) return defaultValue;
            try
            {
                return Convert.ToInt32(valueObj);
            }
            catch (Exception ex)
            {
                DebugLog.Write("LoadIntSetting: '" + key + "' ungültig - Standard " + defaultValue + " (" + ex.Message + ")");
                return defaultValue;
            }
        }

        /// <summary>
        /// Liest ein optionales Zahlen-Array aus dem Layout-Datenobjekt dieses
        /// Fensters unter dem angegebenen Schlüssel (z.B. "columnScales" oder
        /// "rowScales") - eine Zahl pro Spalte/Zeile (z.B. [0.667, 1, 1, 1]).
        /// Fehlt der Schlüssel, wird null zurückgegeben und BuildLayout nutzt den
        /// jeweiligen Fallback.
        /// </summary>
        private double[] LoadScaleArray(string key)
        {
            if (!_layoutData.TryGetValue(key, out var scalesObj)) return null;

            if (!(scalesObj is ArrayList scalesList))
            {
                DebugLog.Write("LoadScaleArray: '" + key + "' ist keine Liste - ignoriert");
                return null;
            }

            var list = new List<double>();
            foreach (var value in scalesList)
            {
                // Ungültige Einträge werden 0 (= "Standard 1.0" bei den Aufrufern).
                double scale;
                try { scale = Convert.ToDouble(value); }
                catch (Exception) { scale = 0; }
                if (double.IsNaN(scale) || double.IsInfinity(scale)) scale = 0;
                list.Add(scale);
            }
            return list.ToArray();
        }

        private List<List<string>> LoadLayoutRows()
        {
            if (!_layoutData.TryGetValue("rows", out var rowsObj))
            {
                DebugLog.Write("LoadLayoutRows: kein 'rows'-Schlüssel gefunden");
                return null;
            }

            var result = new List<List<string>>();
            foreach (var rowObj in (ArrayList)rowsObj)
            {
                var row = new List<string>();
                foreach (var cell in (ArrayList)rowObj)
                    row.Add(Convert.ToString(cell));
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Ordnet einen Namen aus layout.json der passenden Anzeigen-Komponente zu.
        /// Neue Anzeigen werden hier einfach ergänzt.
        /// </summary>
        private IGauge CreateGauge(string name)
        {
            switch (name?.Trim())
            {
                case "Airspeed":
                    return new AirspeedGaugeControl();
                case "TurnCoordinator":
                    return new TurnCoordinatorGaugeControl();
                case "Altimeter":
                    return new AltimeterGaugeControl();
                case "Tachometer":
                    return new TachometerGaugeControl();
                case "VerticalSpeed":
                    return new VerticalSpeedGaugeControl();
                case "HeadingIndicator":
                    return new HeadingIndicatorGaugeControl();
                case "AttitudeIndicator":
                    return new AttitudeIndicatorGaugeControl();
                case "Fuel":
                    return new FuelGaugeControl();
                case "Vor":
                    return new VorGaugeControl();
                case "Ruler":
                    return new RulerGaugeControl();
                case "EgtFlow":
                    return new EgtFlowGaugeControl();
                case "OilTempPress":
                    return new OilTempPressGaugeControl();
                default:
                    return null;
            }
        }

        // ---------------------------------------------------------------
        // Fenster verschieben (kein Rahmen/Titelleiste vorhanden)
        // ---------------------------------------------------------------
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            }
            catch (InvalidOperationException)
            {
                // Kann auftreten, wenn die Maustaste zwischenzeitlich losgelassen wurde - ignorieren.
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void LayoutDesignerButton_Click(object sender, RoutedEventArgs e)
        {
            var designer = new LayoutDesignerWindow(
                LayoutPath, _service,
                onSaved: () => ((App)Application.Current).ReloadAllWindows(),
                onWindowDeleted: index => ((App)Application.Current).CloseWindowAt(index))
            {
                Owner = this
            };
            designer.ShowDialog();
        }

        // ---------------------------------------------------------------
        // Fenster per Maus an den Rändern/Ecken vergrößern/verkleinern.
        // WindowStyle=None liefert keinen eigenen Rahmen mit Greifbereichen,
        // daher der native WM_SYSCOMMAND/SC_SIZE-Trick über die unsichtbaren
        // Rectangle-Elemente an den Kanten (siehe MainWindow.xaml).
        // ---------------------------------------------------------------
        private const int WM_SYSCOMMAND = 0x112;
        private const int SC_SIZE = 0xF000;

        private static readonly Dictionary<string, int> ResizeDirections =
            new Dictionary<string, int>
            {
                ["Left"] = 1,
                ["Right"] = 2,
                ["Top"] = 3,
                ["TopLeft"] = 4,
                ["TopRight"] = 5,
                ["Bottom"] = 6,
                ["BottomLeft"] = 7,
                ["BottomRight"] = 8
            };

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (!(element.Tag is string direction) || !ResizeDirections.TryGetValue(direction, out int code)) return;

            e.Handled = true; // verhindert, dass zusätzlich das Fenster verschoben wird

            var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            if (hwndSource == null) return;

            ReleaseCapture();
            SendMessage(hwndSource.Handle, WM_SYSCOMMAND, (IntPtr)(SC_SIZE + code), IntPtr.Zero);
        }
    }
}
