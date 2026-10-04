// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace InstrumentPanel
{
    /// <summary>
    /// Kleines Setup-Fenster, um layout.json visuell zusammenzuklicken, statt es
    /// von Hand zu editieren. Kein eigenes .exe - wird aus der Haupt-App heraus
    /// geöffnet (siehe MainWindow: LayoutDesignerButton_Click).
    ///
    /// Modell: REKURSIVES AUFTEILEN (wie bei Editor-Fensterteilung/Tiling-
    /// Fenstermanagern) - jede Fläche lässt sich wahlweise in Zeilen ODER Spalten
    /// aufteilen, beliebig verschachtelt. Das ist bewusst symmetrisch zwischen
    /// Zeilen und Spalten (anders als das frühere "Zeilen×Spalten-Raster mit
    /// Sonderfällen"), damit z.B. sowohl eine 2/3-Zeile als auch eine 2/3-Spalte -
    /// auch kombiniert/verschachtelt - abgebildet werden können. Passt exakt zum
    /// bestehenden "cells"-Format in layout.json (row/col/rowSpan/colSpan).
    /// </summary>
    public partial class LayoutDesignerWindow : Window
    {
        private static readonly string[] AvailableGauges =
        {
            "(leer)", "Airspeed", "TurnCoordinator", "Altimeter", "Tachometer",
            "VerticalSpeed", "HeadingIndicator", "AttitudeIndicator", "Fuel", "Vor", "Ruler", "EgtFlow", "OilTempPress"
        };

        /// <summary>
        /// "Normal"-Gewicht eines Knotens innerhalb seiner Geschwister. 27720 =
        /// kgV(1..12): dadurch sind die gekoppelten Breiten (NormalWeight * Referenz-
        /// Zeilen / eigene Zeilen) für bis zu 12 Zeilen EXAKT ganzzahlig und müssen
        /// nicht gerundet werden. Gespeichert wird ohnehin nur der gekürzte Bruch.
        /// </summary>
        private const int NormalWeight = 27720;

        /// <summary>Obergrenze der Teilflächen-Anzahl pro Aufteilen-Vorgang.</summary>
        private const int MaxSplitCount = 24;

        /// <summary>
        /// Obergrenze für rowUnits/colUnits (kgV der Rastergrößen) beim Speichern -
        /// MainWindow legt pro Einheit eine Grid-Zeile/-Spalte an, absurd große
        /// Raster würden die Anzeige ausbremsen.
        /// </summary>
        private const int MaxGridUnits = 1000;

        /// <summary>Richtung der Aufteilung eines Knotens im Baum.</summary>
        private enum SplitDirection { Rows, Columns }

        /// <summary>
        /// Ein Knoten im Aufteilungs-Baum: entweder ein Blatt (IsLeaf=true, mit
        /// einer zugewiesenen Anzeige) oder ein Split (in mehrere Kind-Flächen,
        /// gestapelt gemäß "Direction", gewichtet nach deren "Weight").
        /// </summary>
        private class SplitNode
        {
            public bool IsLeaf = true;
            public string GaugeName = "(leer)";
            public SplitDirection Direction;
            public List<SplitNode> Children = new List<SplitNode>();

            /// <summary>
            /// Relatives Gewicht dieses Knotens INNERHALB der Aufteilung seines
            /// Elternteils (Standard NormalWeight = gleich groß wie "normale"
            /// Geschwister). Ein Gewicht von z.B. 2 bei Geschwistern mit Gewicht 3
            /// ergibt ein Größenverhältnis von 2:3 - damit lassen sich ungleich
            /// große Teilflächen abbilden (z.B. eine 2/3-große Anzeige neben
            /// normalen).
            /// </summary>
            public int Weight = NormalWeight;

            /// <summary>
            /// Wenn true, wird Weight bei jeder Anzeige NEU aus der eigenen
            /// Zeilenanzahl berechnet (siehe RefreshAutoWidth) - damit bleibt die
            /// Breite automatisch im Einklang, auch wenn man später noch Zeilen
            /// hinzufügt/entfernt. Nur sinnvoll bei Direction==Rows UNTER einer
            /// Spalten-Aufteilung.
            /// </summary>
            public bool AutoWidth = false;
        }

        /// <summary>Exakter Bruch (keine Fließkomma-Ungenauigkeiten beim Aufteilen).</summary>
        private struct Frac
        {
            public long Num, Den;
            public Frac(long num, long den)
            {
                if (den == 0) den = 1; // Schutz vor Division durch 0 (sollte nie vorkommen)
                if (den < 0) { num = -num; den = -den; }
                long g = Gcd(Math.Abs(num), den);
                if (g == 0) g = 1;
                Num = num / g;
                Den = den / g;
            }
            public static long Gcd(long a, long b) => b == 0 ? (a == 0 ? 1 : a) : Gcd(b, a % b);
            public static Frac operator +(Frac a, Frac b) => new Frac(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
            public static Frac operator *(Frac a, long k) => new Frac(a.Num * k, a.Den);
            public static Frac operator /(Frac a, long k) => new Frac(a.Num, a.Den * (k == 0 ? 1 : k));
        }

        /// <summary>Eine Zeile der Debug-Werte-Liste (Wert aktualisiert sich in-place per INotifyPropertyChanged).</summary>
        private sealed class DebugValueRow : INotifyPropertyChanged
        {
            private string _value;
            public string Name { get; set; }
            public string Value
            {
                get { return _value; }
                set
                {
                    if (_value == value) return;
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private SplitNode _root = new SplitNode();
        private readonly string _layoutPath;
        private readonly string _settingsPath;
        private readonly SimConnectService _service;
        private readonly DispatcherTimer _debugValuesTimer = new DispatcherTimer();
        private int _currentWindowIndex = 0;

        private readonly Action _onSaved;
        private readonly Action<int> _onWindowDeleted;

        /// <summary>true = der Baum wurde seit dem letzten Laden/Speichern verändert (ungespeicherte Änderungen).</summary>
        private bool _dirty = false;

        /// <summary>true = beim Einlesen musste eine Fläche vereinfacht werden (Zellen gehen beim Speichern verloren).</summary>
        private bool _reconstructionIncomplete = false;

        /// <summary>Eingabe in den "Aufteilen"-Feldern je Knoten und Richtung - überlebt dadurch ein Neuzeichnen des Baums.</summary>
        private readonly Dictionary<(SplitNode, SplitDirection), string> _splitCountInputs = new Dictionary<(SplitNode, SplitDirection), string>();

        private readonly ObservableCollection<DebugValueRow> _debugValueItems = new ObservableCollection<DebugValueRow>();
        private readonly Dictionary<string, DebugValueRow> _debugValueRows = new Dictionary<string, DebugValueRow>();

        public LayoutDesignerWindow(string layoutPath, SimConnectService service, Action onSaved = null, Action<int> onWindowDeleted = null)
        {
            InitializeComponent();
            Title = "Setup - InstrumentPanel v" + AppVersion.Current;
            _layoutPath = layoutPath;
            _settingsPath = Path.Combine(Path.GetDirectoryName(layoutPath) ?? AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            _service = service;
            _onSaved = onSaved;
            _onWindowDeleted = onWindowDeleted;

            // Der Konstruktor darf nie werfen (z.B. bei defekter layout.json/settings.json) -
            // die Lade-Methoden sind bereits defensiv, das hier ist der doppelte Boden.
            try
            {
                RefreshWindowButtons();
                LoadWindow(0); // Fenster 0 (Standard) direkt beim Öffnen einlesen
                LoadSettingsIntoUi();
            }
            catch (Exception ex)
            {
                ShowStatus("Fehler beim Initialisieren: " + ex.Message, Brushes.Salmon);
            }

            DebugValuesList.ItemsSource = _debugValueItems;
            MainTabs.SelectionChanged += (s, e) =>
            {
                // SelectionChanged der inneren ComboBoxen blubbert hierher - nur den Reiterwechsel beachten.
                if (ReferenceEquals(e.OriginalSource, MainTabs)) RefreshDebugValues();
            };
            _debugValuesTimer.Interval = TimeSpan.FromSeconds(1);
            _debugValuesTimer.Tick += (s, e) => RefreshDebugValues();
            _debugValuesTimer.Start();
            Closed += (s, e) => _debugValuesTimer.Stop();
            RefreshDebugValues();
        }

        /// <summary>Fragt beim Schließen nach, wenn noch ungespeicherte Layout-Änderungen vorliegen.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (!ConfirmLeaveWindow()) e.Cancel = true;
        }

        private void ShowStatus(string text, Brush color)
        {
            StatusText.Foreground = color;
            StatusText.Text = text;
        }

        private void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>
        /// Wird vor allem aufgerufen, was die aktuelle Bearbeitung verwirft
        /// (Fensterwechsel, "+ Neues Fenster", Schließen). Ohne ungespeicherte
        /// Änderungen sofort true. Sonst Ja = speichern (und bei Erfolg weiter),
        /// Nein = Änderungen verwerfen, Abbrechen = false (nichts passiert).
        /// </summary>
        private bool ConfirmLeaveWindow()
        {
            if (!_dirty) return true;

            var answer = MessageBox.Show(this,
                "Fenster " + _currentWindowIndex + " hat ungespeicherte Änderungen.\n\n" +
                "Ja = speichern\nNein = Änderungen verwerfen\nAbbrechen = hier bleiben",
                "Ungespeicherte Änderungen", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No)
            {
                _dirty = false;
                return true;
            }
            return TrySaveLayout();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dirty)
            {
                var answer = MessageBox.Show(this,
                    "Ungespeicherte Änderungen gehen dabei verloren. Fenster " + _currentWindowIndex + " trotzdem leeren?",
                    "Fenster leeren", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
            _root = new SplitNode();
            _splitCountInputs.Clear();
            MarkDirty(); // leer weicht vom gespeicherten Stand ab
            RedrawTree();
        }

        // -----------------------------------------------------------------
        // Hilfen: JSON lesen/schreiben
        // -----------------------------------------------------------------

        /// <summary>
        /// Liefert die Liste hinter einem JSON-Array (JavaScriptSerializer liefert
        /// ArrayList, bei manchen Konstellationen object[]) - sonst null.
        /// </summary>
        private static IEnumerable AsList(object value)
        {
            if (value is ArrayList || value is object[]) return (IEnumerable)value;
            return null;
        }

        /// <summary>Liest einen Integer-Wert tolerant aus einem JSON-Objekt (false bei fehlend/null/ungültig).</summary>
        private static bool TryGetInt(Dictionary<string, object> dict, string key, out int value)
        {
            value = 0;
            try
            {
                if (dict != null && dict.TryGetValue(key, out var raw) && raw != null)
                {
                    value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch { /* ungültiger Wert - wie "nicht vorhanden" behandeln */ }
            return false;
        }

        /// <summary>
        /// Wandelt den Wert des 'windows'-Schlüssels in eine KOMPAKTE Liste von
        /// Fenster-Objekten um: null-Einträge und Nicht-Objekte werden entfernt
        /// (die App überspringt sie beim Laden ebenfalls) - so stimmen der Index
        /// im Designer und der Fenster-Index in der App überein.
        /// </summary>
        private static List<Dictionary<string, object>> CompactWindows(object windowsObj, out int skippedEntries)
        {
            skippedEntries = 0;
            var result = new List<Dictionary<string, object>>();
            var list = AsList(windowsObj);
            if (list == null)
            {
                if (windowsObj != null) skippedEntries = 1;
                return result;
            }
            foreach (var entry in list)
            {
                if (entry is Dictionary<string, object> windowDict) result.Add(windowDict);
                else skippedEntries++;
            }
            return result;
        }

        /// <summary>
        /// Liest das 'windows'-Array aus layout.json (kompaktiert, siehe
        /// CompactWindows). Leere Liste, wenn Datei oder Array fehlen. "problem"
        /// enthält einen Hinweistext, wenn die Datei defekt war.
        /// </summary>
        private List<Dictionary<string, object>> ReadWindowsList(out string problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(_layoutPath)) return new List<Dictionary<string, object>>();
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_layoutPath));
                if (root != null && root.TryGetValue("windows", out var windowsObj))
                {
                    var windows = CompactWindows(windowsObj, out int skipped);
                    if (skipped > 0) problem = skipped + " ungültige(r) Eintrag/Einträge im 'windows'-Array ignoriert.";
                    return windows;
                }
            }
            catch (Exception ex)
            {
                problem = "layout.json konnte nicht gelesen werden: " + ex.Message;
            }
            return new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Schreibt eine Datei ATOMAR: erst in eine Temp-Datei im selben Ordner
        /// (UTF-8 ohne BOM), dann per File.Replace/Move an ihren Platz - bei einem
        /// Absturz mitten im Schreiben bleibt so die alte Datei heil.
        /// </summary>
        private static void WriteAllTextAtomic(string path, string content)
        {
            string tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tempPath, path, null);
                else File.Move(tempPath, path);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* Aufräumen ist best-effort */ }
            }
        }

        /// <summary>
        /// Baut die Fenster-Übersicht (ein Button je vorhandenem Fenster + "+ Neues
        /// Fenster") neu auf - das gerade ausgewählte Fenster ist hervorgehoben.
        /// Jeder Button zeigt per Tooltip, welche Anzeigen in diesem Fenster stecken.
        /// </summary>
        private void RefreshWindowButtons()
        {
            var windows = ReadWindowsList(out _);
            WindowButtonsPanel.Children.Clear();

            for (int i = 0; i < windows.Count; i++)
            {
                int index = i;
                bool isSelected = index == _currentWindowIndex;

                string gaugesSummary = "leer";
                try
                {
                    if (windows[i].TryGetValue("cells", out var cellsObj))
                    {
                        var names = new List<string>();
                        var cellList = AsList(cellsObj);
                        if (cellList != null)
                        {
                            foreach (var cell in cellList)
                            {
                                if (cell is Dictionary<string, object> cellDict && cellDict.TryGetValue("name", out var nameObj) && nameObj != null)
                                    names.Add(Convert.ToString(nameObj));
                            }
                        }
                        names = names.Distinct().ToList();
                        if (names.Count > 0) gaugesSummary = string.Join(", ", names);
                    }
                }
                catch { /* Tooltip ist rein informativ */ }

                var button = new Button
                {
                    Content = "Fenster " + index,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
                    Background = isSelected ? new SolidColorBrush(Color.FromRgb(0x3a, 0x5a, 0x3a)) : SystemColors.ControlBrush,
                    ToolTip = "Anzeigen: " + gaugesSummary
                };
                button.Click += (s, e) =>
                {
                    if (!ConfirmLeaveWindow()) return;
                    _currentWindowIndex = index;
                    LoadWindow(index);
                    RefreshWindowButtons();
                };
                WindowButtonsPanel.Children.Add(button);
            }

            var addButton = new Button
            {
                Content = "+ Neues Fenster",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
                FontWeight = _currentWindowIndex >= windows.Count ? FontWeights.Bold : FontWeights.Normal,
                Background = _currentWindowIndex >= windows.Count ? new SolidColorBrush(Color.FromRgb(0x3a, 0x5a, 0x3a)) : SystemColors.ControlBrush,
                ToolTip = "Legt ein neues, leeres Fenster an (wird erst beim Speichern tatsächlich in layout.json geschrieben)"
            };
            addButton.Click += (s, e) =>
            {
                if (!ConfirmLeaveWindow()) return;
                // Nach dem eventuellen Speichern in ConfirmLeaveWindow ist die Fensteranzahl ggf. größer - neu lesen.
                int newIndex = ReadWindowsList(out _).Count;
                _currentWindowIndex = newIndex;
                _root = new SplitNode();
                _splitCountInputs.Clear();
                _dirty = false; // leeres neues Fenster: es gibt noch nichts zu speichern
                RedrawTree();
                RefreshWindowButtons();
            };
            WindowButtonsPanel.Children.Add(addButton);
        }

        /// <summary>
        /// Entfernt das aktuell ausgewählte Fenster KOMPLETT aus dem
        /// 'windows'-Array in layout.json (nicht nur leeren) und schließt es
        /// sofort, falls es gerade offen ist (nach Rückfrage).
        /// </summary>
        private void DeleteWindowButton_Click(object sender, RoutedEventArgs e)
        {
            int windowIndex = _currentWindowIndex;
            int remainingWindows;

            try
            {
                if (!File.Exists(_layoutPath))
                {
                    ShowStatus("layout.json existiert nicht - nichts zu löschen.", Brushes.Salmon);
                    return;
                }

                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_layoutPath))
                           ?? new Dictionary<string, object>();

                if (!root.TryGetValue("windows", out var windowsObj))
                {
                    ShowStatus("Kein 'windows'-Array vorhanden - nichts zu löschen.", Brushes.Salmon);
                    return;
                }

                // Kompaktiert (null-Einträge raus), damit der Index mit dem der App übereinstimmt.
                var windows = CompactWindows(windowsObj, out _).Cast<object>().ToList();
                if (windowIndex >= windows.Count)
                {
                    ShowStatus("Fenster " + windowIndex + " existiert nicht.", Brushes.Salmon);
                    return;
                }

                var answer = MessageBox.Show(this,
                    "Fenster " + windowIndex + " wirklich komplett löschen? Das lässt sich nicht rückgängig machen.",
                    "Fenster löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;

                windows.RemoveAt(windowIndex);
                root["windows"] = windows;
                WriteAllTextAtomic(_layoutPath, serializer.Serialize(root));
                remainingWindows = windows.Count;
            }
            catch (Exception ex)
            {
                ShowStatus("Fehler beim Löschen: " + ex.Message, Brushes.Salmon);
                return;
            }

            // Zuerst den EIGENEN Zustand vollständig aktualisieren - der Callback
            // kann das Fenster schließen, aus dem das Setup geöffnet wurde (und damit
            // auch dieses Setup-Fenster), danach soll nichts mehr auf der UI passieren müssen.
            _currentWindowIndex = Math.Max(0, Math.Min(_currentWindowIndex, remainingWindows - 1));
            RefreshWindowButtons();
            LoadWindow(_currentWindowIndex); // setzt auch das Dirty-Flag zurück
            ShowStatus("Fenster " + windowIndex + " gelöscht und geschlossen." +
                (windowIndex < remainingWindows ? " Achtung: nachfolgende Fenster-Nummern sind nachgerückt." : ""), Brushes.LightGreen);

            try
            {
                _onWindowDeleted?.Invoke(windowIndex); // schließt gezielt genau dieses Fenster, falls offen - als LETZTES
            }
            catch (Exception ex)
            {
                ShowStatus("Fenster " + windowIndex + " aus layout.json gelöscht, aber das Schließen in der App ist fehlgeschlagen: " + ex.Message, Brushes.Salmon);
            }
        }

        // -----------------------------------------------------------------
        // Einlesen: layout.json -> Baum
        // -----------------------------------------------------------------

        /// <summary>
        /// Liest die Zellen eines Fenster-Objekts defensiv ein: fehlende/ungültige
        /// Zellen werden übersprungen (und gezählt), Spannen &lt; 1 auf 1 geklemmt,
        /// ein fehlender Name wird zu "(leer)".
        /// </summary>
        private static List<(int Row, int Col, int RowSpan, int ColSpan, string Name)> ParseCells(
            Dictionary<string, object> windowData, out int skippedCells)
        {
            skippedCells = 0;
            var cells = new List<(int Row, int Col, int RowSpan, int ColSpan, string Name)>();
            if (!windowData.TryGetValue("cells", out var cellsObj)) return cells;

            var cellList = AsList(cellsObj);
            if (cellList == null)
            {
                if (cellsObj != null) skippedCells = 1;
                return cells;
            }

            foreach (var item in cellList)
            {
                var cellObj = item as Dictionary<string, object>;
                if (cellObj == null || !TryGetInt(cellObj, "row", out int row) || !TryGetInt(cellObj, "col", out int col))
                {
                    skippedCells++;
                    continue;
                }

                if (!TryGetInt(cellObj, "rowSpan", out int rowSpan)) rowSpan = 1;
                if (!TryGetInt(cellObj, "colSpan", out int colSpan)) colSpan = 1;
                if (rowSpan < 1) rowSpan = 1;
                if (colSpan < 1) colSpan = 1;
                if (row < 0) row = 0;
                if (col < 0) col = 0;

                string name = cellObj.TryGetValue("name", out var nameObj) && nameObj != null ? Convert.ToString(nameObj) : null;
                if (string.IsNullOrEmpty(name)) name = "(leer)";

                cells.Add((row, col, rowSpan, colSpan, name));
            }
            return cells;
        }

        /// <summary>
        /// Liest das Fenster an "index" aus layout.json ein und baut den
        /// Aufteilungs-Baum daraus wieder auf (best-effort - siehe
        /// BuildTreeFromCells). Existiert die Datei/das Fenster (noch) nicht,
        /// wird eine leere Fläche angezeigt. Wirft nie - Probleme erscheinen in
        /// der Statuszeile.
        /// </summary>
        private void LoadWindow(int windowIndex)
        {
            _dirty = false;
            _reconstructionIncomplete = false;
            _splitCountInputs.Clear();
            _root = new SplitNode();

            var warnings = new List<string>();
            string errorText = null;
            try
            {
                var windows = ReadWindowsList(out string readProblem);
                if (readProblem != null) warnings.Add(readProblem);

                if (windowIndex >= 0 && windowIndex < windows.Count)
                {
                    var windowData = windows[windowIndex];
                    var cells = ParseCells(windowData, out int skippedCells);
                    if (skippedCells > 0)
                        warnings.Add(skippedCells + " fehlerhafte Zelle(n) übersprungen - beim Speichern gehen sie verloren.");

                    if (cells.Count > 0)
                    {
                        int rowUnits = TryGetInt(windowData, "rowUnits", out int ru) && ru > 0 ? ru : cells.Max(c => c.Row + c.RowSpan);
                        int colUnits = TryGetInt(windowData, "colUnits", out int cu) && cu > 0 ? cu : cells.Max(c => c.Col + c.ColSpan);

                        _root = BuildTreeFromCells(cells, 0, 0, rowUnits, colUnits) ?? new SplitNode();
                        DetectAutoWidthFlags(_root);
                        NormalizeWeightScales(_root);
                    }
                }
            }
            catch (Exception ex)
            {
                _root = new SplitNode();
                errorText = "Fehler beim Einlesen: " + ex.Message;
            }

            if (_reconstructionIncomplete)
                warnings.Add("Achtung: Diese Fläche war nicht vollständig als Aufteilung darstellbar (von Hand editiertes Layout?) - beim Speichern gehen dabei Zellen verloren.");

            RedrawTree();

            if (errorText != null) ShowStatus(errorText, Brushes.Salmon);
            else if (warnings.Count > 0) ShowStatus(string.Join(" ", warnings), Brushes.Orange);
        }

        /// <summary>
        /// Effektive "Zeilenanzahl" eines Knotens für den Breite-koppeln-Vergleich:
        /// bei einer Zeilen-Aufteilung Summe/kleinstes Kind-Gewicht (= wie viele
        /// "normale" Zeilen hineinpassen, bei gleich großen Zeilen schlicht die
        /// Kinderanzahl), sonst (Blatt oder Spalten-Aufteilung) 1.
        /// </summary>
        private static double GetEffectiveRowCount(SplitNode s)
        {
            if (s.IsLeaf || s.Direction != SplitDirection.Rows || s.Children.Count == 0) return 1.0;
            long total = s.Children.Sum(c => (long)c.Weight);
            long min = s.Children.Min(c => c.Weight);
            return min <= 0 ? s.Children.Count : (double)total / min;
        }

        /// <summary>
        /// Gemeinsame Referenz-Zeilenanzahl für die Kopplung einer Geschwister-
        /// Gruppe: das Minimum unter den NICHT gekoppelten Geschwistern (die sind
        /// per Definition "Normalbreite") - gibt es keine, das Minimum über ALLE
        /// Geschwister inkl. des Knotens selbst. Dadurch ist das Ergebnis für alle
        /// gekoppelten Geschwister dieselbe Referenz (z.B. 4 vs. 3 Zeilen = 3:4).
        /// </summary>
        private static double ComputeReferenceRows(SplitNode node, List<SplitNode> siblings)
        {
            var referenceSet = siblings.Where(s => s != node && !s.AutoWidth).ToList();
            if (referenceSet.Count == 0) referenceSet = siblings.ToList();
            if (referenceSet.Count == 0) return GetEffectiveRowCount(node);
            return referenceSet.Min(GetEffectiveRowCount);
        }

        /// <summary>
        /// Läuft nach dem Einlesen einmal durch den ganzen Baum und markiert
        /// Zeilen-Aufteilungen unter Spalten-Aufteilungen als "an Zeilen
        /// gekoppelt" (AutoWidth), deren aktuelle Breite GENAU dem entspricht, was
        /// die Kopplung berechnen würde - layout.json speichert nur das fertige
        /// Ergebnis, nicht ob es über die Kopplung entstanden ist, daher diese
        /// Erkennung anhand des Zahlenwerts. Markiert wird NUR, wenn die Fläche
        /// mehr Zeilen hat als die Referenz-Geschwister (Breite weicht also von
        /// der Normalbreite ab) - sonst würden z.B. 3 gleich breite Spalten mit je
        /// 3 Zeilen fälschlich als "gekoppelt" gelten.
        /// </summary>
        private void DetectAutoWidthFlags(SplitNode node)
        {
            if (node.IsLeaf) return;
            foreach (var child in node.Children)
                DetectAutoWidthFlags(child);

            if (node.Direction != SplitDirection.Columns || node.Children.Count == 0) return;

            // Referenz: Geschwister mit der kleinsten effektiven Zeilenanzahl (Normalbreite);
            // bei mehreren davon das mit dem größten Gewicht (wie bisher bei Gleichstand).
            double minRows = node.Children.Min(GetEffectiveRowCount);
            var referenceSibling = node.Children
                .Where(c => GetEffectiveRowCount(c) <= minRows + 1e-9)
                .OrderByDescending(c => c.Weight).First();

            // Erst ALLE prüfen, dann Flags setzen - sonst würde das Setzen die Prüfung der Nächsten beeinflussen.
            var toMark = new List<SplitNode>();
            foreach (var child in node.Children)
            {
                if (child.IsLeaf || child.Direction != SplitDirection.Rows || child.Children.Count == 0) continue;
                double rows = GetEffectiveRowCount(child);
                if (rows <= minRows + 1e-9) continue; // Normalbreite - nichts zu koppeln
                double expectedRatio = minRows / rows;
                double actualRatio = (double)child.Weight / referenceSibling.Weight;
                if (Math.Abs(actualRatio - expectedRatio) <= 0.01 * expectedRatio)
                    toMark.Add(child);
            }
            foreach (var child in toMark) child.AutoWidth = true;
        }

        /// <summary>
        /// Rechnet nach dem Einlesen die (beim Speichern gekürzten) Gewichte jeder
        /// Geschwister-Gruppe auf den gemeinsamen Maßstab NormalWeight um. Nur so
        /// passen die danach neu berechneten gekoppelten Breiten (RefreshAutoWidth,
        /// feste Skala) zu den übrigen Geschwistern - das Verhältnis bleibt
        /// dabei unverändert (z.B. 2:3:3 bleibt 2:3:3, nur auf größerer Skala).
        /// </summary>
        private void NormalizeWeightScales(SplitNode node)
        {
            if (node.IsLeaf) return;
            foreach (var child in node.Children)
                NormalizeWeightScales(child);
            NormalizeSiblingWeights(node.Children, null);
        }

        /// <summary>
        /// Skaliert die Gewichte aller Geschwister (außer "except") so, dass die
        /// Baseline (siehe ComputeBaselineWeight) genau NormalWeight entspricht.
        /// </summary>
        private static void NormalizeSiblingWeights(List<SplitNode> siblings, SplitNode except)
        {
            if (siblings.Count == 0) return;
            int baselineWeight = ComputeBaselineWeight(siblings);
            if (baselineWeight <= 0) return;
            double scale = (double)NormalWeight / baselineWeight;
            foreach (var sibling in siblings)
            {
                if (sibling == except) continue;
                sibling.Weight = Math.Max(1, (int)Math.Round(sibling.Weight * scale));
            }
        }

        /// <summary>
        /// Rekonstruiert (best-effort) einen Aufteilungs-Baum aus einer flachen
        /// Zellen-Liste für die Fläche (row, col, rowSpan, colSpan). Erkennt
        /// gleichmäßige Zeilen- ODER Spalten-Aufteilungen und verarbeitet jede
        /// Teilfläche rekursiv. Layouts, die NICHT als "Guillotine-Schnitt"
        /// darstellbar sind (z.B. windmühlenartige Anordnungen), werden nicht
        /// exotisch nachgebildet - in dem seltenen Fall wird die größte
        /// Einzelzelle als Blatt übernommen (kein Absturz, aber ggf. Datenverlust
        /// für diese eine Fläche - betrifft nur von Hand exotisch editierte
        /// Layouts, nie etwas, das dieser Designer selbst erzeugt hat; die
        /// Statuszeile warnt dann, siehe _reconstructionIncomplete).
        /// </summary>
        private SplitNode BuildTreeFromCells(List<(int Row, int Col, int RowSpan, int ColSpan, string Name)> cells,
            int row, int col, int rowSpan, int colSpan)
        {
            var relevant = cells.Where(c => c.Row >= row && c.Col >= col && c.Row < row + rowSpan && c.Col < col + colSpan).ToList();

            if (relevant.Count == 0)
                return new SplitNode { IsLeaf = true, GaugeName = "(leer)" };

            if (relevant.Count == 1 && relevant[0].Row == row && relevant[0].Col == col
                && relevant[0].RowSpan == rowSpan && relevant[0].ColSpan == colSpan)
                return new SplitNode { IsLeaf = true, GaugeName = relevant[0].Name };

            // Zeilen-Aufteilung versuchen (Bänder dürfen unterschiedlich hoch sein
            // - Gewichte -, UND unbelegte Bereiche werden als eigene "(leer)"-
            // Bänder erkannt, statt die Erkennung komplett scheitern zu lassen).
            var rowSplit = TryBuildBands(relevant, row, rowSpan, true, col, colSpan);
            if (rowSplit != null) return rowSplit;

            // Spalten-Aufteilung versuchen (analog).
            var colSplit = TryBuildBands(relevant, col, colSpan, false, row, rowSpan);
            if (colSplit != null) return colSplit;

            // Allgemeinerer Fallback: irgendeinen sauberen EINZELNEN Schnitt
            // suchen (nicht nur einheitliche Mehrfach-Bänder) - deckt
            // verschachtelte/"T-förmige" Muster ab, z.B. wenn in einer Spalte
            // oben 2 kleine Anzeigen nebeneinander UND darunter 1 große über die
            // volle Breite liegen (das ergibt kein einheitliches Band, aber
            // durchaus einen sauberen rekursiven Schnitt).
            var rowCut = TryBuildAnyCut(relevant, row, rowSpan, true, col, colSpan);
            if (rowCut != null) return rowCut;

            var colCut = TryBuildAnyCut(relevant, col, colSpan, false, row, rowSpan);
            if (colCut != null) return colCut;

            // Kein sauberer Guillotine-Schnitt gefunden (exotisches, von Hand
            // editiertes Muster) - größte Einzelzelle als Blatt übernehmen,
            // damit wenigstens etwas Sinnvolles angezeigt wird.
            _reconstructionIncomplete = true;
            var largest = relevant.OrderByDescending(c => (long)c.RowSpan * c.ColSpan).First();
            return new SplitNode { IsLeaf = true, GaugeName = largest.Name };
        }

        /// <summary>
        /// Sucht IRGENDEINE Position entlang einer Achse, an der sich alle
        /// Zellen sauber in zwei Gruppen trennen lassen (keine Zelle reicht über
        /// die Schnittposition hinweg) - allgemeiner als TryBuildBands, weil hier
        /// nicht verlangt wird, dass alle Zellen an einer Position gleich groß
        /// sind. Findet dadurch auch verschachtelte Muster, bei denen innerhalb
        /// eines Bereichs unterschiedlich große Unterteilungen vorkommen (z.B.
        /// zwei kleine Flächen nebeneinander UND darunter eine große). Gibt null
        /// zurück, wenn sich kein solcher Schnitt finden lässt.
        /// </summary>
        private SplitNode TryBuildAnyCut(List<(int Row, int Col, int RowSpan, int ColSpan, string Name)> relevant,
            int start, int span, bool isRow, int otherStart, int otherSpan)
        {
            var candidates = new SortedSet<int>();
            foreach (var c in relevant)
            {
                int s = isRow ? c.Row : c.Col;
                int e = s + (isRow ? c.RowSpan : c.ColSpan);
                if (s > start) candidates.Add(s);
                if (e < start + span) candidates.Add(e);
            }

            foreach (var cut in candidates)
            {
                bool separable = relevant.All(c =>
                {
                    int s = isRow ? c.Row : c.Col;
                    int e = s + (isRow ? c.RowSpan : c.ColSpan);
                    return e <= cut || s >= cut;
                });
                if (!separable) continue;

                var firstGroup = relevant.Where(c => (isRow ? c.Row + c.RowSpan : c.Col + c.ColSpan) <= cut).ToList();
                var secondGroup = relevant.Where(c => (isRow ? c.Row : c.Col) >= cut).ToList();
                if (firstGroup.Count == 0 || secondGroup.Count == 0) continue;

                int firstSize = cut - start;
                int secondSize = start + span - cut;

                SplitNode firstChild, secondChild;
                if (isRow)
                {
                    firstChild = BuildTreeFromCells(firstGroup, start, otherStart, firstSize, otherSpan);
                    secondChild = BuildTreeFromCells(secondGroup, cut, otherStart, secondSize, otherSpan);
                }
                else
                {
                    firstChild = BuildTreeFromCells(firstGroup, otherStart, start, otherSpan, firstSize);
                    secondChild = BuildTreeFromCells(secondGroup, otherStart, cut, otherSpan, secondSize);
                }
                if (firstChild == null || secondChild == null) continue;

                var weights = SimplifyToWeights(new List<int> { firstSize, secondSize });
                firstChild.Weight = weights[0];
                secondChild.Weight = weights[1];

                return new SplitNode
                {
                    IsLeaf = false,
                    Direction = isRow ? SplitDirection.Rows : SplitDirection.Columns,
                    Children = new List<SplitNode> { firstChild, secondChild }
                };
            }
            return null;
        }

        /// <summary>
        /// Versucht, die übergebenen Zellen entlang EINER Achse (Zeilen ODER
        /// Spalten, je nach isRow) in Bänder zu zerlegen - Bänder dürfen
        /// unterschiedlich groß sein (werden zu Gewichten), UND unbelegte
        /// Bereiche (Lücken zwischen/vor/nach den eigentlichen Zellen) werden
        /// automatisch als eigene "(leer)"-Bänder eingefügt, statt die gesamte
        /// Erkennung scheitern zu lassen. Gibt null zurück, wenn sich entlang
        /// dieser Achse kein sauberer Schnitt bilden lässt (z.B. weil Zellen sich
        /// überlappen oder unterschiedliche Größen innerhalb eines Bandes haben).
        /// </summary>
        private SplitNode TryBuildBands(List<(int Row, int Col, int RowSpan, int ColSpan, string Name)> relevant,
            int start, int span, bool isRow, int otherStart, int otherSpan)
        {
            var keys = relevant.Select(c => isRow ? c.Row : c.Col).Distinct().OrderBy(k => k).ToList();

            var bands = new List<(int Start, int Size, List<(int Row, int Col, int RowSpan, int ColSpan, string Name)> Cells)>();
            int cursor = start;
            foreach (var key in keys)
            {
                if (key < cursor) return null; // überlappende Zellen - kein sauberer Schnitt
                if (key > cursor)
                    bands.Add((cursor, key - cursor, new List<(int, int, int, int, string)>())); // Lücke = leeres Band

                var bandCells = relevant.Where(c => (isRow ? c.Row : c.Col) == key).ToList();
                int size = isRow ? bandCells[0].RowSpan : bandCells[0].ColSpan;
                if (bandCells.Any(c => (isRow ? c.RowSpan : c.ColSpan) != size)) return null; // uneinheitliche Größe im selben Band
                bands.Add((key, size, bandCells));
                cursor = key + size;
            }
            if (cursor < start + span)
                bands.Add((cursor, start + span - cursor, new List<(int, int, int, int, string)>())); // Lücke am Ende
            else if (cursor > start + span)
                return null; // reicht über den Rand hinaus - kein sauberer Schnitt

            if (bands.Count <= 1) return null; // keine echte Aufteilung entlang dieser Achse

            var weights = SimplifyToWeights(bands.Select(b => b.Size).ToList());
            var node = new SplitNode { IsLeaf = false, Direction = isRow ? SplitDirection.Rows : SplitDirection.Columns };
            var children = new List<SplitNode>();
            for (int i = 0; i < bands.Count; i++)
            {
                SplitNode child;
                if (bands[i].Cells.Count == 0)
                {
                    child = new SplitNode { IsLeaf = true, GaugeName = "(leer)" };
                }
                else
                {
                    child = isRow
                        ? BuildTreeFromCells(bands[i].Cells, bands[i].Start, otherStart, bands[i].Size, otherSpan)
                        : BuildTreeFromCells(bands[i].Cells, otherStart, bands[i].Start, otherSpan, bands[i].Size);
                    if (child == null) return null;
                }
                child.Weight = weights[i];
                children.Add(child);
            }
            node.Children = children;
            return node;
        }

        /// <summary>
        /// Wandelt eine Liste absoluter Bandgrößen in die kleinstmöglichen
        /// ganzzahligen Gewichte um (z.B. [2,3,3] -> [2,3,3], [4,6,6] -> [2,3,3]) -
        /// per größtem gemeinsamen Teiler. Größen &lt;= 0 werden auf 1 geklemmt
        /// (keine Division durch 0).
        /// </summary>
        private static List<int> SimplifyToWeights(List<int> sizes)
        {
            var safeSizes = sizes.Select(s => Math.Max(1, s)).ToList();
            long g = safeSizes[0];
            foreach (var s in safeSizes.Skip(1)) g = Frac.Gcd(g, s);
            if (g <= 0) g = 1;
            return safeSizes.Select(s => (int)(s / g)).ToList();
        }

        // -----------------------------------------------------------------
        // Anzeige: Baum -> UI
        // -----------------------------------------------------------------

        private double _maxLeafArea = 1.0; // Fläche der größten Anzeige in diesem Fenster (als Anteil der Fensterfläche) - Bezugsgröße für den Prozent-Vergleich an jeder Anzeige

        private void RedrawTree()
        {
            SlotsGrid.RowDefinitions.Clear();
            SlotsGrid.ColumnDefinitions.Clear();
            SlotsGrid.Children.Clear();
            SlotsGrid.RowDefinitions.Add(new RowDefinition());
            SlotsGrid.ColumnDefinitions.Add(new ColumnDefinition());

            // Gekoppelte Breiten VOR der Flächenberechnung aktualisieren, damit die
            // Prozentangaben zur tatsächlich gezeichneten Aufteilung passen.
            RefreshAutoWidthsInTree(_root);

            // Größte Anzeige im ganzen Baum ermitteln (per Fläche) - Bezugsgröße
            // für den Prozent-Vergleich "X% der größten Anzeige" an jeder Fläche,
            // damit z.B. 4 gleich große Anzeigen in einem 2x2-Raster nicht nur
            // "Normal" (zueinander) zeigen, sondern auch "25% der größten
            // Anzeige", falls es im selben Fenster größere Anzeigen gibt.
            var leaves = new List<(SplitNode Leaf, Frac X, Frac Y, Frac W, Frac H)>();
            ComputeLeafRects(_root, new Frac(0, 1), new Frac(0, 1), new Frac(1, 1), new Frac(1, 1), leaves);
            _maxLeafArea = leaves.Count == 0 ? 1.0 : leaves.Max(l => ((double)l.W.Num / l.W.Den) * ((double)l.H.Num / l.H.Den));
            if (_maxLeafArea <= 0) _maxLeafArea = 1.0;

            // Für den Wurzelknoten gibt es keine Geschwister (siblings = null) -
            // "eigene Größe" ist dort bedeutungslos (füllt immer 100%), daher zeigt
            // BuildNodeVisual dort gar keine Größen-Auswahl an. Breite/Höhe
            // starten bei 1/1 (volles Fenster) und werden beim Runterreichen durch
            // den Baum entsprechend verkleinert - daraus berechnet sich die an
            // jeder Anzeige gezeigte tatsächliche Endgröße.
            var visual = BuildNodeVisual(_root, null, false, new Frac(1, 1), new Frac(1, 1));
            Grid.SetRow(visual, 0);
            Grid.SetColumn(visual, 0);
            SlotsGrid.Children.Add(visual);
        }

        /// <summary>
        /// Berechnet rekursiv für den ganzen Baum die Breiten aller gekoppelten
        /// (AutoWidth) Flächen neu - so bleibt die Breite korrekt, auch wenn sich
        /// die eigene Zeilenanzahl seit dem Einschalten geändert hat, ohne dass
        /// der Umschalter erneut betätigt werden muss.
        /// </summary>
        private void RefreshAutoWidthsInTree(SplitNode node)
        {
            if (node.IsLeaf) return;
            if (node.Direction == SplitDirection.Columns)
            {
                foreach (var child in node.Children)
                    if (child.AutoWidth && !child.IsLeaf && child.Direction == SplitDirection.Rows)
                        RefreshAutoWidth(child, node.Children);
            }
            foreach (var child in node.Children)
                RefreshAutoWidthsInTree(child);
        }

        /// <summary>
        /// Ermittelt den "Normal"-Bezugswert unter Geschwister-Gewichten: den
        /// häufigsten Wert (bei Gleichstand den größten) - relativ dazu wird die
        /// Größe jeder Fläche verstanden. Wichtig, weil gespeicherte Gewichte beim
        /// Schreiben auf den kleinstmöglichen Bruch gekürzt werden (z.B. 8:12 ->
        /// 2:3) - der Bezug zu "was ist normal" muss also aus den GESCHWISTERN
        /// abgeleitet werden, nicht aus einer festen Zahl.
        /// </summary>
        private static int ComputeBaselineWeight(List<SplitNode> siblings)
        {
            // Bereits gekoppelte Geschwister (AutoWidth=true) NICHT als Referenz
            // für "was ist Normal" verwenden - ihr Gewicht ist ja gerade das
            // ABGELEITETE Ergebnis einer Kopplung, nicht die eigentliche
            // Normalgröße. Ohne diesen Ausschluss könnte z.B. ein Aus-/Wieder-
            // Einschalten die Referenz auf einen bereits verkleinerten Wert
            // verschieben und dadurch andere, tatsächlich normale Geschwister
            // ungewollt vergrößern.
            var candidates = siblings.Where(s => !s.AutoWidth).ToList();
            if (candidates.Count == 0) candidates = siblings;

            return candidates
                .GroupBy(s => s.Weight)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First().Key;
        }

        /// <summary>
        /// Kurzer, für Menschen lesbarer Bruch für die Anzeige (z.B. "1/8" oder
        /// "1/1" für "volle Breite/Höhe") - Frac ist durch seinen Konstruktor
        /// bereits gekürzt, hier geht es nur um die Textdarstellung.
        /// </summary>
        private static string FormatFrac(Frac f) => f.Num + "/" + f.Den;

        /// <summary>
        /// Baut die Darstellung eines Knotens: bei einem Split ein Grid mit den
        /// Kind-Flächen (gewichtet nach node.Weight - die Vorschau zeigt dadurch
        /// die ECHTEN Größenverhältnisse, nicht nur gleich große Kästen), bei
        /// einem Blatt eine kompakte Karte mit Anzeigen-Auswahl, den
        /// Aufteilen-Knöpfen und der TATSÄCHLICHEN Endgröße (Breite/Höhe als
        /// Bruch vom Gesamtfenster - das beantwortet direkt "wie groß wird das
        /// wirklich", ohne dass man den Baum im Kopf durchrechnen muss).
        /// siblings sind die Geschwister dieses Knotens (null beim Wurzelknoten),
        /// parentIsColumns sagt, ob der Elternknoten eine Spalten-Aufteilung ist
        /// (nur dann ist "Breite koppeln" sinnvoll). widthFrac/heightFrac sind die
        /// vom Wurzelknoten bis hierhin akkumulierten Anteile der Gesamtbreite/-höhe.
        /// </summary>
        private UIElement BuildNodeVisual(SplitNode node, List<SplitNode> siblings, bool parentIsColumns, Frac widthFrac, Frac heightFrac)
        {
            if (!node.IsLeaf)
            {
                var outerGrid = new Grid();
                outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // Kopfleiste: Richtung links, Koppeln-Umschalter und
                // Zusammenführen-Knopf UNTEREINANDER (statt nebeneinander) -
                // damit die Kopfzeile in der Breite nicht unnötig aufträgt.
                var headerText = node.Direction == SplitDirection.Rows ? "⬍ Zeilen" : "⬌ Spalten";
                var header = new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x38)), Padding = new Thickness(6, 3, 6, 3) };
                var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                headerPanel.Children.Add(new TextBlock { Text = headerText, Foreground = Brushes.LightGray, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                var headerButtonsStack = new StackPanel { Orientation = Orientation.Vertical };
                // Hauptweg für unterschiedliche Größen: koppelt die Breite an die eigene
                // Zeilenanzahl - nur sinnvoll für eine Zeilen-Aufteilung unter einer Spalten-Aufteilung.
                if (siblings != null && parentIsColumns && node.Direction == SplitDirection.Rows)
                    headerButtonsStack.Children.Add(BuildAutoWidthToggle(node, siblings));
                var mergeButton = new Button
                {
                    Content = "⊟ Zusammenführen",
                    FontSize = 10,
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Diese Aufteilung wieder zu einer einzelnen Fläche zusammenführen (die erste zugewiesene Anzeige bleibt erhalten)"
                };
                mergeButton.Click += (s, e) =>
                {
                    string keepName = FindFirstNonEmptyGauge(node) ?? "(leer)";
                    node.IsLeaf = true;
                    node.GaugeName = keepName;
                    node.Children = new List<SplitNode>();
                    node.AutoWidth = false; // eine zusammengeführte Fläche hat keine Zeilen mehr, an die sich koppeln ließe
                    MarkDirty();
                    RedrawTree();
                };
                var addChildButton = new Button
                {
                    Content = "+ Fläche hinzufügen",
                    FontSize = 10,
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Fügt dieser Aufteilung eine weitere, leere Fläche hinzu (z.B. eine 6. Spalte), ohne die bestehenden Flächen zu verändern."
                };
                addChildButton.Click += (s, e) =>
                {
                    // Neues Kind bekommt das gleiche Gewicht wie die bestehenden
                    // Geschwister (nicht blind den Standardwert) - sonst würde es bei
                    // z.B. anders skalierten Gewichten alle anderen komplett dominieren.
                    int matchingWeight = node.Children.Count > 0 ? ComputeBaselineWeight(node.Children) : NormalWeight;
                    node.Children.Add(new SplitNode { Weight = matchingWeight });
                    MarkDirty();
                    RedrawTree();
                };
                headerButtonsStack.Children.Add(addChildButton);
                headerButtonsStack.Children.Add(mergeButton);
                headerPanel.Children.Add(headerButtonsStack);
                header.Child = headerPanel;
                Grid.SetRow(header, 0);
                outerGrid.Children.Add(header);

                var childrenGrid = new Grid();
                int n = node.Children.Count;

                // (Gekoppelte Breiten wurden bereits in RedrawTree aktualisiert.)
                for (int i = 0; i < n; i++)
                {
                    var star = new GridLength(node.Children[i].Weight, GridUnitType.Star);
                    if (node.Direction == SplitDirection.Rows)
                        childrenGrid.RowDefinitions.Add(new RowDefinition { Height = star });
                    else
                        childrenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
                }
                long totalWeight = node.Children.Sum(c => (long)c.Weight);
                if (totalWeight <= 0) totalWeight = 1;
                for (int i = 0; i < n; i++)
                {
                    Frac childWidth = widthFrac;
                    Frac childHeight = heightFrac;
                    var childShare = new Frac(node.Children[i].Weight, totalWeight);
                    if (node.Direction == SplitDirection.Rows)
                        childHeight = heightFrac * childShare.Num / childShare.Den;
                    else
                        childWidth = widthFrac * childShare.Num / childShare.Den;

                    var childVisual = BuildNodeVisual(node.Children[i], node.Children, node.Direction == SplitDirection.Columns, childWidth, childHeight);
                    if (node.Direction == SplitDirection.Rows)
                    {
                        Grid.SetRow(childVisual, i);
                        Grid.SetColumn(childVisual, 0);
                    }
                    else
                    {
                        Grid.SetRow(childVisual, 0);
                        Grid.SetColumn(childVisual, i);
                    }
                    childrenGrid.Children.Add(childVisual);
                }
                Grid.SetRow(childrenGrid, 1);
                outerGrid.Children.Add(childrenGrid);

                return outerGrid;
            }

            bool isEmpty = node.GaugeName == "(leer)";
            var outer = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(isEmpty ? (byte)0x3a : (byte)0x55, isEmpty ? (byte)0x3a : (byte)0x55, isEmpty ? (byte)0x3a : (byte)0x55)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(isEmpty ? Color.FromRgb(0x1e, 0x1e, 0x1e) : Color.FromRgb(0x2a, 0x2a, 0x2a))
            };

            // Alle Regler UNTEREINANDER statt eng nebeneinander gequetscht - bleibt
            // auch in schmalen/kleinen Flächen lesbar und anklickbar.
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) };

            var gaugeBox = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 4),
                ItemsSource = AvailableGauges,
                SelectedItem = node.GaugeName,
                FontWeight = isEmpty ? FontWeights.Normal : FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            gaugeBox.SelectionChanged += (s, e) =>
            {
                node.GaugeName = (string)gaugeBox.SelectedItem;
                MarkDirty();
                RedrawTree(); // Farbe (leer/belegt) aktualisieren
            };
            stack.Children.Add(gaugeBox);

            // Tatsächliche Endgröße relativ zum GESAMTEN Fenster - beantwortet
            // direkt "wie groß wird das wirklich", unabhängig davon, wie oft
            // vorher schon (an dieser oder anderer Stelle im Baum) aufgeteilt
            // wurde. Zusätzlich der Vergleich zur GRÖSSTEN Anzeige im Fenster
            // (in Prozent) - das ist die Zahl, die man intuitiv erwartet (z.B.
            // "25%" bei 4 gleich großen Anzeigen in einem 2x2-Raster), auch wenn
            // die "Größe"-Auswahl direkt darunter nur "Normal" zu den direkten
            // Nachbarn zeigt. Nur anzeigen, wenn nicht das alleinige
            // Wurzelelement (dort wäre das ohnehin trivial 100%/"1/1 · 1/1").
            if (siblings != null)
            {
                double thisArea = ((double)widthFrac.Num / widthFrac.Den) * ((double)heightFrac.Num / heightFrac.Den);
                int percentOfLargest = (int)Math.Round(100.0 * thisArea / _maxLeafArea);
                stack.Children.Add(new TextBlock
                {
                    Text = "≈ " + percentOfLargest + "% der größten Anzeige (" + FormatFrac(widthFrac) + " breit · " + FormatFrac(heightFrac) + " hoch)",
                    FontSize = 9,
                    Foreground = Brushes.LightBlue,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                    ToolTip = "Tatsächliche Endgröße: Anteil an der größten Anzeige im Fenster, sowie an der gesamten Fensterbreite/-höhe"
                });
                var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                resetRow.Children.Add(BuildResetToNormalButton(node, siblings));
                stack.Children.Add(resetRow);
            }

            var splitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            splitRow.Children.Add(BuildSplitControl(node, "⬍", SplitDirection.Rows, "In Zeilen teilen"));
            splitRow.Children.Add(BuildSplitControl(node, "⬌", SplitDirection.Columns, "In Spalten teilen"));
            stack.Children.Add(splitRow);

            outer.Child = stack;
            return outer;
        }

        /// <summary>
        /// Berechnet NUR node's eigenes Gewicht neu (relativ zur festen
        /// NormalWeight-Referenz), OHNE die Geschwister anzufassen. Wird bei
        /// JEDEM Neuzeichnen für automatisch gekoppelte Flächen aufgerufen
        /// (siehe RefreshAutoWidthsInTree) - deshalb DARF das die Geschwister
        /// nicht jedes Mal neu skalieren, sonst schaukelt sich das mit jeder
        /// weiteren Interaktion immer weiter hoch (siehe ApplyAutoWidth für den
        /// einmaligen Normalisierungs-Schritt beim Einschalten).
        /// </summary>
        private void RefreshAutoWidth(SplitNode node, List<SplitNode> siblings)
        {
            if (node.Children.Count == 0) return;

            // Referenz = gemeinsames Minimum der Zeilenanzahl (siehe
            // ComputeReferenceRows): die am WENIGSTEN unterteilte Fläche ist die
            // eigentliche "voll"-Referenz, an der sich MainWindow beim Berechnen der
            // Fenstergröße orientiert (größter rowSpan) - und für alle gekoppelten
            // Geschwister dieselbe, damit z.B. 4 vs. 3 Zeilen sauber 3:4 ergibt.
            double referenceRows = ComputeReferenceRows(node, siblings);
            double ratio = referenceRows / GetEffectiveRowCount(node);
            node.Weight = Math.Max(1, (int)Math.Round(NormalWeight * ratio));
        }

        /// <summary>
        /// EINMALIGER Schritt beim Einschalten von "Breite koppeln": normalisiert
        /// zuerst die Geschwister-Gruppe auf unseren Standard-Maßstab
        /// (NormalWeight) - das ist bewusst NUR hier (beim expliziten Einschalten
        /// durch den Nutzer), NICHT bei jedem automatischen Neuberechnen
        /// (RefreshAutoWidth), da sonst jede weitere Interaktion die Geschwister
        /// immer weiter hochskalieren würde (Aufschaukel-Effekt).
        /// </summary>
        private void ApplyAutoWidth(SplitNode node, List<SplitNode> siblings)
        {
            NormalizeSiblingWeights(siblings, node);
            RefreshAutoWidth(node, siblings);
        }

        /// <summary>
        /// Einzelner Umschalter statt zweier getrennter Knöpfe: koppelt die
        /// Breite dieser Fläche an ihre eigene Zeilenanzahl (AN) oder löst die
        /// Kopplung wieder (AUS, zurück auf "Normal"). Im AN-Zustand wird die
        /// Breite bei jeder Änderung automatisch neu berechnet (siehe
        /// RefreshAutoWidthsInTree) - ändert man später die Zeilenanzahl, passt
        /// sich die Breite von selbst mit an, ohne den Knopf erneut betätigen zu
        /// müssen.
        /// </summary>
        private UIElement BuildAutoWidthToggle(SplitNode node, List<SplitNode> siblings)
        {
            var toggle = new ToggleButton
            {
                Content = "🔗 Breite an Zeilen koppeln",
                FontSize = 10,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 6, 0),
                IsChecked = node.AutoWidth,
                ToolTip = "Wenn aktiv: die Breite dieser Fläche wird automatisch so berechnet, dass ihre Zellen quadratisch bleiben - basierend darauf, dass diese Fläche in " + node.Children.Count + " Zeilen aufgeteilt ist. Passt sich automatisch an, wenn sich die Zeilenanzahl später ändert."
            };
            toggle.Checked += (s, e) =>
            {
                node.AutoWidth = true;
                ApplyAutoWidth(node, siblings);
                MarkDirty();
                RedrawTree();
            };
            toggle.Unchecked += (s, e) =>
            {
                // Reihenfolge wichtig: Baseline/Rescale BEVOR AutoWidth auf false
                // gesetzt wird - sonst würde der eigene (noch veraltete) Gewichts-
                // wert die Berechnung mit verfälschen (siehe ComputeBaselineWeight).
                NormalizeSiblingWeights(siblings, node);
                node.AutoWidth = false;
                node.Weight = NormalWeight; // zurück auf "Normal"
                MarkDirty();
                RedrawTree();
            };
            return toggle;
        }

        /// <summary>
        /// Setzt diese Fläche wieder auf "Normal" zurück (Verhältnis 1:1 zu den
        /// Geschwistern) - die naheliegende Art, eine vorherige Kopplung oder
        /// manuelle Größen-Änderung rückgängig zu machen.
        /// </summary>
        private UIElement BuildResetToNormalButton(SplitNode node, List<SplitNode> siblings)
        {
            var button = new Button
            {
                Content = "↩ Normal",
                FontSize = 10,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Setzt diese Fläche wieder auf 'Normal' zurück (gleich groß wie eine normale Geschwister-Fläche) - macht z.B. eine Kopplung oder eine manuelle Größe rückgängig."
            };
            button.Click += (s, e) =>
            {
                NormalizeSiblingWeights(siblings, node);
                node.Weight = NormalWeight; // "Normal"
                MarkDirty();
                RedrawTree();
            };
            return button;
        }

        private static string FindFirstNonEmptyGauge(SplitNode node)
        {
            if (node.IsLeaf) return node.GaugeName != "(leer)" ? node.GaugeName : null;
            foreach (var child in node.Children)
            {
                var found = FindFirstNonEmptyGauge(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Kompakter "Teilen"-Knopf (Pfeil-Symbol) + Anzahl, um dieses Blatt in N
        /// gleich große Teilflächen (2 bis MaxSplitCount) in der angegebenen
        /// Richtung aufzuteilen. Die erste Teilfläche übernimmt die bisherige
        /// Anzeige. Die Eingabe im Anzahl-Feld wird pro Knoten gemerkt und geht
        /// beim Neuzeichnen anderer Flächen nicht verloren.
        /// </summary>
        private UIElement BuildSplitControl(SplitNode node, string icon, SplitDirection direction, string tooltip)
        {
            var key = (node, direction);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            var countBox = new TextBox
            {
                Width = 22,
                Text = _splitCountInputs.TryGetValue(key, out var storedText) ? storedText : "2",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
                ToolTip = "Anzahl Teilflächen (2 bis " + MaxSplitCount + ")"
            };
            countBox.TextChanged += (s, e) => _splitCountInputs[key] = countBox.Text;
            var splitButton = new Button { Content = icon, Padding = new Thickness(5, 1, 5, 1), ToolTip = tooltip };
            splitButton.Click += (s, e) =>
            {
                if (!int.TryParse(countBox.Text, out int n) || n < 2) n = 2;
                bool clamped = false;
                if (n > MaxSplitCount) { n = MaxSplitCount; clamped = true; }

                string currentGauge = node.GaugeName;
                node.IsLeaf = false;
                node.Direction = direction;
                node.AutoWidth = false; // neue Aufteilung = Kopplung bewusst neu wählen
                node.Children = new List<SplitNode>();
                for (int i = 0; i < n; i++)
                    node.Children.Add(new SplitNode { IsLeaf = true, GaugeName = i == 0 ? currentGauge : "(leer)" });
                MarkDirty();
                RedrawTree();
                if (clamped) ShowStatus("Anzahl auf " + MaxSplitCount + " begrenzt.", Brushes.Orange);
            };
            panel.Children.Add(splitButton);
            panel.Children.Add(countBox);
            return panel;
        }

        // -----------------------------------------------------------------
        // Speichern: Baum -> layout.json
        // -----------------------------------------------------------------

        /// <summary>
        /// Berechnet für jedes Blatt seine exakte Position/Ausdehnung als Bruch
        /// (0..1 normiert), rekursiv über den Baum.
        /// </summary>
        private void ComputeLeafRects(SplitNode node, Frac x, Frac y, Frac w, Frac h,
            List<(SplitNode Leaf, Frac X, Frac Y, Frac W, Frac H)> result)
        {
            if (node.IsLeaf)
            {
                result.Add((node, x, y, w, h));
                return;
            }

            // Gewichtete Aufteilung statt gleicher Teile - gleiche Gewichte bei
            // allen Kindern ergeben weiterhin gleich große Teile.
            long totalWeight = node.Children.Sum(c => (long)c.Weight);
            if (totalWeight <= 0) totalWeight = 1;
            if (node.Direction == SplitDirection.Rows)
            {
                Frac accY = y;
                foreach (var child in node.Children)
                {
                    var childH = h * child.Weight / totalWeight;
                    ComputeLeafRects(child, x, accY, w, childH, result);
                    accY = accY + childH;
                }
            }
            else
            {
                Frac accX = x;
                foreach (var child in node.Children)
                {
                    var childW = w * child.Weight / totalWeight;
                    ComputeLeafRects(child, accX, y, childW, h, result);
                    accX = accX + childW;
                }
            }
        }

        /// <summary>Kleinstes gemeinsames Vielfaches mit Überlaufprüfung (false = zu groß für long).</summary>
        private static bool TryLcm(long a, long b, out long result)
        {
            try
            {
                result = checked(a / Frac.Gcd(a, b) * b);
                return true;
            }
            catch (OverflowException)
            {
                result = 0;
                return false;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            TrySaveLayout();
        }

        /// <summary>
        /// Erzeugt aus dem aktuellen Baum das Layout FÜR DIESES EINE FENSTER
        /// (rowUnits/colUnits/cells) und schreibt es an Position "Fenster-Nr." im
        /// 'windows'-Array von layout.json - andere, bereits vorhandene Fenster in
        /// der Datei bleiben dabei unangetastet erhalten (null-Einträge werden
        /// dabei entfernt, damit Designer- und App-Index übereinstimmen).
        /// Gibt true zurück, wenn die Datei geschrieben wurde (auch wenn das
        /// anschließende Neuladen in der App fehlschlug).
        /// </summary>
        private bool TrySaveLayout()
        {
            var leaves = new List<(SplitNode Leaf, Frac X, Frac Y, Frac W, Frac H)>();
            ComputeLeafRects(_root, new Frac(0, 1), new Frac(0, 1), new Frac(1, 1), new Frac(1, 1), leaves);

            // Gemeinsamen Nenner (kgV) je Achse finden, damit alle Positionen/
            // Ausdehnungen als ganze Zahlen dargestellt werden können - mit
            // Obergrenze, damit keine absurd großen Raster entstehen.
            long rowScale = 1, colScale = 1;
            bool tooLarge = false;
            foreach (var l in leaves)
            {
                if (!TryLcm(rowScale, l.Y.Den, out rowScale) || !TryLcm(rowScale, l.H.Den, out rowScale) ||
                    !TryLcm(colScale, l.X.Den, out colScale) || !TryLcm(colScale, l.W.Den, out colScale) ||
                    rowScale > MaxGridUnits || colScale > MaxGridUnits)
                {
                    tooLarge = true;
                    break;
                }
            }
            if (tooLarge)
            {
                ShowStatus("Nicht gespeichert: Die Aufteilung ist zu fein (Raster größer als " + MaxGridUnits +
                    " Einheiten je Richtung). Bitte Aufteilung vereinfachen, weniger Zeilen koppeln oder Gewichte mit '↩ Normal' zurücksetzen.", Brushes.Salmon);
                return false;
            }

            var cellDicts = new List<Dictionary<string, object>>();
            foreach (var l in leaves)
            {
                // Leere Felder werden bewusst MIT gespeichert (mit
                // "name": "(leer)") - sonst geht beim Speichern die Grenze
                // zwischen zwei benachbarten leeren Feldern verloren, und beim
                // Wiedereinlesen sehen sie wie EIN größeres, unzerteiltes leeres
                // Feld aus. Zur Laufzeit hat das keinen Effekt: MainWindow
                // erstellt für einen unbekannten Namen ohnehin einfach keine
                // Anzeige an dieser Stelle (siehe CreateGauge -> default: null).
                string name = string.IsNullOrEmpty(l.Leaf.GaugeName) ? "(leer)" : l.Leaf.GaugeName;
                cellDicts.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["row"] = (int)(l.Y.Num * (rowScale / l.Y.Den)),
                    ["col"] = (int)(l.X.Num * (colScale / l.X.Den)),
                    ["rowSpan"] = (int)(l.H.Num * (rowScale / l.H.Den)),
                    ["colSpan"] = (int)(l.W.Num * (colScale / l.W.Den))
                });
            }

            var thisWindow = new Dictionary<string, object>
            {
                ["rowUnits"] = (int)rowScale,
                ["colUnits"] = (int)colScale,
                ["cells"] = cellDicts
            };

            int windowIndex;
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                Dictionary<string, object> root;
                if (File.Exists(_layoutPath))
                {
                    root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_layoutPath))
                           ?? new Dictionary<string, object>();
                }
                else
                {
                    root = new Dictionary<string, object>();
                }

                // Bestehende Fenster-Liste übernehmen (falls vorhanden, kompaktiert),
                // sonst neu anlegen - und das aktuell bearbeitete Fenster ersetzen bzw.
                // ergänzen, alle anderen bleiben unverändert.
                var windows = root.TryGetValue("windows", out var windowsObj)
                    ? CompactWindows(windowsObj, out _).Cast<object>().ToList()
                    : new List<object>();

                windowIndex = Math.Min(_currentWindowIndex, windows.Count); // keine Lücken (null) mehr erzeugen
                if (windowIndex == windows.Count) windows.Add(thisWindow);
                else windows[windowIndex] = thisWindow;
                _currentWindowIndex = windowIndex;

                root["windows"] = windows;

                WriteAllTextAtomic(_layoutPath, serializer.Serialize(root));
            }
            catch (Exception ex)
            {
                ShowStatus("Fehler beim Speichern: " + ex.Message, Brushes.Salmon);
                return false;
            }

            // Ab hier ist die Datei geschrieben - Fehler beim Neuladen der App sind KEIN Speicherfehler.
            _dirty = false;
            _reconstructionIncomplete = false;
            RefreshWindowButtons();

            string callbackError = null;
            try
            {
                _onSaved?.Invoke();
            }
            catch (Exception ex)
            {
                callbackError = ex.Message;
            }

            if (callbackError != null)
                ShowStatus("Gespeichert (Fenster " + windowIndex + "), aber Neuladen fehlgeschlagen: " + callbackError, Brushes.Orange);
            else
                ShowStatus("Gespeichert und übernommen (Fenster " + windowIndex + ", " + cellDicts.Count + " Anzeige(n)).", Brushes.LightGreen);
            return true;
        }

        // -----------------------------------------------------------------
        // Debug-Werte-Reiter
        // -----------------------------------------------------------------

        /// <summary>
        /// Liest den aktuellen Stand aller angefragten SimConnect-Variablen aus der
        /// geteilten SimConnectService-Instanz und zeigt sie (alphabetisch sortiert)
        /// im "Debug-Werte"-Reiter an. Nur wenn dieser Reiter aktiv ist, und die
        /// vorhandenen Einträge werden in-place aktualisiert (ObservableCollection),
        /// damit Scroll-Position und Auswahl nicht jede Sekunde springen.
        /// </summary>
        private void RefreshDebugValues()
        {
            if (_service == null || !DebugTab.IsSelected) return;

            try
            {
                var snapshot = _service.GetLatestValuesSnapshot();

                // Entfallene Variablen entfernen.
                foreach (var gone in _debugValueRows.Keys.Where(k => !snapshot.ContainsKey(k)).ToList())
                {
                    _debugValueItems.Remove(_debugValueRows[gone]);
                    _debugValueRows.Remove(gone);
                }

                // Vorhandene aktualisieren, neue alphabetisch an der richtigen Stelle einfügen.
                foreach (var kv in snapshot.OrderBy(k => k.Key))
                {
                    string valueText = kv.Value.ToString(CultureInfo.InvariantCulture);
                    if (_debugValueRows.TryGetValue(kv.Key, out var existingRow))
                    {
                        existingRow.Value = valueText;
                        continue;
                    }

                    var newRow = new DebugValueRow { Name = kv.Key, Value = valueText };
                    int insertAt = 0;
                    while (insertAt < _debugValueItems.Count &&
                           string.Compare(_debugValueItems[insertAt].Name, kv.Key, StringComparison.CurrentCulture) < 0)
                        insertAt++;
                    _debugValueItems.Insert(insertAt, newRow);
                    _debugValueRows[kv.Key] = newRow;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("LayoutDesigner: Debug-Werte konnten nicht aktualisiert werden - " + ex.Message);
            }
        }

        // -----------------------------------------------------------------
        // Einstellungen-Reiter (settings.json)
        // -----------------------------------------------------------------

        /// <summary>
        /// Füllt den "Einstellungen"-Reiter mit den Werten aus settings.json
        /// (frisch von der Platte gelesen - AppSettings kennt nur den Stand beim
        /// Programmstart und wäre nach einem Speichern veraltet). Fehlt die Datei
        /// oder ein Wert, gilt der Wert aus AppSettings.
        /// </summary>
        private void LoadSettingsIntoUi()
        {
            Dictionary<string, object> root = null;
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_settingsPath));
                }
            }
            catch { /* defekte Datei - mit den Werten aus AppSettings weiterarbeiten */ }
            if (root == null) root = new Dictionary<string, object>();

            var turnCoordinator = root.TryGetValue("turnCoordinator", out var tcObj) ? tcObj as Dictionary<string, object> : null;
            var windowBackground = root.TryGetValue("windowBackground", out var bgObj) ? bgObj as Dictionary<string, object> : null;

            RefreshIntervalBox.Text = ((int)ReadNumber(root, "refreshIntervalMs", AppSettings.RefreshIntervalMs)).ToString(CultureInfo.InvariantCulture);
            GaugeCellSizeBox.Text = ReadNumber(root, "gaugeCellSize", AppSettings.GaugeCellSize).ToString(CultureInfo.InvariantCulture);
            DebugLoggingCheckBox.IsChecked = root.TryGetValue("debugLogging", out var debugObj) && debugObj is bool debugFlag
                ? debugFlag : AppSettings.DebugLoggingEnabled;
            BallDivisorBox.Text = ReadNumber(turnCoordinator, "ballDivisor", AppSettings.TurnCoordinatorBallDivisor).ToString(CultureInfo.InvariantCulture);
            VacuumThresholdBox.Text = ReadNumber(turnCoordinator, "vacuumThreshold", AppSettings.TurnCoordinatorVacuumThreshold).ToString(CultureInfo.InvariantCulture);
            AttitudeVacuumThresholdBox.Text = ReadNumber(root, "attitudeVacuumThreshold", AppSettings.AttitudeVacuumThreshold).ToString(CultureInfo.InvariantCulture);
            OilPressOffsetBox.Text = ReadNumber(root, "oilPressAtmosphericOffsetPsi", AppSettings.OilPressAtmosphericOffsetPsi).ToString(CultureInfo.InvariantCulture);
            EgtMinFBox.Text = ReadNumber(root, "egtMinF", AppSettings.EgtMinF).ToString(CultureInfo.InvariantCulture);
            EgtMaxFBox.Text = ReadNumber(root, "egtMaxF", AppSettings.EgtMaxF).ToString(CultureInfo.InvariantCulture);

            string mode = windowBackground != null && windowBackground.TryGetValue("mode", out var modeObj) && modeObj != null
                ? modeObj.ToString() : AppSettings.WindowBackgroundMode;
            string imagePath = windowBackground != null && windowBackground.TryGetValue("imagePath", out var imgObj) && imgObj != null
                ? imgObj.ToString() : AppSettings.WindowBackgroundImagePath;

            WindowBackgroundModeBox.ItemsSource = WindowBackgroundBrushes.AvailableModes
                .Select(m => new { m.Mode, m.Label }).ToList();
            WindowBackgroundModeBox.DisplayMemberPath = "Label";
            WindowBackgroundModeBox.SelectedValuePath = "Mode";
            // Modus case-insensitiv (die App wertet ihn ebenfalls so aus), sonst würde z.B.
            // "Dark-Panel" im Dropdown auf "none" fallen und beim Speichern überschrieben.
            WindowBackgroundModeBox.SelectedValue = (mode ?? "none").Trim().ToLowerInvariant();
            if (WindowBackgroundModeBox.SelectedItem == null) WindowBackgroundModeBox.SelectedIndex = 0;
            CustomImagePathBox.Text = imagePath;
            CustomImageRow.Visibility = (WindowBackgroundModeBox.SelectedValue as string) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Liest eine Zahl tolerant aus einem JSON-Objekt (fehlend/ungültig = fallback).</summary>
        private static double ReadNumber(Dictionary<string, object> dict, string key, double fallback)
        {
            try
            {
                if (dict != null && dict.TryGetValue(key, out var raw) && raw != null)
                {
                    double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(value) && !double.IsInfinity(value)) return value;
                }
            }
            catch { /* ungültiger Wert - Fallback */ }
            return fallback;
        }

        private void WindowBackgroundModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CustomImageRow.Visibility = (WindowBackgroundModeBox.SelectedValue as string) == "custom"
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Bilder (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Alle Dateien (*.*)|*.*",
                Title = "Hintergrundbild auswählen"
            };
            if (dialog.ShowDialog() == true)
                CustomImagePathBox.Text = dialog.FileName;
        }

        /// <summary>
        /// Liest eine Dezimalzahl aus einem Textfeld: Komma wird zu Punkt, KEIN
        /// Tausendertrennzeichen ("14,5" ist 14.5 und nicht 145), NaN/Unendlich
        /// werden abgelehnt.
        /// </summary>
        private static bool TryParseDecimal(string text, out double value)
        {
            string normalized = (text ?? "").Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Schreibt die im "Einstellungen"-Reiter eingetragenen Werte nach
        /// settings.json - bestehende, hier nicht bearbeitete Schlüssel (falls
        /// die Datei manuell um weitere ergänzt wurde) bleiben dabei erhalten.
        /// Alle Eingaben werden VOR dem Schreiben geprüft (Zahlenformat und
        /// Wertebereich); bei einem Fehler nennt die Meldung das Feld und es wird
        /// nichts geschrieben.
        /// </summary>
        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // ---- Eingaben lesen und prüfen (nichts schreiben, solange etwas ungültig ist) ----
            string validationError = null;

            double ReadDecimalField(string label, string text)
            {
                if (validationError != null) return 0;
                if (!TryParseDecimal(text, out double parsed))
                {
                    validationError = "Feld '" + label + "': ungültige Zahl";
                    return 0;
                }
                return parsed;
            }

            int refreshIntervalMs = 0;
            if (!int.TryParse((RefreshIntervalBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out refreshIntervalMs))
                validationError = "Feld 'Aktualisierungsrate (ms)': ungültige Zahl (ganze Zahl erwartet)";
            else if (refreshIntervalMs < 10 || refreshIntervalMs > 5000)
                validationError = "Feld 'Aktualisierungsrate (ms)': Wert muss zwischen 10 und 5000 liegen";

            double gaugeCellSize = ReadDecimalField("Anzeigen-Basisgröße (px)", GaugeCellSizeBox.Text);
            if (validationError == null && (gaugeCellSize < 50 || gaugeCellSize > 2000))
                validationError = "Feld 'Anzeigen-Basisgröße (px)': Wert muss zwischen 50 und 2000 liegen";

            double ballDivisor = ReadDecimalField("Ball-Teiler", BallDivisorBox.Text);
            if (validationError == null && ballDivisor == 0)
                validationError = "Feld 'Ball-Teiler': darf nicht 0 sein";

            double vacuumThreshold = ReadDecimalField("Vakuum-Schwellwert (inHg)", VacuumThresholdBox.Text);
            if (validationError == null && vacuumThreshold < 0)
                validationError = "Feld 'Vakuum-Schwellwert (inHg)': darf nicht negativ sein";

            double attitudeVacuumThreshold = ReadDecimalField("Künstlicher Horizont: Vakuum-Schwelle (inHg)", AttitudeVacuumThresholdBox.Text);
            if (validationError == null && attitudeVacuumThreshold < 0)
                validationError = "Feld 'Künstlicher Horizont: Vakuum-Schwelle (inHg)': darf nicht negativ sein (0 = Vakuum ignorieren)";

            double oilPressOffset = ReadDecimalField("Atmosphären-Offset (PSI)", OilPressOffsetBox.Text);
            double egtMinF = ReadDecimalField("Skalen-Minimum (°F)", EgtMinFBox.Text);
            double egtMaxF = ReadDecimalField("Skalen-Maximum (°F)", EgtMaxFBox.Text);
            if (validationError == null && egtMaxF <= egtMinF)
                validationError = "Feld 'Skalen-Maximum (°F)': muss größer als das Skalen-Minimum sein";

            string backgroundMode = (WindowBackgroundModeBox.SelectedValue as string) ?? "none";
            string backgroundImagePath = CustomImagePathBox.Text ?? "";
            // Relative Pfade wie in WindowBackgroundBrushes gegen den Programmordner auflösen
            string resolvedImagePath = string.IsNullOrWhiteSpace(backgroundImagePath) ? ""
                : (Path.IsPathRooted(backgroundImagePath)
                    ? backgroundImagePath
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, backgroundImagePath));
            if (validationError == null && backgroundMode == "custom" &&
                (resolvedImagePath.Length == 0 || !File.Exists(resolvedImagePath)))
                validationError = "Feld 'Eigenes Bild': Modus 'Eigenes Bild' braucht einen vorhandenen Dateipfad";

            if (validationError != null)
            {
                SettingsStatusText.Foreground = Brushes.Salmon;
                SettingsStatusText.Text = "Nicht gespeichert - " + validationError;
                return;
            }

            // ---- Schreiben ----
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                Dictionary<string, object> root;
                if (File.Exists(_settingsPath))
                    root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_settingsPath))
                           ?? new Dictionary<string, object>();
                else
                    root = new Dictionary<string, object>();

                root["refreshIntervalMs"] = refreshIntervalMs;
                root["gaugeCellSize"] = gaugeCellSize;
                root["debugLogging"] = DebugLoggingCheckBox.IsChecked == true;

                var turnCoordinator = root.TryGetValue("turnCoordinator", out var tcObj) && tcObj is Dictionary<string, object> tc
                    ? tc : new Dictionary<string, object>();
                turnCoordinator["ballDivisor"] = ballDivisor;
                turnCoordinator["vacuumThreshold"] = vacuumThreshold;
                root["turnCoordinator"] = turnCoordinator;

                root["attitudeVacuumThreshold"] = attitudeVacuumThreshold;

                root["oilPressAtmosphericOffsetPsi"] = oilPressOffset;
                root["egtMinF"] = egtMinF;
                root["egtMaxF"] = egtMaxF;

                var windowBackground = root.TryGetValue("windowBackground", out var bgObj) && bgObj is Dictionary<string, object> bg
                    ? bg : new Dictionary<string, object>();
                windowBackground["mode"] = backgroundMode;
                windowBackground["imagePath"] = backgroundImagePath;
                root["windowBackground"] = windowBackground;

                WriteAllTextAtomic(_settingsPath, serializer.Serialize(root));
                SettingsStatusText.Foreground = Brushes.LightGreen;
                SettingsStatusText.Text = "Gespeichert. Bitte die App neu starten, damit es übernommen wird.";
            }
            catch (Exception ex)
            {
                SettingsStatusText.Foreground = Brushes.Salmon;
                SettingsStatusText.Text = "Fehler beim Speichern: " + ex.Message;
            }
        }
    }
}
