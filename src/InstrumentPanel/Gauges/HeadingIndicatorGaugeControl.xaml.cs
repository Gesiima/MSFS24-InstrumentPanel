// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.FlightSimulator.SimConnect;

namespace InstrumentPanel
{
    public partial class HeadingIndicatorGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // entspricht dem Rand des schwarzen Zifferblatts

        private const double DragPixelsPerStep = 5;
        private const double KnobDegreesPerStep = 1.5; // 1,5:1 Verhältnis für beide Knöpfe

        private static readonly Dictionary<int, string> CardLabels = new Dictionary<int, string>
        {
            { 0, "N" }, { 30, "3" }, { 60, "6" }, { 90, "E" }, { 120, "12" }, { 150, "15" },
            { 180, "S" }, { 210, "21" }, { 240, "24" }, { 270, "W" }, { 300, "30" }, { 330, "33" }
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct HeadingStruct
        {
            public double Heading;    // Grad (0-359), aktuelle Anzeige des Kreisels
            public double HeadingBug; // Grad (0-359), eingestellter Heading-Bug für den Autopilot
        }

        private SimConnectService _service;
        private RotateTransform _cardRotation;
        private RotateTransform _bugMarkerRotation;
        private double _currentHeading;
        private double _currentBug;

        private bool _draggingLeft;
        private bool _draggingRight;
        private double _dragStartXLeft;
        private double _dragStartXRight;
        private double _dragAccumLeft;
        private double _dragAccumRight;
        private int _wheelAccumLeft;  // aufsummiertes Mausrad-Delta (120 = ein Rastschritt)
        private int _wheelAccumRight;

        public HeadingIndicatorGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            _service = service;
            service.Register<HeadingStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("HEADING INDICATOR", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
                    ("AUTOPILOT HEADING LOCK DIR", "degrees", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        private void OnData(HeadingStruct data)
        {
            // NaN/Infinity nie an die RotateTransform weiterreichen - Update überspringen.
            if (double.IsNaN(data.Heading) || double.IsInfinity(data.Heading)
                || double.IsNaN(data.HeadingBug) || double.IsInfinity(data.HeadingBug))
                return;

            _currentHeading = data.Heading;
            _currentBug = data.HeadingBug;

            if (_cardRotation != null)
                _cardRotation.Angle = -_currentHeading;

            if (_bugMarkerRotation != null)
                _bugMarkerRotation.Angle = _currentBug - _currentHeading; // relativ zur drehenden Kompassscheibe, nicht absolut

            DebugLog.Write(() => "[HDG] heading=" + _currentHeading + ", bug=" + _currentBug);
        }

        // ---------------------------------------------------------------
        // Zifferblatt zeichnen (einmalig beim Start)
        // ---------------------------------------------------------------
        private void DrawGaugeFace()
        {
            var outerBezel = new Ellipse
            {
                Width = 300,
                Height = 300,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x6a, 0x6a, 0x6e), 0.85),
                        new GradientStop(Color.FromRgb(0x1a, 0x1a, 0x1c), 1.0)
                    })
            };
            Canvas.SetLeft(outerBezel, 0);
            Canvas.SetTop(outerBezel, 0);
            GaugeCanvas.Children.Add(outerBezel);

            var innerBezel = new Ellipse
            {
                Width = 282,
                Height = 282,
                Fill = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2c))
            };
            Canvas.SetLeft(innerBezel, 9);
            Canvas.SetTop(innerBezel, 9);
            GaugeCanvas.Children.Add(innerBezel);

            var face = new Ellipse { Width = 264, Height = 264, Fill = Brushes.Black };
            Canvas.SetLeft(face, 18);
            Canvas.SetTop(face, 18);
            GaugeCanvas.Children.Add(face);

            // Rotierende Kompassscheibe: ein Sub-Canvas mit ALLEN drehenden Elementen
            // (Striche, Zahlen/Buchstaben, Heading-Bug-Marker), eine einzige
            // RotateTransform für das ganze Paket statt pro Element.
            var card = new Canvas { Width = 300, Height = 300 };
            _cardRotation = new RotateTransform(0, CenterX, CenterY);
            card.RenderTransform = _cardRotation;

            const double majorLen = 20;
            for (int deg = 0; deg < 360; deg += 5)
            {
                bool isMajor = deg % 30 == 0;
                bool isMinor = !isMajor && deg % 10 == 0;
                double outer = OuterRadius - 4;
                double len = isMajor ? majorLen : isMinor ? majorLen * 2.0 / 3.0 : majorLen / 3.0;
                double inner = outer - len;
                var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, inner);
                var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, outer);
                card.Children.Add(new Line
                {
                    X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = isMajor ? 3 : isMinor ? 1.5 : 1
                });

                if (isMajor)
                {
                    var labelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, OuterRadius - 38);
                    // Rotation = deg, damit die Zahl radial ausgerichtet ist (Unterkante
                    // zeigt zur Mitte, Oberkante nach außen) statt immer aufrecht zu stehen.
                    GaugeDrawing.AddCenteredText(card, CardLabels[deg], labelPoint.X, labelPoint.Y, 22, Brushes.White, true, deg);
                }
            }

            // Heading-Bug-Marker: zwei schmalere (30%), um 90° gedrehte Fünfecke
            // (schmale Seite zeigt zur Mitte) mit dünner Lücke dazwischen, in Rot.
            // Eigene Gruppe bei (0,0) ohne Canvas.Left/Top-Verschiebung, damit die
            // RotateTransform mit absoluten Koordinaten korrekt funktioniert (sonst
            // derselbe "wandert irgendwohin"-Fehler wie beim Turn-Coordinator-Leitwerk).
            // ERST ganz am Ende von DrawGaugeFace zum Canvas hinzugefügt, damit er im
            // Vordergrund liegt (nicht von den weißen Strichen überdeckt wird).
            var bugGroup = new Canvas();
            var bugMarkerCenter = new Point(CenterX, CenterY - (OuterRadius - 14));
            DrawCourseSelectorPair(bugGroup, bugMarkerCenter, 10, Brushes.Red, 180);
            _bugMarkerRotation = new RotateTransform(0, CenterX, CenterY);
            bugGroup.RenderTransform = _bugMarkerRotation;

            GaugeCanvas.Children.Add(card);

            // Festes Flugzeug-Symbol (Aufsicht von oben) in der Mitte, dreht NICHT mit -
            // als EIN zusammenhängendes Polygon (Nase, Tragflächen, Rumpf, Leitwerk,
            // Heck), damit es klar als Flugzeug-Silhouette erkennbar ist.
            var plane = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX - 4, CenterY - 22),      // Rumpf-Vorderkante links: Motor nochmal 15% kürzer
                    new Point(CenterX + 4, CenterY - 22),      // Rumpf-Vorderkante rechts
                    new Point(CenterX + 4, CenterY - 8),       // Rumpf doppelt so breit vorn
                    new Point(CenterX + 32, CenterY - 8),      // rechte Spitze, Vorderkante gerade durchgehend (Spannweite -20%)
                    new Point(CenterX + 32, CenterY - 1.3),    // Flügelspitze 2/3 der Tragflächen-Dicke (Wurzel=10 -> Spitze=6,7)
                    new Point(CenterX + 21, CenterY + 2),      // Ende des parallelen Mittelteils - Querruder = äußeres Drittel
                    new Point(CenterX + 4, CenterY + 2),       // Hinterkante parallel zur Vorderkante bis hierhin
                    new Point(CenterX + 2, CenterY + 20),      // Rumpf verjüngt sich hinter der Tragfläche auf die alte Breite
                    new Point(CenterX + 15, CenterY + 25),     // rechte Leitwerksspitze
                    new Point(CenterX + 15, CenterY + 29),
                    new Point(CenterX + 2, CenterY + 29),
                    new Point(CenterX, CenterY + 33),          // Heckspitze
                    new Point(CenterX - 2, CenterY + 29),
                    new Point(CenterX - 15, CenterY + 29),
                    new Point(CenterX - 15, CenterY + 25),     // linke Leitwerksspitze
                    new Point(CenterX - 2, CenterY + 20),      // Rumpf verjüngt sich hinter der Tragfläche auf die alte Breite
                    new Point(CenterX - 4, CenterY + 2),       // Hinterkante parallel zur Vorderkante bis hierhin
                    new Point(CenterX - 21, CenterY + 2),      // Ende des parallelen Mittelteils - Querruder = äußeres Drittel
                    new Point(CenterX - 32, CenterY - 1.3),    // Flügelspitze 2/3 der Tragflächen-Dicke
                    new Point(CenterX - 32, CenterY - 8),      // linke Spitze
                    new Point(CenterX - 4, CenterY - 8)        // Rumpf doppelt so breit vorn
                }),
                Fill = Brushes.White
            };
            GaugeCanvas.Children.Add(plane);

            // Kleiner Strich für den Propeller-Spinner, direkt an der stumpfen Nase
            GaugeCanvas.Children.Add(new Line
            {
                X1 = CenterX - 3, Y1 = CenterY - 23,
                X2 = CenterX + 3, Y2 = CenterY - 23,
                Stroke = Brushes.White,
                StrokeThickness = 1
            });

            // Separater, fester langer Pfeil (Form wie beim VSI-Zeiger) - beginnt ein
            // kleines Stück oberhalb der Flugzeugspitze und reicht bis zu den
            // Skalenstrichen. Dient als präzise Kurs-Ablesehilfe (Lubber-Line).
            const double indexHubHalfWidth = 10;
            double indexTipY = CenterY - (OuterRadius - 8);   // Spitze zeigt auf die Skala, reicht ins obere rote Dreieck hinein
            double indexTailTipY = CenterY - 28;               // hintere 45°-Spitze, endet knapp vor dem Flugzeug (Nase bei -24)
            double indexHubY = indexTailTipY - indexHubHalfWidth; // 45°: Länge der Heck-Spitze = Halbbreite (tan45°=1)
            var indexArrow = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, indexTipY),                 // Spitze - zeigt den Wert auf der Skala, läuft durchgehend spitz zu
                    new Point(CenterX - indexHubHalfWidth, indexHubY),
                    new Point(CenterX, indexTailTipY),             // Heck (zum Mittelpunkt): kleine 45°-Spitze
                    new Point(CenterX + indexHubHalfWidth, indexHubY)
                }),
                Fill = Brushes.White
            };
            GaugeCanvas.Children.Add(indexArrow);

            // Feste rote Referenzmarken (drehen NICHT mit): groß bei 12/3/6/9-Uhr,
            // klein dazwischen bei 45° - als Dreiecke statt Striche. NACH dem Zeiger
            // gezeichnet, damit sie im Vordergrund liegen (nicht vom Zeiger überdeckt).
            for (int deg = 0; deg < 360; deg += 45)
            {
                bool isMajorMark = deg % 90 == 0;
                double outer = OuterRadius - 4;
                double inner = outer - (isMajorMark ? majorLen : 10); // große Dreiecke so lang wie die großen weißen Striche
                double halfWidthDeg = (isMajorMark ? 3 : 2) * 2.0 / 3.0; // 1/3 schmaler
                var tip = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, inner);
                var baseLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg - halfWidthDeg, outer);
                var baseRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg + halfWidthDeg, outer);
                GaugeCanvas.Children.Add(new Polygon
                {
                    Points = new PointCollection(new[] { tip, baseLeft, baseRight }),
                    Fill = Brushes.Red
                });
            }

            // Fester senkrechter Strich unter dem Flugzeug bis zum Rand - mit Lücke
            // zum Flugzeug-Heck, "VACUUM" sitzt in dieser Lücke.
            GaugeCanvas.Children.Add(new Line
            {
                X1 = CenterX, Y1 = CenterY + 58,
                X2 = CenterX, Y2 = CenterY + (OuterRadius - 20),
                Stroke = Brushes.White,
                StrokeThickness = 3
            });

            GaugeDrawing.AddCenteredText(GaugeCanvas, "VACUUM", CenterX, CenterY + 45, 11, Brushes.LightGray, false);

            DrawKnob(225, out var hitAreaLeft, isLeftKnob: true);
            DrawKnob(135, out var hitAreaRight, isLeftKnob: false);
            GaugeCanvas.Children.Add(hitAreaLeft);
            GaugeCanvas.Children.Add(hitAreaRight);

            // Heading-Bug ganz zuletzt hinzufügen, damit er über allem anderen liegt.
            GaugeCanvas.Children.Add(bugGroup);
        }

        // ---------------------------------------------------------------
        // Drehknöpfe: links = Kompass-Abgleich (GYRO_DRIFT), rechts = Heading-Bug
        // für den Autopiloten (HEADING_BUG). Beide per Mausrad oder horizontalem
        // Ziehen bedienbar, wie beim Höhenmesser-Drehknopf. Einfache runde Form,
        // mit Symbol/Text passend zum Vorbild-Foto.
        // ---------------------------------------------------------------
        private void DrawKnob(double placementAngle, out Canvas hitArea, bool isLeftKnob)
        {
            const double knobDiameter = 48;
            const double knobRadius = knobDiameter / 2;
            const double bezelMidRadius = 141; // Hälfte der Umrandung (zwischen 132 und 150)
            var knobCenter = GaugeDrawing.PointOnCircle(CenterX, CenterY, placementAngle, bezelMidRadius + knobRadius);

            // Einfache runde Knopf-Form (wie ursprünglich), mit Symbol/Text passend
            // zum Vorbild-Foto: links "PUSH" + gebogener Doppelpfeil, rechts "HDG"
            // + kleines rotes Kurswahlanzeiger-Symbol.
            var knobRotation = new RotateTransform(0, knobCenter.X, knobCenter.Y);
            var knobBody = new Ellipse
            {
                Width = knobDiameter,
                Height = knobDiameter,
                Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), // dunkel, wie die Kugel beim Turn Coordinator
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(knobBody, knobCenter.X - knobRadius);
            Canvas.SetTop(knobBody, knobCenter.Y - knobRadius);
            GaugeCanvas.Children.Add(knobBody);

            if (isLeftKnob)
            {
                // Alles (Bogen, Pfeilspitzen, "PUSH"-Text) in einer eigenen Gruppe bei
                // (0,0) ohne Canvas.Left/Top-Verschiebung, damit die RotateTransform
                // mit absoluten Koordinaten korrekt funktioniert und sich beim Drehen
                // des Knopfs mitdreht.
                var leftContent = new Canvas();
                double arcRadius = knobRadius - 8; // weiter runter/nach innen, mehr Abstand zur Kante
                const double arcHalfSpan = 45;      // Bogen macht insgesamt 90° aus
                const double headAngleSpan = 24;   // Öffnung der Pfeilspitze (Winkel zurück zur Bogenmitte) - deutlich länger gezogen
                const double headRadialSize = 5;   // Breite der Pfeilspitze (senkrecht zum Bogen)

                var arcFigure = new PathFigure { StartPoint = GaugeDrawing.PointOnCircle(knobCenter.X, knobCenter.Y, -arcHalfSpan, arcRadius) };
                arcFigure.Segments.Add(new ArcSegment(
                    GaugeDrawing.PointOnCircle(knobCenter.X, knobCenter.Y, arcHalfSpan, arcRadius),
                    new Size(arcRadius, arcRadius), 0, false, SweepDirection.Clockwise, true));
                var arcPath = new Path
                {
                    Data = new PathGeometry(new[] { arcFigure }),
                    Stroke = Brushes.White,
                    StrokeThickness = 2
                };
                leftContent.Children.Add(arcPath);

                foreach (double a in new[] { -arcHalfSpan, arcHalfSpan })
                {
                    double dir = a < 0 ? -1 : 1;
                    double aBack = a - dir * headAngleSpan; // zurück Richtung Bogenmitte
                    var tip = GaugeDrawing.PointOnCircle(knobCenter.X, knobCenter.Y, a, arcRadius);
                    var corner1 = GaugeDrawing.PointOnCircle(knobCenter.X, knobCenter.Y, aBack, arcRadius - headRadialSize);
                    var corner2 = GaugeDrawing.PointOnCircle(knobCenter.X, knobCenter.Y, aBack, arcRadius + headRadialSize);
                    var arrowHead = new Polygon
                    {
                        Points = new PointCollection(new[] { corner1, corner2, tip }),
                        Fill = Brushes.White
                    };
                    leftContent.Children.Add(arrowHead);
                }

                // "PUSH": schmale (Narrow) Schriftart, damit es nicht über den Knopf
                // hinausragt, dabei Höhe noch etwas größer als zuvor.
                double pushFontSize = knobDiameter / 2.8;
                var pushText = new TextBlock
                {
                    Text = "PUSH",
                    FontSize = pushFontSize,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Arial Narrow"),
                    FontWeight = FontWeights.Bold
                };
                // Echte Vermessung statt geschätzter Breite - zentriert korrekt, egal
                // welche Schrift WPF tatsächlich verwendet (falls "Arial Narrow" auf
                // dem System fehlt und durch eine andere Schrift ersetzt wird).
                pushText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double naturalWidth = pushText.DesiredSize.Width;
                double pushHeight = pushText.DesiredSize.Height;
                Canvas.SetLeft(pushText, knobCenter.X - naturalWidth / 2);
                Canvas.SetTop(pushText, knobCenter.Y - pushHeight / 2);
                leftContent.Children.Add(pushText);

                leftContent.RenderTransform = knobRotation;
                GaugeCanvas.Children.Add(leftContent);
            }
            else
            {
                // Alles (Symbol-Paar, "HDG"-Text) in einer eigenen Gruppe, damit es sich
                // beim Drehen des Knopfs mitdreht (wie schon beim linken Knopf).
                var rightContent = new Canvas();

                // Kleines rotes Kurswahlanzeiger-Symbol (Miniatur der gefasten Form) -
                // nach oben verschoben, damit die Unterkante genau auf der Mittelachse
                // des Knopfs sitzt (statt mittig über den Knopf verteilt).
                const double iconSize = 8;
                double iconHalfExtent = 1.05 * iconSize; // aus der internen Skalierung in DrawCourseSelectorPair/Mark
                var iconCenter = new Point(knobCenter.X, knobCenter.Y - iconHalfExtent);
                DrawCourseSelectorPair(rightContent, iconCenter, iconSize, Brushes.Red, 180);

                // "HDG" in Rot, direkt unter dem Symbol - etwas größer und näher ans
                // Symbol herangerückt als zuvor.
                var hdgText = new TextBlock
                {
                    Text = "HDG",
                    FontSize = knobDiameter / 3.5,
                    Foreground = Brushes.Red,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.Bold
                };
                hdgText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(hdgText, knobCenter.X - hdgText.DesiredSize.Width / 2);
                Canvas.SetTop(hdgText, knobCenter.Y - 4);
                rightContent.Children.Add(hdgText);

                rightContent.RenderTransform = knobRotation;
                GaugeCanvas.Children.Add(rightContent);
            }

            hitArea = new Canvas { Width = knobDiameter * 1.5, Height = knobDiameter * 1.5, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            Canvas.SetLeft(hitArea, knobCenter.X - knobRadius * 1.5);
            Canvas.SetTop(hitArea, knobCenter.Y - knobRadius * 1.5);

            if (isLeftKnob)
            {
                hitArea.MouseWheel += (s, e) => { HandleWheel(true, e.Delta, knobRotation); e.Handled = true; };
                hitArea.MouseLeftButtonDown += (s, e) =>
                {
                    _draggingLeft = true;
                    _dragStartXLeft = e.GetPosition(GaugeCanvas).X;
                    _dragAccumLeft = 0;
                    ((UIElement)s).CaptureMouse();
                    e.Handled = true;
                };
                hitArea.MouseMove += (s, e) => HandleDrag(true, e, knobRotation);
                hitArea.MouseLeftButtonUp += (s, e) => { _draggingLeft = false; ((UIElement)s).ReleaseMouseCapture(); };
                hitArea.LostMouseCapture += (s, e) => { _draggingLeft = false; }; // Mausaufnahme verloren: Drag beenden
            }
            else
            {
                hitArea.MouseWheel += (s, e) => { HandleWheel(false, e.Delta, knobRotation); e.Handled = true; };
                hitArea.MouseLeftButtonDown += (s, e) =>
                {
                    _draggingRight = true;
                    _dragStartXRight = e.GetPosition(GaugeCanvas).X;
                    _dragAccumRight = 0;
                    ((UIElement)s).CaptureMouse();
                    e.Handled = true;
                };
                hitArea.MouseMove += (s, e) => HandleDrag(false, e, knobRotation);
                hitArea.MouseLeftButtonUp += (s, e) => { _draggingRight = false; ((UIElement)s).ReleaseMouseCapture(); };
                hitArea.LostMouseCapture += (s, e) => { _draggingRight = false; }; // Mausaufnahme verloren: Drag beenden
            }
        }

        /// <summary>
        /// Mausrad: Delta pro Knopf akkumulieren, pro 120 (ein Rastschritt) genau ein
        /// Schritt (mit Vorzeichen), damit hochauflösende Mäuse/Touchpads nicht zu
        /// viele Schritte auslösen.
        /// </summary>
        private void HandleWheel(bool isLeftKnob, int delta, RotateTransform knobRotation)
        {
            int accum = isLeftKnob ? _wheelAccumLeft : _wheelAccumRight;
            if (accum != 0 && Math.Sign(accum) != Math.Sign(delta))
                accum = 0; // Richtungswechsel: Rest verwerfen
            accum += delta;

            while (accum >= 120)
            {
                SendKnobStep(isLeftKnob, 1, knobRotation);
                accum -= 120;
            }
            while (accum <= -120)
            {
                SendKnobStep(isLeftKnob, -1, knobRotation);
                accum += 120;
            }

            if (isLeftKnob) _wheelAccumLeft = accum; else _wheelAccumRight = accum;
        }

        private void HandleDrag(bool isLeftKnob, MouseEventArgs e, RotateTransform knobRotation)
        {
            bool dragging = isLeftKnob ? _draggingLeft : _draggingRight;
            if (!dragging) return;

            // Linke Taste nicht mehr gedrückt (Aufnahme verloren/Button außerhalb losgelassen): Drag beenden.
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (isLeftKnob) _draggingLeft = false; else _draggingRight = false;
                return;
            }

            double x = e.GetPosition(GaugeCanvas).X;
            double startX = isLeftKnob ? _dragStartXLeft : _dragStartXRight;
            double delta = x - startX;

            if (isLeftKnob) _dragStartXLeft = x; else _dragStartXRight = x;

            double accum = (isLeftKnob ? _dragAccumLeft : _dragAccumRight) + delta;

            while (accum >= DragPixelsPerStep)
            {
                SendKnobStep(isLeftKnob, 1, knobRotation);
                accum -= DragPixelsPerStep;
            }
            while (accum <= -DragPixelsPerStep)
            {
                SendKnobStep(isLeftKnob, -1, knobRotation);
                accum += DragPixelsPerStep;
            }

            if (isLeftKnob) _dragAccumLeft = accum; else _dragAccumRight = accum;
        }

        /// <summary>
        /// Zeichnet EIN Kurswahlanzeiger-Symbol (gefaste Fünfeck-Form: Rechteck mit
        /// einer Fase an der äußeren Kante) - genutzt (paarweise über
        /// DrawCourseSelectorPair) sowohl als kleines Icon auf dem HDG-Knopf als auch
        /// als echter Heading-Bug-Marker auf dem Zifferblatt.
        /// Exakte Form nach Vorgabe (Außenkanten, beginnend oben links, im
        /// Uhrzeigersinn): waagerecht 3, runter 5, links 1,5, rauf 3, dann
        /// diagonal zurück zum Start - macht ein unregelmäßiges Fünfeck.
        /// mirror=true spiegelt diese Form (für die zweite Hälfte des Paars).
        /// </summary>
        private void DrawCourseSelectorMark(Canvas canvas, Point center, double scale, Brush fill, double orientationAngle, bool mirror)
        {
            // Rohpunkte nach Vorgabe, um den Formmittelpunkt zentriert (-1.5,-2.5 verschoben).
            var raw = new[]
            {
                new Point(0 - 1.5, 0 - 2.5),
                new Point(3 - 1.5, 0 - 2.5),
                new Point(3 - 1.5, 5 - 2.5),
                new Point(1.5 - 1.5, 5 - 2.5),
                new Point(0 - 1.5, 3 - 2.5)
            };

            double rad = orientationAngle * Math.PI / 180.0;
            double outX = Math.Sin(rad), outY = -Math.Cos(rad);
            double tanX = Math.Sin(rad + Math.PI / 2), tanY = -Math.Cos(rad + Math.PI / 2);
            Point Pt(double radial, double tangential) => new Point(
                center.X + outX * radial * scale + tanX * tangential * scale,
                center.Y + outY * radial * scale + tanY * tangential * scale);

            var mark = new Polygon
            {
                Points = new PointCollection(raw.Select(p => Pt(p.Y, mirror ? -p.X : p.X))),
                Fill = fill
            };
            canvas.Children.Add(mark);
        }

        /// <summary>
        /// Zwei Kurswahlanzeiger-Symbole (je 30% schmaler) mit einer dünnen Lücke
        /// dazwischen (Breite wie ein Skalenstrich) - der eigentliche Heading-Bug.
        /// </summary>
        private void DrawCourseSelectorPair(Canvas canvas, Point center, double size, Brush fill, double orientationAngle)
        {
            double narrowSize = size * 0.7; // 30% schmaler
            const double gap = 3;
            double rad = orientationAngle * Math.PI / 180.0;
            double tanX = Math.Sin(rad + Math.PI / 2), tanY = -Math.Cos(rad + Math.PI / 2);
            // Die tatsächliche halbe Breite des Symbols ist 0.9 × narrowSize (aus der
            // Rohform, deren Tangential-Ausdehnung ±1.5 bei Skalierung narrowSize×0.6
            // ist) - nicht narrowSize selbst. Vorher fälschlich narrowSize verwendet,
            // wodurch die reale Lücke größer als der eingestellte Wert war.
            double shapeHalfWidth = 0.9 * narrowSize;
            double offset = shapeHalfWidth + gap / 2;

            var sideA = new Point(center.X - tanX * offset, center.Y - tanY * offset);
            var sideB = new Point(center.X + tanX * offset, center.Y + tanY * offset);
            DrawCourseSelectorMark(canvas, sideA, narrowSize * 0.6, fill, orientationAngle, mirror: false);
            DrawCourseSelectorMark(canvas, sideB, narrowSize * 0.6, fill, orientationAngle, mirror: true);
        }

        private void SendKnobStep(bool isLeftKnob, int direction, RotateTransform knobRotation)
        {
            _service?.SendEvent(
                isLeftKnob
                    ? (direction > 0 ? "GYRO_DRIFT_INC" : "GYRO_DRIFT_DEC")
                    : (direction > 0 ? "HEADING_BUG_INC" : "HEADING_BUG_DEC"));

            // Drehrichtung der optischen Rückmeldung: beim linken (Gyro-Drift-)Knopf
            // absichtlich umgekehrt - entspricht dem Verhalten im echten Sim.
            knobRotation.Angle += (isLeftKnob ? -1 : 1) * direction * KnobDegreesPerStep;
        }
    }
}
