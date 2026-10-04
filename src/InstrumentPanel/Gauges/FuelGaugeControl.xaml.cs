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
    public partial class FuelGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // jetzt genauso groß wie alle anderen Anzeigen - Skalierung nur noch übers Layout

        [StructLayout(LayoutKind.Sequential)]
        private struct FuelStruct
        {
            public double Left;  // Gallonen
            public double Right; // Gallonen
        }

        private const double ArcCenterOffset = 28.5; // 15/110 vom Rand, jetzt 5 weiter außen
        private const double ArcRadius = 81.6;        // 34/110

        private static readonly Point LeftArcCenterPoint = new Point((CenterX - OuterRadius) + ArcCenterOffset, CenterY);
        private static readonly Point RightArcCenterPoint = new Point((CenterX + OuterRadius) - ArcCenterOffset, CenterY);
        // Zeiger-Drehpunkt und Skalen-Mittelpunkt sind getrennte Punkte: der
        // Drehpunkt liegt 21 weiter zur Gehäuse-Mitte hin (größerer Abstand vom Rand).
        private const double NeedlePivotOffset = ArcCenterOffset + 21; // aktueller Endwert aller Korrekturen
        private static readonly Point LeftNeedlePivotPoint = new Point((CenterX - OuterRadius) + NeedlePivotOffset, CenterY);
        private static readonly Point RightNeedlePivotPoint = new Point((CenterX + OuterRadius) - NeedlePivotOffset, CenterY);

        private SimConnectService _service;
        private RotateTransform _leftNeedleRotate;
        private RotateTransform _rightNeedleRotate;
        // NaN = noch kein Messwert: beim ersten Wert (Start/Reconnect) springt der Zeiger sofort dorthin.
        private double _displayedLeft = double.NaN;
        private double _displayedRight = double.NaN;
        private DateTime? _lastUpdateTime;

        public FuelGaugeControl()
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
                    ("FUEL LEFT QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64),
                    ("FUEL RIGHT QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        private void OnData(FuelStruct data)
        {
            // NaN/Infinity verwerfen (würde sonst die Anzeige dauerhaft vergiften).
            if (double.IsNaN(data.Left) || double.IsInfinity(data.Left) ||
                double.IsNaN(data.Right) || double.IsInfinity(data.Right)) return;

            // Zeiger bei sprunghaften Wertänderungen (z.B. beim Betanken) nur mit
            // maximal 15 Gallonen/Sekunde bewegen, statt sofort zu springen.
            const double maxRatePerSecond = 15.0;
            var now = DateTime.UtcNow;
            double deltaTimeSec = _lastUpdateTime.HasValue ? (now - _lastUpdateTime.Value).TotalSeconds : 0;
            deltaTimeSec = Math.Max(0, Math.Min(1, deltaTimeSec)); // gegen Ausreißer (z.B. nach Pause) absichern
            _lastUpdateTime = now;

            if (double.IsNaN(_displayedLeft))
                _displayedLeft = data.Left;
            if (double.IsNaN(_displayedRight))
                _displayedRight = data.Right;

            double maxStep = maxRatePerSecond * deltaTimeSec;
            _displayedLeft = MoveTowards(_displayedLeft, data.Left, maxStep);
            _displayedRight = MoveTowards(_displayedRight, data.Right, maxStep);

            if (_leftNeedleRotate != null)
                _leftNeedleRotate.Angle = AngleForNeedle(_displayedLeft, LeftNeedlePivotPoint, LeftArcCenterPoint, mirrored: false);
            if (_rightNeedleRotate != null)
                _rightNeedleRotate.Angle = AngleForNeedle(_displayedRight, RightNeedlePivotPoint, RightArcCenterPoint, mirrored: true);
        }

        private static double MoveTowards(double current, double target, double maxStep)
        {
            double diff = target - current;
            if (Math.Abs(diff) <= maxStep) return target;
            // Ohne Math.Sign (wirft bei NaN eine ArithmeticException).
            if (diff > 0) return current + maxStep;
            if (diff < 0) return current - maxStep;
            return current;
        }

        // Gemeinsame Winkel-Formel (auch von DrawScaleArc genutzt): 26 oben, 0
        // unten, linear dazwischen, +90° Korrektur der Referenzachse.
        private static double AngleForFuelValue(double v, bool mirrored)
        {
            double clamped = Math.Max(0, Math.Min(26, v));
            double baseAngle = 55 - (clamped / 26.0) * 110.0;
            double a = mirrored ? -baseAngle + 180 : baseAngle;
            return a + 90;
        }

        // Zeiger-eigene Winkel-Formel: Zeiger- und Skalen-Drehpunkt sind
        // unterschiedliche Punkte, daher zielt die gleiche Winkelformel allein
        // nicht auf dieselbe Stelle. Stattdessen: tatsächliche 0°/26°-Positionen
        // auf der Skala berechnen, dann den Winkel vom Zeiger-Drehpunkt aus zu
        // diesen beiden Zielpunkten bestimmen (per atan2) und linear interpolieren.
        private static double AngleForNeedle(double v, Point needlePivot, Point arcCenter, bool mirrored)
        {
            double clamped = Math.Max(0, Math.Min(26, v));
            double angle0 = AngleForFuelValue(0, mirrored);
            double angle26 = AngleForFuelValue(26, mirrored);
            var target0 = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle0, ArcRadius);
            var target26 = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle26, ArcRadius);

            double needleAngle0 = Math.Atan2(target0.X - needlePivot.X, needlePivot.Y - target0.Y) * 180.0 / Math.PI;
            double needleAngle26 = Math.Atan2(target26.X - needlePivot.X, needlePivot.Y - target26.Y) * 180.0 / Math.PI;

            return needleAngle0 + (clamped / 26.0) * (needleAngle26 - needleAngle0);
        }

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
            DrawScaleArc(LeftArcCenterPoint, ArcRadius, mirrored: false);
            DrawScaleArc(RightArcCenterPoint, ArcRadius, mirrored: true);

            // Zeiger im VSI-Stil (Basis mit kleiner 45°-Spitze hinten, Hauptspitze
            // läuft spitz zu), Drehpunkt getrennt vom Skalen-Mittelpunkt
            // (siehe NeedlePivotOffset). Liegt über den Bögen/
            // Zahlen, aber hinter den schwarzen Kreissegmenten (weiter unten).
            _leftNeedleRotate = DrawNeedle(LeftNeedlePivotPoint);
            _rightNeedleRotate = DrawNeedle(RightNeedlePivotPoint);
            // Startwinkel direkt auf "0" setzen, damit der Zeiger vor der ersten
            // MSFS-Verbindung nicht senkrecht (WPF-Standardwinkel) steht.
            _leftNeedleRotate.Angle = AngleForNeedle(0, LeftNeedlePivotPoint, LeftArcCenterPoint, mirrored: false);
            _rightNeedleRotate.Angle = AngleForNeedle(0, RightNeedlePivotPoint, RightArcCenterPoint, mirrored: true);

            // Senkrechte Kreissegmente an beiden Seiten (23% der Kreisfläche je
            // Segment) - Sehne bei ±38,6 vom Mittelpunkt (rechnerisch ermittelt).
            // NACH dem Zeiger gezeichnet, damit sie ihn an den Seiten verdecken.
            const double chordOffset = 76.8; // 23/110 lineares Verhältnis (Segment-Breite : Durchmesser)
            DrawSideSegment(-1, chordOffset);
            DrawSideSegment(1, chordOffset);

            // "LEFT"/"RIGHT"-Schriftzüge: Buchstaben gestapelt, senkrecht mittig zur
            // Horizontlinie, linke/rechte Kante bei 14/110 vom jeweiligen Rand.
            const double textEdgeOffset = (14.0 / 110.0) * (OuterRadius * 2);
            const double estimatedHalfLetterWidth = 6.75;
            double leftTextCenterX = (CenterX - OuterRadius) + textEdgeOffset + estimatedHalfLetterWidth;
            double rightTextCenterX = (CenterX + OuterRadius) - textEdgeOffset - estimatedHalfLetterWidth;
            DrawStackedLabel("LEFT", leftTextCenterX, CenterY);
            DrawStackedLabel("RIGHT", rightTextCenterX, CenterY);

            // Mittlere Texte auf der senkrechten Mittellinie, möglichst weit oben/unten.
            GaugeDrawing.AddCenteredText(GaugeCanvas, "FUEL", CenterX, CenterY - 117, 25.3125, Brushes.White, true);

            // GALLONS-Schriftgröße so berechnen, dass die Wortbreite exakt der von
            // QTY entspricht (Breiten zur Laufzeit vermessen, nicht geschätzt).
            var qtyMeasure = new TextBlock { Text = "QTY", FontSize = 25.3125, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold };
            qtyMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var gallonsMeasureRef = new TextBlock { Text = "GALLONS", FontSize = 25.3125, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold };
            gallonsMeasureRef.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double gallonsFontSize = 25.3125 * (qtyMeasure.DesiredSize.Width / gallonsMeasureRef.DesiredSize.Width);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "GALLONS", CenterX, CenterY + 103.5, gallonsFontSize, Brushes.White, true);

            GaugeDrawing.AddCenteredText(GaugeCanvas, "QTY", CenterX, CenterY + 117, 25.3125, Brushes.White, true);
        }

        private RotateTransform DrawNeedle(Point pivot)
        {
            const double needleLength = 81; // 45/110, jetzt um 5+3+10 gekürzt
            const double tanTailAngle = 0.26794919; // tan(15°), wie beim VSI
            const double oldHubHalfWidth = (needleLength * 0.25) * tanTailAngle;
            const double hubHalfWidth = oldHubHalfWidth * 1.5 * 0.8; // 20% schmaler an der Basis
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

        private void DrawStackedLabel(string text, double centerX, double centerY)
        {
            const double fontSize = 20.25; // +50% (war 9)
            const double letterSpacing = 24;
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
        private void DrawScaleArc(Point arcCenter, double radius, bool mirrored)
        {
            // Winkel-Formel: 26 oben (+55° vor Spiegelung/Korrektur), 0 unten (-55°) -
            // umgekehrt zur vorherigen Version. +90° Korrektur der Referenzachse.
            double AngleFor(double v) => AngleForFuelValue(v, mirrored);

            double startAngle = AngleFor(26); // oben
            double endAngle = AngleFor(0);    // unten
            var arcStart = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, startAngle, radius);
            var arcEndPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, endAngle, radius);
            var arcFigure = new PathFigure { StartPoint = arcStart };
            arcFigure.Segments.Add(new ArcSegment(
                arcEndPoint, new Size(radius, radius), 0, false,
                mirrored ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true));
            GaugeCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { arcFigure }),
                Stroke = Brushes.White,
                StrokeThickness = 6.75
            });

            const double labelFontSize = 20.25; // so groß wie FUEL
            const double halfStrokeGap = 3; // halbe Strichstärke (4/2)
            const double textHeight = 24; // ungefähre Texthöhe bei 13.5pt

            // Für "5"/"20": senkrechter Abstand von der jeweiligen Strich-Kante
            // (nicht entlang der schrägen Strichrichtung, die bei seitlichen
            // Strichen sonst zu wenig vertikalen Abstand ergibt).
            double YBelowTick(double angle)
            {
                var a = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius - 13.5);
                var b = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius + 13.5);
                double tickBottomY = Math.Max(a.Y, b.Y); // die weiter unten liegende Strich-Kante
                return tickBottomY + halfStrokeGap + textHeight / 2.0 - 6; // 4 (Strichstärke) höher als zuvor
            }

            // "5" separat berechnen (wird für "20" als Spiegel-Referenz gebraucht),
            // zusätzlich 5 Einheiten näher zur Mittellinie.
            double y5 = YBelowTick(AngleFor(5)) - 7.5;
            double offset5FromCenter = y5 - arcCenter.Y;

            var values = new[] { 0.0, 5, 10, 15, 20, 26 };
            foreach (double v in values)
            {
                double angle = AngleFor(v);
                bool isRed = v == 0;

                // Doppelt so lang, mittig auf dem Bogen (radius ± 6 statt radius-6 bis radius).
                var tickInner = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius - 13.5);
                var tickOuter = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius + 13.5);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = tickInner.X, Y1 = tickInner.Y, X2 = tickOuter.X, Y2 = tickOuter.Y,
                    Stroke = isRed ? Brushes.Red : Brushes.White,
                    StrokeThickness = 6
                });

                if (v == 0 || v == 26)
                {
                    // Endpunkte jeweils an ihrem eigenen Strich beschriften ("0" jetzt
                    // weiß statt rot - nur der Strich selbst bleibt rot).
                    var labelPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius + 28.5);
                    double x = labelPoint.X;
                    double y = labelPoint.Y;
                    if (v == 26)
                    {
                        // Halb wieder zurück (2 statt 4) + Verschiebung Richtung Mitte
                        // jetzt korrekt gespiegelt (links: -9, rechts: +9).
                        y -= 3;
                        x += mirrored ? 13.5 : -13.5;
                    }
                    else if (v == 0)
                    {
                        y -= 4.5;
                    }
                    GaugeDrawing.AddCenteredText(GaugeCanvas, v.ToString("0"), x, y, labelFontSize, Brushes.White, true);
                }
                else if (!mirrored)
                {
                    // 5/10/15/20: nur einmal (beim linken Aufruf), zentriert auf der
                    // 12-Uhr-Mittellinie, gilt für beide Skalen gleichzeitig.
                    double y;
                    if (v == 5)
                        y = y5;
                    else if (v == 20)
                        y = arcCenter.Y - offset5FromCenter + 7.5; // 5 näher zur Mittellinie
                    else
                        y = YBelowTick(angle) + (v == 10 ? 1.5 : 0); // 10: um 1 wieder hoch (war +2)
                    GaugeDrawing.AddCenteredText(GaugeCanvas, v.ToString("0"), CenterX, y, labelFontSize, Brushes.White, true);
                }
            }
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
