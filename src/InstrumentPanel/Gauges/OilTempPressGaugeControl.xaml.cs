// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.FlightSimulator.SimConnect;

namespace InstrumentPanel
{
    public partial class OilTempPressGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // jetzt genauso groß wie alle anderen Anzeigen - Skalierung nur noch übers Layout

        [StructLayout(LayoutKind.Sequential)]
        private struct FuelStruct
        {
            public double OilTemp;  // GENERAL ENG OIL TEMPERATURE:1 (°F)
            public double OilPress; // GENERAL ENG OIL PRESSURE:1 (PSI)
        }

        private const double ArcCenterOffset = 28.5;
        private const double ArcRadius = 81.6;

        // Skalen-Mittelpunkte 15 weiter nach außen versetzt (Zeiger-Drehpunkte
        // bleiben unverändert an ihrer bisherigen Position - siehe
        // LeftNeedlePivotPoint/RightNeedlePivotPoint weiter unten).
        private static readonly Point LeftArcCenterPoint = new Point(15, CenterY); // absolut auf X=15 gesetzt
        private static readonly Point RightArcCenterPoint = new Point(292.79 + 5, CenterY); // nochmal 5 weiter rechts

        private const double NeedlePivotOffset = ArcCenterOffset + 21;
        private static readonly Point LeftNeedlePivotPoint = new Point((CenterX - OuterRadius) + NeedlePivotOffset, CenterY);
        private static readonly Point RightNeedlePivotPoint = new Point((CenterX + OuterRadius) - NeedlePivotOffset, CenterY);

        // TEMP-Skala (°F): 5 Messpunkte, Winkel wie vorgegeben (bezogen auf die
        // Horizontale: + = nach oben, - = nach unten). Rotationswinkel =
        // 90 - Nutzerwinkel (90° = Horizontale auf der linken/nicht gespiegelten
        // Seite). NUR diese 5 Werte bekommen einen Tick, keine Zwischenstriche.
        private static readonly double[] TempValues = { 75, 100, 150, 200, 245 };
        private static readonly double[] TempAngles = { 130, 123, 111, 72, 52 };
        private const double TempRedlineValue = 245;

        // PRESS-Skala (PSI): Rotationswinkel = 270 + Nutzerwinkel (270° =
        // Horizontale auf der rechten/gespiegelten Seite). Hauptpunkte (mit
        // Zahl) und zusätzliche unbeschriftete Zwischen-Ticks bei 30/40/50/80
        // (linear innerhalb ihres jeweiligen Hauptabschnitts ermittelt).
        private static readonly double[] PressValues = { 0, 20, 60, 100, 115 };
        private static readonly double[] PressAngles = { 230, 240, 280, 300, 310 };
        private static readonly double[] PressMinorValues = { 30, 40, 50, 80 };
        private static readonly double[] PressMinorAngles = { 250, 260, 270, 290 };
        private const double PressLowRedlineValue = 20;
        private const double PressHighRedlineValue = 115;

        // Vom SimConnect-Rohwert wird einmalig 1 bar Atmosphärendruck (in PSI
        // umgerechnet) abgezogen, da GENERAL ENG OIL PRESSURE ggf. absolut statt
        // als Gauge-Druck (relativ zur Umgebung) geliefert wird.
        // (Standardwert kommt jetzt aus AppSettings.OilPressAtmosphericOffsetPsi,
        // per settings.json überschreibbar - siehe dort für Details.)
        private SimConnectService _service;
        private RotateTransform _leftNeedleRotate;
        private RotateTransform _rightNeedleRotate;
        private double _displayedLeft = 0;
        private double _displayedRight = 0;
        private DateTime? _lastUpdateTime;

        public OilTempPressGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            _service = service;
            service.Register<FuelStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("GENERAL ENG OIL TEMPERATURE:1", "fahrenheit", SIMCONNECT_DATATYPE.FLOAT64),
                    ("GENERAL ENG OIL PRESSURE:1", "psi", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird zentral im Fenster (MainWindow) angezeigt.
        }

        private void OnData(FuelStruct data)
        {
            // Zeiger bei sprunghaften Wertänderungen (z.B. beim Betanken) nur mit
            // maximal 10 Gallonen/Sekunde bewegen, statt sofort zu springen.
            const double maxRatePerSecond = 18.0; // +20%, bleibt für TEMP und PRESS gemeinsam
            var now = DateTime.UtcNow;
            double deltaTimeSec = _lastUpdateTime.HasValue ? (now - _lastUpdateTime.Value).TotalSeconds : 0;
            deltaTimeSec = Math.Max(0, Math.Min(1, deltaTimeSec)); // gegen Ausreißer (z.B. nach Pause) absichern
            _lastUpdateTime = now;

            double oilPressGauge = data.OilPress - AppSettings.OilPressAtmosphericOffsetPsi;

            if (double.IsNaN(_displayedLeft))
                _displayedLeft = data.OilTemp;
            if (double.IsNaN(_displayedRight))
                _displayedRight = oilPressGauge;

            double maxStep = maxRatePerSecond * deltaTimeSec;
            _displayedLeft = MoveTowards(_displayedLeft, data.OilTemp, maxStep);
            _displayedRight = MoveTowards(_displayedRight, oilPressGauge, maxStep);

            if (_leftNeedleRotate != null)
                _leftNeedleRotate.Angle = AngleForTempNeedle(_displayedLeft);
            if (_rightNeedleRotate != null)
                _rightNeedleRotate.Angle = AngleForPressNeedle(_displayedRight);
        }

        private static double MoveTowards(double current, double target, double maxStep)
        {
            double diff = target - current;
            if (Math.Abs(diff) <= maxStep) return target;
            return current + Math.Sign(diff) * maxStep;
        }

        // Verallgemeinerte stückweise ATAN2-Interpolation (Erweiterung von Fuels
        // ursprünglichem 2-Punkt-Verfahren auf N Kalibrierpunkte): für jeden
        // Wert wird das passende Kalibrier-Segment gesucht, dort die
        // tatsächlichen Zielpunkte auf dem Bogen bestimmt, und der Zeiger-
        // Drehwinkel (vom - nicht mit dem Skalen-Mittelpunkt identischen -
        // Zeiger-Drehpunkt aus) per atan2 errechnet und linear interpoliert.
        private static double AngleForNeedlePiecewise(double value, double[] values, double[] rotationAngles, Point needlePivot, Point arcCenter, double targetRadius)
        {
            // Nur die UNTERE Grenze wird hart geklemmt. Nach oben hin darf der
            // Zeiger über den letzten Kalibrierpunkt hinauswandern (Extrapolation
            // mit der Steigung der letzten Teilstrecke) - z.B. bei TEMP über die
            // 245 hinaus, wenn der tatsächliche Wert höher liegt.
            double v = Math.Max(values[0], value);
            for (int i = 0; i < values.Length - 1; i++)
            {
                double v0 = values[i], v1 = values[i + 1];
                if (v <= v1 || i == values.Length - 2)
                {
                    double a0 = rotationAngles[i], a1 = rotationAngles[i + 1];
                    var target0 = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, a0, targetRadius);
                    var target1 = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, a1, targetRadius);
                    double needleAngle0 = Math.Atan2(target0.X - needlePivot.X, needlePivot.Y - target0.Y) * 180.0 / Math.PI;
                    double needleAngle1 = Math.Atan2(target1.X - needlePivot.X, needlePivot.Y - target1.Y) * 180.0 / Math.PI;
                    double t = (v1 > v0) ? (v - v0) / (v1 - v0) : 0; // kann jetzt > 1 werden (Extrapolation)
                    return needleAngle0 + t * (needleAngle1 - needleAngle0);
                }
            }
            return 0;
        }

        // TEMP zielt jetzt auf die Tick-Mitte (125,85, siehe DrawTempScaleArc),
        // nicht mehr auf den alten ArcRadius (81,6) - die Ticks liegen inzwischen
        // deutlich weiter außen. PRESS unverändert (Ticks dort noch nicht verschoben).
        private const double TempNeedleTargetRadius = 113.85; // Tick-Innenseite (statt Tick-Mitte 125,85)
        private static double AngleForTempNeedle(double tempF) => AngleForNeedlePiecewise(tempF, TempValues, TempAngles, LeftNeedlePivotPoint, LeftArcCenterPoint, TempNeedleTargetRadius);
        // PRESS: gleiches Prinzip wie TEMP - ATAN2 zielt auf die Tick-Innenseite
        // (81,6-13,5=68,1), Zeigerlänge = Zielradius minus 40, Breite bleibt
        // unabhängig davon fest (siehe DrawNeedle).
        private const double PressNeedleTargetRadius = 125.34; // Mitte des PRESS-Ticks (Außenkante 134,94, Länge 19,2)
        private static double AngleForPressNeedle(double pressPsi) => AngleForNeedlePiecewise(pressPsi, PressValues, PressAngles, RightNeedlePivotPoint, RightArcCenterPoint, PressNeedleTargetRadius);

        // ---------------------------------------------------------------
        // Zifferblatt zeichnen (einmalig beim Start) - aktuell nur der nackte
        // Rahmen, Rest wird Schritt für Schritt ergänzt.
        // ---------------------------------------------------------------
        private void DrawGaugeFace()
        {
            var outerBezel = new Ellipse
            {
                Width = OuterRadius * 2 + 36,
                Height = OuterRadius * 2 + 36,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x6a, 0x6a, 0x6e), 0.85),
                        new GradientStop(Color.FromRgb(0x1a, 0x1a, 0x1c), 1.0)
                    })
            };
            Canvas.SetLeft(outerBezel, CenterX - OuterRadius - 18);
            Canvas.SetTop(outerBezel, CenterY - OuterRadius - 18);
            GaugeCanvas.Children.Add(outerBezel);

            var innerBezel = new Ellipse
            {
                Width = OuterRadius * 2 + 18,
                Height = OuterRadius * 2 + 18,
                Fill = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2c))
            };
            Canvas.SetLeft(innerBezel, CenterX - OuterRadius - 9);
            Canvas.SetTop(innerBezel, CenterY - OuterRadius - 9);
            GaugeCanvas.Children.Add(innerBezel);

            var face = new Ellipse
            {
                Width = OuterRadius * 2,
                Height = OuterRadius * 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x4c))
            };
            Canvas.SetLeft(face, CenterX - OuterRadius);
            Canvas.SetTop(face, CenterY - OuterRadius);
            GaugeCanvas.Children.Add(face);

            // Skalen-Bögen ZUERST gezeichnet, damit der Zeiger (danach) über ihnen
            // (und den Zahlen) liegt. Mittelpunkt bei 15/110 von links (bzw.
            // gespiegelt von rechts, jetzt 5 weiter außen), Radius 34/110.
            DrawTempScaleArc(LeftArcCenterPoint, ArcRadius);
            DrawPressScaleArc(RightArcCenterPoint, ArcRadius);

            // "°F"/"PSI"/"OIL" ebenfalls VOR den Zeigern gezeichnet, damit sie
            // (wie die Zahlen) unter den Zeigern liegen - nur "TEMP"/"PRESS"
            // sollen ganz oben liegen (siehe weiter unten).
            GaugeDrawing.AddCenteredText(GaugeCanvas, "°F", LeftNeedlePivotPoint.X + 20, LeftNeedlePivotPoint.Y, 20.25 * 0.8, Brushes.White, true);
            DrawStackedLabel("PSI", RightNeedlePivotPoint.X - 20 + 5, RightNeedlePivotPoint.Y, 20.25 * 0.8, 16);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "OIL", CenterX + 10, CenterY + 117 - 4 - 3, 25.3125, Brushes.White, true);

            // Zeiger im VSI-Stil (Basis mit kleiner 45°-Spitze hinten, Hauptspitze
            // läuft spitz zu), Drehpunkt = Skalen-Mittelpunkt. Liegt über den Bögen/
            // Zahlen, aber hinter den schwarzen Kreissegmenten (weiter unten).
            // Beide Zeiger EXAKT gleich lang (fester Wert, unabhängig vom
            // jeweiligen ATAN2-Zielradius) - nur die Zielrichtung unterscheidet sich.
            const double sharedNeedleLength = TempNeedleTargetRadius - 40; // 73,85
            _leftNeedleRotate = DrawNeedle(LeftNeedlePivotPoint, sharedNeedleLength);
            _rightNeedleRotate = DrawNeedle(RightNeedlePivotPoint, sharedNeedleLength);
            // Startwinkel direkt auf "0" setzen, damit der Zeiger vor der ersten
            // MSFS-Verbindung nicht senkrecht (WPF-Standardwinkel) steht.
            _leftNeedleRotate.Angle = AngleForTempNeedle(TempValues[0]);
            _rightNeedleRotate.Angle = AngleForPressNeedle(PressValues[0]);

            // Senkrechte Kreissegmente an beiden Seiten (23% der Kreisfläche je
            // Segment) - Sehne bei ±38,6 vom Mittelpunkt (rechnerisch ermittelt).
            // NACH dem Zeiger gezeichnet, damit sie ihn an den Seiten verdecken.
            const double chordOffset = 76.8; // 23/110 lineares Verhältnis (Segment-Breite : Durchmesser)
            DrawSideSegment(-1, chordOffset);
            DrawSideSegment(1, chordOffset);

            // "LEFT"/"RIGHT"-Schriftzüge: Buchstaben gestapelt, senkrecht mittig zur
            // Horizontlinie, linke/rechte Kante bei 14/110 vom jeweiligen Rand.
            // GANZ ZULETZT gezeichnet, damit TEMP/PRESS über allem anderen liegen.
            const double textEdgeOffset = (14.0 / 110.0) * (OuterRadius * 2);
            const double estimatedHalfLetterWidth = 6.75;
            double leftTextCenterX = (CenterX - OuterRadius) + textEdgeOffset + estimatedHalfLetterWidth;
            double rightTextCenterX = (CenterX + OuterRadius) - textEdgeOffset - estimatedHalfLetterWidth;
            DrawStackedLabel("TEMP", leftTextCenterX, CenterY);
            DrawStackedLabel("PRESS", rightTextCenterX, CenterY);

            // DEBUG: rote Punkte auf den Skalen-Mittelpunkten und den
            // Zeiger-Drehpunkten (zur Kontrolle/Justierung, nicht Teil des
            // eigentlichen Anzeigen-Designs).
        }

        private RotateTransform DrawNeedle(Point pivot, double needleLength = 81)
        {
            const double widthReferenceLength = 81; // Breite bleibt IMMER hierauf bezogen, unabhängig von der tatsächlichen Länge
            const double tanTailAngle = 0.26794919; // tan(15°), wie beim VSI
            double oldHubHalfWidth = (widthReferenceLength * 0.25) * tanTailAngle;
            double hubHalfWidth = oldHubHalfWidth * 1.5 * 0.8; // 20% schmaler an der Basis
            double wideY = pivot.Y;
            double tipY = pivot.Y - needleLength;

            // Statt spitzem 45°-Heck: Halbkreis am Drehpunkt-Ende.
            var figure = new PathFigure { StartPoint = new Point(pivot.X - hubHalfWidth, wideY) };
            figure.Segments.Add(new LineSegment(new Point(pivot.X, tipY), true));
            figure.Segments.Add(new LineSegment(new Point(pivot.X + hubHalfWidth, wideY), true));
            figure.Segments.Add(new ArcSegment(
                new Point(pivot.X - hubHalfWidth, wideY),
                new Size(hubHalfWidth, hubHalfWidth), 0, false, SweepDirection.Clockwise, true));

            var needle = new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { figure }),
                Fill = Brushes.White
            };
            var rotate = new RotateTransform(0, pivot.X, pivot.Y);
            needle.RenderTransform = rotate;
            GaugeCanvas.Children.Add(needle);
            return rotate;
        }

        private void DrawStackedLabel(string text, double centerX, double centerY, double fontSize = 20.25, double letterSpacing = 24)
        {
            double startY = centerY - (text.Length - 1) * letterSpacing / 2.0;
            for (int i = 0; i < text.Length; i++)
            {
                // Exakt vermessen statt Schätzformel - sonst steht z.B. das schmale
                // "I" nicht wirklich zentriert.
                var letter = new TextBlock
                {
                    Text = text[i].ToString(),
                    FontSize = fontSize,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.Bold
                };
                letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double y = startY + i * letterSpacing;
                Canvas.SetLeft(letter, centerX - letter.DesiredSize.Width / 2.0);
                Canvas.SetTop(letter, y - letter.DesiredSize.Height / 2.0);
                GaugeCanvas.Children.Add(letter);
            }
        }
        /// <summary>
        /// TEMP-Skala links: 5 Messpunkte (75/100/150/200/245°F) mit den
        /// vorgegebenen Winkeln, KEINE Zwischen-Ticks. 245 als roter Redline-Tick.
        /// </summary>
        private void DrawTempScaleArc(Point arcCenter, double radius)
        {
            // Bogen: jetzt grün, halb so breit wie die Ticks lang sind (27/2=13.5),
            // äußerer Rand liegt auf der äußeren Tick-Kante (die ihrerseits die
            // senkrechte Mittelachse trifft) - eigener Radius entsprechend kleiner
            // als der Tick-Radius (um die halbe Bogen-Dicke).
            const double tickOuterRadius = 137.85; // unverändert (äußere Bogen-Kante)
            const double tickInnerRadius = 137.85 - 27 + 3; // Innenseite um 3 gekürzt (war ±13.5 = 27 Länge)
            const double greenThickness = 13.5; // unverändert (war ursprünglich "halb der Tick-Länge", jetzt fix, da Tick-Länge sich geändert hat)
            // Bogen-Radius jetzt direkt auf 5/8 der Gehäusehöhe festgelegt (siehe
            // Herleitung: vertikale Spannweite pro Radius-Einheit bei den festen
            // Winkeln 52°/130° ist 1,2584 - für 5/8 von 264 (=165) ergibt das 131,1).
            double greenRadius = 131.1;

            // Unten gekürzt: beginnt erst beim "100"-Tick (Index 1), nicht bei "75".
            var arcStart = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, TempAngles[TempAngles.Length - 1], greenRadius);
            var arcEnd = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, TempAngles[1], greenRadius);
            var arcFigure = new PathFigure { StartPoint = arcStart };
            arcFigure.Segments.Add(new ArcSegment(
                arcEnd, new Size(greenRadius, greenRadius), 0, false, SweepDirection.Clockwise, true));
            GaugeCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { arcFigure }),
                Stroke = Brushes.LimeGreen,
                StrokeThickness = greenThickness
            });

            const double labelFontSize = 20.25;
            for (int i = 0; i < TempValues.Length; i++)
            {
                double v = TempValues[i];
                double angle = TempAngles[i];
                bool isRed = v == TempRedlineValue;

                var tickInner = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, tickInnerRadius);
                var tickOuter = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, tickOuterRadius);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = tickInner.X, Y1 = tickInner.Y, X2 = tickOuter.X, Y2 = tickOuter.Y,
                    Stroke = isRed ? Brushes.Red : Brushes.White,
                    StrokeThickness = 3
                });

                // Individuelle Positions-Korrekturen je Zahl.
                var tempLabelOffsets = new Dictionary<double, (double dx, double dy)>
                {
                    [245.0] = (20, -20 - 5 - 4 - 3),
                    [200.0] = (-15 - 5 + 4 + 2, 0 + 2),
                    [150.0] = (-10 - 8 + 7 + 6, -10 - 3 + 2),
                    [100.0] = (-15 + 5 + 3, -7 - 3),
                    [75.0] = (20 - 5 + 3, 30 - 4)
                };
                var labelPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius + 28.5);
                var (dx, dy) = tempLabelOffsets.TryGetValue(v, out var off) ? off : (0, 0);
                GaugeDrawing.AddCenteredText(GaugeCanvas, v.ToString("0"), labelPoint.X + dx, labelPoint.Y + dy, labelFontSize, Brushes.White, true);
            }
        }

        /// <summary>
        /// PRESS-Skala rechts: 5 Hauptmesspunkte (0/20/60/100/115 PSI) mit
        /// Zahl, dazu unbeschriftete Zwischen-Ticks bei 30/40/50/80. 20 und 115
        /// als rote Redline-Ticks.
        /// </summary>
        private void DrawPressScaleArc(Point arcCenter, double radius)
        {
            // Bogen (Tick-Radius) so aufgezogen, dass die vertikale Höhe gleich
            // der von TEMP ist (165) - Außenradius 128,35. Ticks 80% so lang wie
            // bei TEMP (19,2 statt 24), gleiche Dicke (3).
            const double pressTickOuterRadius = 134.94; // korrigiert: Höhe jetzt aus TEMPs TATSÄCHLICHER Tick-Außenkante (137,85) hergeleitet, nicht aus der grünen-Bogen-Höhe
            const double pressTickLength = 19.2;
            const double pressTickInnerRadius = pressTickOuterRadius - pressTickLength;

            // Grüner Bogen von 47 bis 90 PSI, gleiche Breite (Dicke) wie bei TEMP
            // (13,5), äußerer Rand an der Tick-Außenkante (wie bei TEMP).
            // Winkel für 57/90 direkt aus der PRESS-Kalibrierung abgeleitet
            // (57 liegt im 20-60-Abschnitt, 90 im 60-100-Abschnitt).
            const double pressGreenThickness = 13.5;
            double pressGreenRadius = pressTickOuterRadius - pressGreenThickness / 2.0;
            var pressGreenStart = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, 267.0, pressGreenRadius);
            var pressGreenEnd = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, 295.0, pressGreenRadius);
            var pressGreenFigure = new PathFigure { StartPoint = pressGreenStart };
            pressGreenFigure.Segments.Add(new ArcSegment(
                pressGreenEnd, new Size(pressGreenRadius, pressGreenRadius), 0, false, SweepDirection.Clockwise, true));
            GaugeCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { pressGreenFigure }),
                Stroke = Brushes.LimeGreen,
                StrokeThickness = pressGreenThickness
            });

            void DrawTick(double angle, bool red)
            {
                var tickInner = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, pressTickInnerRadius);
                var tickOuter = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, pressTickOuterRadius);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = tickInner.X, Y1 = tickInner.Y, X2 = tickOuter.X, Y2 = tickOuter.Y,
                    Stroke = red ? Brushes.Red : Brushes.White,
                    StrokeThickness = 3
                });
            }

            const double labelFontSize = 20.25;
            var pressLabelOffsets = new Dictionary<double, (double dx, double dy)>
            {
                [0.0] = (-15 - 7, 26),
                [20.0] = (5 + 3, -5),
                [60.0] = (5 + 3, -3),
                [100.0] = (5, 8),
                [115.0] = (-15, -26 - 3)
            };
            for (int i = 0; i < PressValues.Length; i++)
            {
                double v = PressValues[i];
                double angle = PressAngles[i];
                bool isRed = v == PressLowRedlineValue || v == PressHighRedlineValue;
                DrawTick(angle, isRed);

                var labelPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius + 28.5);
                var (dx, dy) = pressLabelOffsets.TryGetValue(v, out var off) ? off : (0, 0);
                GaugeDrawing.AddCenteredText(GaugeCanvas, v.ToString("0"), labelPoint.X + dx, labelPoint.Y + dy, labelFontSize, Brushes.White, true);
            }

            // Unbeschriftete Zwischen-Ticks (30/40/50/80) - nur der Strich, keine Zahl.
            foreach (double angle in PressMinorAngles)
                DrawTick(angle, red: false);
        }

        private void DrawSideSegment(int side, double chordOffset)
        {
            double chordX = CenterX + side * chordOffset;
            // Für beide Seiten korrekt bis zum Kreisrand (nicht bis zur Mitte)
            // reichen lassen: linke Kante des Rechtecks jeweils exakt bestimmen.
            double rectLeft = side < 0 ? CenterX - OuterRadius - 1.5 : chordX;
            double rectRight = side < 0 ? chordX : CenterX + OuterRadius + 1.5;
            var segmentGeometry = new CombinedGeometry(
                GeometryCombineMode.Intersect,
                new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, OuterRadius),
                new RectangleGeometry(new Rect(rectLeft, CenterY - OuterRadius - 1.5, rectRight - rectLeft, OuterRadius * 2 + 3)));
            var segment = new System.Windows.Shapes.Path
            {
                Data = segmentGeometry,
                Fill = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2c))
            };
            GaugeCanvas.Children.Add(segment);
        }
    }
}
