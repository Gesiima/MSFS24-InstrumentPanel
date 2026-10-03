// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// Ein Knoten im Aufteilungs-Baum: entweder ein Blatt (IsLeaf=true, mit
        /// einer zugewiesenen Anzeige) oder ein Split (in "SplitCount" gleich
        /// große Kind-Flächen, gestapelt gemäß "Direction").
        /// </summary>
        private enum SplitDirection { Rows, Columns }

        private class SplitNode
        {
            public bool IsLeaf = true;
            public string GaugeName = "(leer)";
            public SplitDirection Direction;
            public List<SplitNode> Children = new List<SplitNode>();

            /// <summary>
            /// Relatives Gewicht dieses Knotens INNERHALB der Aufteilung seines
            /// Elternteils (Standard 1 = gleich groß wie seine Geschwister). Ein
            /// Gewicht von z.B. 2 bei Geschwistern mit Gewicht 3 ergibt ein
            /// Größenverhältnis von 2:3 - damit lassen sich ungleich große
            /// Teilflächen abbilden (z.B. eine 2/3-große Anzeige neben normalen).
            /// </summary>
            public int Weight = 12; // 12 = "Normal" (Standard-Maßstab, siehe ApplyAutoWidth/BuildResetToNormalButton)

            /// <summary>
            /// Wenn true, wird Weight bei jeder Anzeige NEU aus der eigenen
            /// Zeilenanzahl berechnet (siehe UpdateAutoWidth) - damit bleibt die
            /// Breite automatisch im Einklang, auch wenn man später noch Zeilen
            /// hinzufügt/entfernt. Nur sinnvoll bei Direction==Rows.
            /// </summary>
            public bool AutoWidth = false;
        }

        /// <summary>Exakter Bruch (keine Fließkomma-Ungenauigkeiten beim Aufteilen).</summary>
        private struct Frac
        {
            public long Num, Den;
            public Frac(long num, long den)
            {
                if (den < 0) { num = -num; den = -den; }
                long g = Gcd(Math.Abs(num), den);
                if (g == 0) g = 1;
                Num = num / g;
                Den = den / g;
            }
            public static long Gcd(long a, long b) => b == 0 ? (a == 0 ? 1 : a) : Gcd(b, a % b);
            public static Frac operator +(Frac a, Frac b) => new Frac(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
            public static Frac operator *(Frac a, long k) => new Frac(a.Num * k, a.Den);
            public static Frac operator /(Frac a, long k) => new Frac(a.Num, a.Den * k);
        }

        private SplitNode _root = new SplitNode();
        private readonly string _layoutPath;
        private readonly string _settingsPath;
        private readonly SimConnectService _service;
        private readonly DispatcherTimer _debugValuesTimer = new DispatcherTimer();
        private int _currentWindowIndex = 0;

        private readonly Action _onSaved;
        private readonly Action<int> _onWindowDeleted;

        public LayoutDesignerWindow(string layoutPath, SimConnectService service, Action onSaved = null, Action<int> onWindowDeleted = null)
        {
            InitializeComponent();
            Title = "Setup - InstrumentPanel v" + AppVersion.Current;
            _layoutPath = layoutPath;
            _settingsPath = Path.Combine(Path.GetDirectoryName(layoutPath) ?? AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            _service = service;
            _onSaved = onSaved;
            _onWindowDeleted = onWindowDeleted;
            RefreshWindowButtons();
            LoadWindow(0); // Fenster 0 (Standard) direkt beim Öffnen einlesen
            LoadSettingsIntoUi();

            _debugValuesTimer.Interval = TimeSpan.FromSeconds(1);
            _debugValuesTimer.Tick += (s, e) => RefreshDebugValues();
            _debugValuesTimer.Start();
            Closed += (s, e) => _debugValuesTimer.Stop();
            RefreshDebugValues();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _root = new SplitNode();
            RedrawTree();
        }

        /// <summary>
        /// Liest das 'windows'-Array roh aus layout.json (leere Liste, wenn Datei
        /// oder Array fehlen) - Grundlage für die Fenster-Buttons-Übersicht.
        /// </summary>
        private List<object> ReadWindowsArray()
        {
            try
            {
                if (!File.Exists(_layoutPath)) return new List<object>();
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_layoutPath));
                if (root != null && root.TryGetValue("windows", out var windowsObj))
                    return ((ArrayList)windowsObj).Cast<object>().ToList();
            }
            catch { /* Übersicht ist rein informativ - bei Fehlern einfach leer anzeigen */ }
            return new List<object>();
        }

        /// <summary>
        /// Baut die Fenster-Übersicht (ein Button je vorhandenem Fenster + "+ Neues
        /// Fenster") neu auf - das gerade ausgewählte Fenster ist hervorgehoben.
        /// Jeder Button zeigt per Tooltip, welche Anzeigen in diesem Fenster stecken.
        /// </summary>
        private void RefreshWindowButtons()
        {
            var windows = ReadWindowsArray();
            WindowButtonsPanel.Children.Clear();

            for (int i = 0; i < windows.Count; i++)
            {
                int index = i;
                bool isSelected = index == _currentWindowIndex;

                string gaugesSummary = "leer";
                if (windows[i] is Dictionary<string, object> windowData && windowData.TryGetValue("cells", out var cellsObj))
                {
                    var names = ((ArrayList)cellsObj)
                        .Cast<Dictionary<string, object>>()
                        .Select(c => Convert.ToString(c["name"]))
                        .Distinct()
                        .ToList();
                    if (names.Count > 0) gaugesSummary = string.Join(", ", names);
                }

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
                _currentWindowIndex = windows.Count;
                _root = new SplitNode();
                RedrawTree();
                RefreshWindowButtons();
            };
            WindowButtonsPanel.Children.Add(addButton);
        }

        /// <summary>
        /// Entfernt das aktuell ausgewählte Fenster KOMPLETT aus dem
        /// 'windows'-Array in layout.json (nicht nur leeren) und schließt es
        /// sofort, falls es gerade offen ist.
        /// </summary>
        private void DeleteWindowButton_Click(object sender, RoutedEventArgs e)
        {
            int windowIndex = _currentWindowIndex;

            try
            {
                if (!File.Exists(_layoutPath))
                {
                    StatusText.Foreground = Brushes.Salmon;
                    StatusText.Text = "layout.json existiert nicht - nichts zu löschen.";
                    return;
                }

                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_layoutPath))
                           ?? new Dictionary<string, object>();

                if (!root.TryGetValue("windows", out var windowsObj))
                {
                    StatusText.Foreground = Brushes.Salmon;
                    StatusText.Text = "Kein 'windows'-Array vorhanden - nichts zu löschen.";
                    return;
                }

                var windows = ((ArrayList)windowsObj).Cast<object>().ToList();
                if (windowIndex >= windows.Count)
                {
                    StatusText.Foreground = Brushes.Salmon;
                    StatusText.Text = "Fenster " + windowIndex + " existiert nicht.";
                    return;
                }

                windows.RemoveAt(windowIndex);
                root["windows"] = windows;
                File.WriteAllText(_layoutPath, serializer.Serialize(root));

                _onWindowDeleted?.Invoke(windowIndex); // schließt gezielt genau dieses Fenster, falls offen

                _currentWindowIndex = Math.Max(0, Math.Min(_currentWindowIndex, windows.Count - 1));
                RefreshWindowButtons();
                LoadWindow(_currentWindowIndex);

                StatusText.Foreground = Brushes.LightGreen;
                StatusText.Text = "Fenster " + windowIndex + " gelöscht und geschlossen." +
                    (windowIndex < windows.Count ? " Achtung: nachfolgende Fenster-Nummern sind nachgerückt." : "");
            }
            catch (Exception ex)
            {
                StatusText.Foreground = Brushes.Salmon;
                StatusText.Text = "Fehler beim Löschen: " + ex.Message;
            }
        }

        // -----------------------------------------------------------------
        // Einlesen: layout.json -> Baum
        // -----------------------------------------------------------------

        /// <summary>
        /// Liest das Fenster an "index" aus layout.json ein und baut den
        /// Aufteilungs-Baum daraus wieder auf (best-effort - siehe
        /// BuildTreeFromCells). Existiert die Datei/das Fenster (noch) nicht,
        /// wird eine leere Fläche angezeigt.
        /// </summary>
        private void LoadWindow(int windowIndex)
        {

            Dictionary<string, object> windowData = null;
            try
            {
                if (File.Exists(_layoutPath))
                {
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_layoutPath));
                    if (root != null && root.TryGetValue("windows", out var windowsObj))
                    {
                        var windowsList = (ArrayList)windowsObj;
                        if (windowIndex < windowsList.Count)
                            windowData = windowsList[windowIndex] as Dictionary<string, object>;
                    }
                }
            }
            catch (Exception ex)
            {
                if (StatusText != null)
                {
                    StatusText.Foreground = Brushes.Salmon;
                    StatusText.Text = "Fehler beim Einlesen: " + ex.Message;
                }
            }

            if (windowData == null || !windowData.TryGetValue("cells", out var cellsObj))
            {
                _root = new SplitNode();
                RedrawTree();
                return;
            }

            var cells = new List<(int Row, int Col, int RowSpan, int ColSpan, string Name)>();
            foreach (Dictionary<string, object> cellObj in (ArrayList)cellsObj)
            {
                cells.Add((
                    Convert.ToInt32(cellObj["row"]),
                    Convert.ToInt32(cellObj["col"]),
                    cellObj.TryGetValue("rowSpan", out var rs) ? Convert.ToInt32(rs) : 1,
                    cellObj.TryGetValue("colSpan", out var cs) ? Convert.ToInt32(cs) : 1,
                    Convert.ToString(cellObj["name"])));
            }

            int rowUnits = windowData.TryGetValue("rowUnits", out var ru) ? Convert.ToInt32(ru) : cells.Max(c => c.Row + c.RowSpan);
            int colUnits = windowData.TryGetValue("colUnits", out var cu) ? Convert.ToInt32(cu) : cells.Max(c => c.Col + c.ColSpan);

            _root = BuildTreeFromCells(cells, 0, 0, rowUnits, colUnits) ?? new SplitNode();
            DetectAutoWidthFlags(_root);
            RedrawTree();
        }

        /// <summary>
        /// Läuft nach dem Einlesen einmal durch den ganzen Baum und markiert
        /// jede Zeilen-Aufteilung als "an Zeilen gekoppelt" (AutoWidth), deren
        /// aktuelle Breite GENAU dem entspricht, was die Kopplung berechnen
        /// würde - layout.json speichert nur das fertige Ergebnis, nicht ob es
        /// über die Kopplung entstanden ist, daher diese Erkennung anhand des
        /// Zahlenwerts. Ohne das würde der Umschalter nach jedem Neuladen immer
        /// als "aus" erscheinen, obwohl die Breite vorher gekoppelt war.
        /// </summary>
        private void DetectAutoWidthFlags(SplitNode node)
        {
            if (node.IsLeaf) return;
            foreach (var child in node.Children)
                DetectAutoWidthFlags(child);

            if (node.Direction != SplitDirection.Columns) return;
            foreach (var child in node.Children)
            {
                if (child.IsLeaf || child.Direction != SplitDirection.Rows) continue;
                if (MatchesAutoWidth(child, node.Children))
                    child.AutoWidth = true;
            }
        }

        /// <summary>
        /// Prüft (ohne etwas zu verändern), ob node's aktuelles Gewicht dem
        /// entspricht, was BuildAutoWidthToggle/ApplyAutoWidth berechnen würde -
        /// Grundlage für DetectAutoWidthFlags.
        /// </summary>
        private bool MatchesAutoWidth(SplitNode node, List<SplitNode> siblings)
        {
            long ownRowUnits = node.Children.Sum(c => c.Weight);
            long normalRowWeight = node.Children.Min(c => c.Weight);
            double ownRowHeight = (double)normalRowWeight / ownRowUnits;

            var otherSiblings = siblings.Where(sib => sib != node).ToList();
            int referenceRowCount = otherSiblings.Count == 0 ? 1 : otherSiblings.Select(GetRowCount).Min();
            double normalRowHeight = 1.0 / referenceRowCount;
            double expectedRatio = ownRowHeight / normalRowHeight;

            int baselineWeight = ComputeBaselineWeight(siblings);
            if (baselineWeight <= 0) return false;
            double actualRatio = (double)node.Weight / baselineWeight;

            return Math.Abs(expectedRatio - actualRatio) < 0.05;
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
        /// Layouts, nie etwas, das dieser Designer selbst erzeugt hat).
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
        /// per größtem gemeinsamen Teiler.
        /// </summary>
        private static List<int> SimplifyToWeights(List<int> sizes)
        {
            long g = sizes[0];
            foreach (var s in sizes.Skip(1)) g = Frac.Gcd(g, s);
            if (g == 0) g = 1;
            return sizes.Select(s => (int)(s / g)).ToList();
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

            // Größte Anzeige im ganzen Baum ermitteln (per Fläche) - Bezugsgröße
            // für den Prozent-Vergleich "X% der größten Anzeige" an jeder Fläche,
            // damit z.B. 4 gleich große Anzeigen in einem 2x2-Raster nicht nur
            // "Normal" (zueinander) zeigen, sondern auch "25% der größten
            // Anzeige", falls es im selben Fenster größere Anzeigen gibt.
            var leaves = new List<(SplitNode Leaf, Frac X, Frac Y, Frac W, Frac H)>();
            ComputeLeafRects(_root, new Frac(0, 1), new Frac(0, 1), new Frac(1, 1), new Frac(1, 1), leaves);
            _maxLeafArea = leaves.Count == 0 ? 1.0 : leaves.Max(l => ((double)l.W.Num / l.W.Den) * ((double)l.H.Num / l.H.Den));
            if (_maxLeafArea <= 0) _maxLeafArea = 1.0;

            // Für den Wurzelknoten gibt es keine Geschwister - "eigene Größe" ist
            // dort bedeutungslos (füllt immer 100%). -1 signalisiert das, damit
            // BuildNodeVisual dort gar keine Größen-Auswahl anzeigt. Breite/Höhe
            // starten bei 1/1 (volles Fenster) und werden beim Runterreichen durch
            // den Baum entsprechend verkleinert - daraus berechnet sich die an
            // jeder Anzeige gezeigte tatsächliche Endgröße.
            var visual = BuildNodeVisual(_root, null, new Frac(1, 1), new Frac(1, 1));
            Grid.SetRow(visual, 0);
            Grid.SetColumn(visual, 0);
            SlotsGrid.Children.Add(visual);
        }

        /// <summary>
        /// Ermittelt den "Normal"-Bezugswert unter Geschwister-Gewichten: den
        /// häufigsten Wert (bei Gleichstand den größten) - relativ dazu wird die
        /// Größen-Auswahl (Normal/2/3/...) jeder Fläche angezeigt. Wichtig, weil
        /// gespeicherte Gewichte beim Schreiben auf den kleinstmöglichen Bruch
        /// gekürzt werden (z.B. 8:12 -> 2:3) - der Bezug zu "was ist normal" muss
        /// also aus den GESCHWISTERN abgeleitet werden, nicht aus einer festen Zahl.
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
        /// baselineWeight ist der "Normal"-Bezugswert unter den GESCHWISTERN
        /// dieses Knotens (siehe ComputeBaselineWeight) - für die Größen-Auswahl
        /// dieses Knotens selbst. widthFrac/heightFrac sind die vom Wurzelknoten
        /// bis hierhin akkumulierten Anteile der Gesamtbreite/-höhe.
        /// </summary>
        private UIElement BuildNodeVisual(SplitNode node, List<SplitNode> siblings, Frac widthFrac, Frac heightFrac)
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
                if (siblings != null && node.Direction == SplitDirection.Rows)
                    headerButtonsStack.Children.Add(BuildAutoWidthToggle(node, siblings)); // Hauptweg für unterschiedliche Größen: koppelt die Breite an die eigene Zeilenanzahl
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
                    // Geschwister (nicht den Standardwert 12) - sonst würde es bei
                    // z.B. gleich großen 1er-Gewichten (nach dem Kürzen beim
                    // Speichern) alle anderen komplett dominieren.
                    int matchingWeight = node.Children.Count > 0 ? ComputeBaselineWeight(node.Children) : 12;
                    node.Children.Add(new SplitNode { Weight = matchingWeight });
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

                // Automatisch gekoppelte Breiten (siehe BuildAutoWidthToggle) vor
                // dem Zeichnen neu berechnen - so bleibt die Breite korrekt, auch
                // wenn sich die eigene Zeilenanzahl seit dem Einschalten geändert
                // hat, ohne dass der Umschalter erneut betätigt werden muss.
                if (node.Direction == SplitDirection.Columns)
                {
                    foreach (var child in node.Children)
                        if (child.AutoWidth && !child.IsLeaf && child.Direction == SplitDirection.Rows)
                            RefreshAutoWidth(child, node.Children);
                }

                for (int i = 0; i < n; i++)
                {
                    var star = new GridLength(node.Children[i].Weight, GridUnitType.Star);
                    if (node.Direction == SplitDirection.Rows)
                        childrenGrid.RowDefinitions.Add(new RowDefinition { Height = star });
                    else
                        childrenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
                }
                long totalWeight = node.Children.Sum(c => c.Weight);
                for (int i = 0; i < n; i++)
                {
                    Frac childWidth = widthFrac;
                    Frac childHeight = heightFrac;
                    var childShare = new Frac(node.Children[i].Weight, totalWeight);
                    if (node.Direction == SplitDirection.Rows)
                        childHeight = heightFrac * childShare.Num / childShare.Den;
                    else
                        childWidth = widthFrac * childShare.Num / childShare.Den;

                    var childVisual = BuildNodeVisual(node.Children[i], node.Children, childWidth, childHeight);
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
        /// Berechnet die passende Breite (Weight relativ zu siblings), damit die
        /// Zellen dieser Fläche quadratisch bleiben - basierend auf der eigenen
        /// Zeilenanzahl im Vergleich zu einer "normalen" Geschwister-Spalte.
        /// Setzt sie DIREKT auf node.Weight, rechnet die übrigen siblings zuvor
        /// auf unseren Standard-Maßstab (Normal=12) um.
        /// </summary>
        /// <summary>
        /// Eigene "Zeilenanzahl" eines Knotens für den Breite-koppeln-Vergleich:
        /// bei einer Zeilen-Aufteilung die Anzahl ihrer Kinder, sonst (Blatt oder
        /// Spalten-Aufteilung) 1 - eine unaufgeteilte Fläche gilt also als "1 Zeile".
        /// </summary>
        private static int GetRowCount(SplitNode s) =>
            (!s.IsLeaf && s.Direction == SplitDirection.Rows) ? s.Children.Count : 1;

        /// <summary>
        /// Berechnet NUR node's eigenes Gewicht neu (relativ zur festen
        /// "Normal=12"-Referenz), OHNE die Geschwister anzufassen. Wird bei
        /// JEDEM Neuzeichnen für automatisch gekoppelte Flächen aufgerufen
        /// (siehe BuildNodeVisual) - deshalb DARF das die Geschwister nicht
        /// jedes Mal neu skalieren, sonst schaukelt sich das mit jeder weiteren
        /// Interaktion immer weiter hoch (siehe ApplyAutoWidth für den
        /// einmaligen Normalisierungs-Schritt beim Einschalten).
        /// </summary>
        private void RefreshAutoWidth(SplitNode node, List<SplitNode> siblings)
        {
            long ownRowUnits = node.Children.Sum(c => c.Weight);
            long normalRowWeight = node.Children.Min(c => c.Weight); // eine "normale" eigene Zeile
            double ownRowHeight = (double)normalRowWeight / ownRowUnits;

            // Referenz = KLEINSTE Zeilenanzahl unter den ÜBRIGEN Geschwistern -
            // also die am WENIGSTEN unterteilte Fläche (im Extremfall unaufgeteilt
            // = 1 Zeile), NICHT die häufigste. Eine unaufgeteilte volle Anzeige
            // ist die eigentliche "voll"-Referenz, an der sich MainWindow beim
            // Berechnen der Fenstergröße orientiert (größter rowSpan) - auch wenn
            // ZUFÄLLIG mehrere andere Geschwister ebenfalls z.B. 2-zeilig sind,
            // bleibt die unaufgeteilte Fläche die richtige Referenz für "Normal".
            var otherSiblings = siblings.Where(sib => sib != node).ToList();
            int referenceRowCount = otherSiblings.Count == 0 ? 1 : otherSiblings.Select(GetRowCount).Min();
            double normalRowHeight = 1.0 / referenceRowCount;

            double ratio = ownRowHeight / normalRowHeight;
            node.Weight = Math.Max(1, (int)Math.Round(12.0 * ratio));
        }

        /// <summary>
        /// EINMALIGER Schritt beim Einschalten von "Breite koppeln": normalisiert
        /// zuerst die Geschwister-Gruppe auf unseren Standard-Maßstab (Normal=12)
        /// - das ist bewusst NUR hier (beim expliziten Einschalten durch den
        /// Nutzer), NICHT bei jedem automatischen Neuberechnen (RefreshAutoWidth),
        /// da sonst jede weitere Interaktion die Geschwister immer weiter
        /// hochskalieren würde (Aufschaukel-Effekt).
        /// </summary>
        private void ApplyAutoWidth(SplitNode node, List<SplitNode> siblings)
        {
            int baselineWeight = ComputeBaselineWeight(siblings);
            double scale = 12.0 / baselineWeight;
            foreach (var sibling in siblings)
            {
                if (sibling == node) continue;
                sibling.Weight = Math.Max(1, (int)Math.Round(sibling.Weight * scale));
            }
            RefreshAutoWidth(node, siblings);
        }

        /// <summary>
        /// Einzelner Umschalter statt zweier getrennter Knöpfe: koppelt die
        /// Breite dieser Fläche an ihre eigene Zeilenanzahl (AN) oder löst die
        /// Kopplung wieder (AUS, zurück auf "Normal"). Im AN-Zustand wird die
        /// Breite bei jeder Änderung automatisch neu berechnet (siehe
        /// BuildNodeVisual) - ändert man später die Zeilenanzahl, passt sich die
        /// Breite von selbst mit an, ohne den Knopf erneut betätigen zu müssen.
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
                RedrawTree();
            };
            toggle.Unchecked += (s, e) =>
            {
                // Reihenfolge wichtig: Baseline/Rescale BEVOR AutoWidth auf false
                // gesetzt wird - sonst würde der eigene (noch veraltete) Gewichts-
                // wert die Berechnung mit verfälschen (siehe ComputeBaselineWeight).
                int baselineWeight = ComputeBaselineWeight(siblings);
                double scale = 12.0 / baselineWeight;
                foreach (var sibling in siblings)
                {
                    if (sibling == node) continue;
                    sibling.Weight = Math.Max(1, (int)Math.Round(sibling.Weight * scale));
                }
                node.AutoWidth = false;
                node.Weight = 12; // zurück auf "Normal"
                RedrawTree();
            };
            return toggle;
        }

        /// <summary>
        /// Verpackt die manuelle Größen-Auswahl in einen eingeklappten
        /// "Erweitert"-Bereich - das ist der SELTENERE Sonderfall (bewusst NICHT
        /// quadratisch, oder eine Größe, die sich nicht aus einer Zeilenanzahl
        /// ergibt). Für den Regelfall (Seitenverhältnis erhalten) gibt es den
        /// direkt sichtbaren "🔗 Breite an Zeilen koppeln"-Umschalter - das hält
        /// die Oberfläche im Regelfall aufgeräumt.
        /// </summary>
        /// <summary>
        /// Setzt diese Fläche wieder auf "Normal" zurück (Verhältnis 1:1 zu den
        /// Geschwistern) - die naheliegende Art,
        /// eine vorherige Quadratisch- oder manuelle Größen-Änderung rückgängig
        /// zu machen, ohne die "Erweitert"-Auswahl von Hand suchen zu müssen.
        /// </summary>
        private UIElement BuildResetToNormalButton(SplitNode node, List<SplitNode> siblings)
        {
            var button = new Button
            {
                Content = "↩ Normal",
                FontSize = 10,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Setzt diese Fläche wieder auf 'Normal' zurück (gleich groß wie eine normale Geschwister-Fläche) - macht z.B. 'Quadratisch' oder eine manuelle Größe rückgängig."
            };
            button.Click += (s, e) =>
            {
                int baselineWeight = ComputeBaselineWeight(siblings);
                double scale = 12.0 / baselineWeight;
                foreach (var sibling in siblings)
                {
                    if (sibling == node) continue;
                    sibling.Weight = Math.Max(1, (int)Math.Round(sibling.Weight * scale));
                }
                node.Weight = 12; // "Normal"
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
        /// gleich große Teilflächen in der angegebenen Richtung aufzuteilen. Die
        /// erste Teilfläche übernimmt die bisherige Anzeige.
        /// </summary>
        private UIElement BuildSplitControl(SplitNode node, string icon, SplitDirection direction, string tooltip)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            var countBox = new TextBox { Width = 22, Text = "2", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0), ToolTip = "Anzahl Teilflächen" };
            var splitButton = new Button { Content = icon, Padding = new Thickness(5, 1, 5, 1), ToolTip = tooltip };
            splitButton.Click += (s, e) =>
            {
                if (!int.TryParse(countBox.Text, out int n) || n < 2) n = 2;

                string currentGauge = node.GaugeName;
                node.IsLeaf = false;
                node.Direction = direction;
                node.Children = new List<SplitNode>();
                for (int i = 0; i < n; i++)
                    node.Children.Add(new SplitNode { IsLeaf = true, GaugeName = i == 0 ? currentGauge : "(leer)" });
                RedrawTree();
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

            // Gewichtete Aufteilung statt gleicher Teile - Standardgewicht 1 bei
            // allen Kindern ergibt weiterhin gleich große Teile wie bisher.
            int totalWeight = node.Children.Sum(c => c.Weight);
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

        private static long Lcm(long a, long b) => a / Frac.Gcd(a, b) * b;

        /// <summary>
        /// Erzeugt aus dem aktuellen Baum das Layout FÜR DIESES EINE FENSTER
        /// (rowUnits/colUnits/cells) und schreibt es an Position "Fenster-Nr." im
        /// 'windows'-Array von layout.json - andere, bereits vorhandene Fenster in
        /// der Datei bleiben dabei unangetastet erhalten.
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var leaves = new List<(SplitNode Leaf, Frac X, Frac Y, Frac W, Frac H)>();
            ComputeLeafRects(_root, new Frac(0, 1), new Frac(0, 1), new Frac(1, 1), new Frac(1, 1), leaves);

            // Gemeinsamen Nenner (kgV) je Achse finden, damit alle Positionen/
            // Ausdehnungen als ganze Zahlen dargestellt werden können.
            long rowScale = 1, colScale = 1;
            foreach (var l in leaves)
            {
                rowScale = Lcm(rowScale, l.Y.Den);
                rowScale = Lcm(rowScale, l.H.Den);
                colScale = Lcm(colScale, l.X.Den);
                colScale = Lcm(colScale, l.W.Den);
            }

            var cellDicts = new List<Dictionary<string, object>>();
            foreach (var l in leaves)
            {
                // Leere Felder werden JETZT bewusst MIT gespeichert (mit
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

                // Bestehende Fenster-Liste übernehmen (falls vorhanden), sonst neu
                // anlegen - und das aktuell bearbeitete Fenster ersetzen bzw.
                // ergänzen, alle anderen bleiben unverändert.
                var windows = root.TryGetValue("windows", out var windowsObj)
                    ? ((ArrayList)windowsObj).Cast<object>().ToList()
                    : new List<object>();

                int windowIndex = _currentWindowIndex;

                while (windows.Count <= windowIndex)
                    windows.Add(null);
                windows[windowIndex] = thisWindow;

                root["windows"] = windows;

                File.WriteAllText(_layoutPath, serializer.Serialize(root));
                _onSaved?.Invoke();
                RefreshWindowButtons();
                StatusText.Foreground = Brushes.LightGreen;
                StatusText.Text = "Gespeichert und übernommen (Fenster " + windowIndex + ", " + cellDicts.Count + " Anzeige(n)).";
            }
            catch (Exception ex)
            {
                StatusText.Foreground = Brushes.Salmon;
                StatusText.Text = "Fehler beim Speichern: " + ex.Message;
            }
        }

        // -----------------------------------------------------------------
        // Einstellungen-Reiter (settings.json) - unverändert
        // -----------------------------------------------------------------

        /// <summary>
        /// Liest den aktuellen Stand aller angefragten SimConnect-Variablen aus der
        /// geteilten SimConnectService-Instanz und zeigt sie (alphabetisch sortiert)
        /// im "Debug-Werte"-Reiter an.
        /// </summary>
        private void RefreshDebugValues()
        {
            if (_service == null) return;

            var snapshot = _service.GetLatestValuesSnapshot();
            DebugValuesList.ItemsSource = snapshot
                .OrderBy(kv => kv.Key)
                .Select(kv => new { Name = kv.Key, Value = kv.Value.ToString(CultureInfo.InvariantCulture) })
                .ToList();
        }

        /// <summary>
        /// Füllt den "Einstellungen"-Reiter mit den aktuell wirksamen Werten (aus
        /// AppSettings, das settings.json beim Programmstart bereits gelesen hat).
        /// </summary>
        private void LoadSettingsIntoUi()
        {
            RefreshIntervalBox.Text = AppSettings.RefreshIntervalMs.ToString();
            GaugeCellSizeBox.Text = AppSettings.GaugeCellSize.ToString(CultureInfo.InvariantCulture);
            DebugLoggingCheckBox.IsChecked = AppSettings.DebugLoggingEnabled;
            BallDivisorBox.Text = AppSettings.TurnCoordinatorBallDivisor.ToString(CultureInfo.InvariantCulture);
            VacuumThresholdBox.Text = AppSettings.TurnCoordinatorVacuumThreshold.ToString(CultureInfo.InvariantCulture);
            OilPressOffsetBox.Text = AppSettings.OilPressAtmosphericOffsetPsi.ToString(CultureInfo.InvariantCulture);
            EgtMinFBox.Text = AppSettings.EgtMinF.ToString(CultureInfo.InvariantCulture);
            EgtMaxFBox.Text = AppSettings.EgtMaxF.ToString(CultureInfo.InvariantCulture);

            WindowBackgroundModeBox.ItemsSource = WindowBackgroundBrushes.AvailableModes
                .Select(m => new { m.Mode, m.Label }).ToList();
            WindowBackgroundModeBox.DisplayMemberPath = "Label";
            WindowBackgroundModeBox.SelectedValuePath = "Mode";
            WindowBackgroundModeBox.SelectedValue = AppSettings.WindowBackgroundMode;
            if (WindowBackgroundModeBox.SelectedItem == null) WindowBackgroundModeBox.SelectedIndex = 0;
            CustomImagePathBox.Text = AppSettings.WindowBackgroundImagePath;
            CustomImageRow.Visibility = AppSettings.WindowBackgroundMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
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
        /// Schreibt die im "Einstellungen"-Reiter eingetragenen Werte nach
        /// settings.json - bestehende, hier nicht bearbeitete Schlüssel (falls
        /// die Datei manuell um weitere ergänzt wurde) bleiben dabei erhalten.
        /// </summary>
        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                Dictionary<string, object> root;
                if (File.Exists(_settingsPath))
                    root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_settingsPath))
                           ?? new Dictionary<string, object>();
                else
                    root = new Dictionary<string, object>();

                root["refreshIntervalMs"] = int.Parse(RefreshIntervalBox.Text);
                root["gaugeCellSize"] = double.Parse(GaugeCellSizeBox.Text, CultureInfo.InvariantCulture);
                root["debugLogging"] = DebugLoggingCheckBox.IsChecked == true;

                var turnCoordinator = root.TryGetValue("turnCoordinator", out var tcObj) && tcObj is Dictionary<string, object> tc
                    ? tc : new Dictionary<string, object>();
                turnCoordinator["ballDivisor"] = double.Parse(BallDivisorBox.Text, CultureInfo.InvariantCulture);
                turnCoordinator["vacuumThreshold"] = double.Parse(VacuumThresholdBox.Text, CultureInfo.InvariantCulture);
                root["turnCoordinator"] = turnCoordinator;

                root["oilPressAtmosphericOffsetPsi"] = double.Parse(OilPressOffsetBox.Text, CultureInfo.InvariantCulture);
                root["egtMinF"] = double.Parse(EgtMinFBox.Text, CultureInfo.InvariantCulture);
                root["egtMaxF"] = double.Parse(EgtMaxFBox.Text, CultureInfo.InvariantCulture);

                var windowBackground = root.TryGetValue("windowBackground", out var bgObj) && bgObj is Dictionary<string, object> bg
                    ? bg : new Dictionary<string, object>();
                windowBackground["mode"] = (WindowBackgroundModeBox.SelectedValue as string) ?? "none";
                windowBackground["imagePath"] = CustomImagePathBox.Text ?? "";
                root["windowBackground"] = windowBackground;

                File.WriteAllText(_settingsPath, serializer.Serialize(root));
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
