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
    /// <summary>
    /// Reine Libelle (Kugel-Neigungsmesser / Slip Indicator): Bezel und Kugel vom
    /// Turn Coordinator, aber ohne Flugzeug-Symbol, Referenzmarken und OFF-Klappe.
    /// Die Röhre ist ein Kreisbogen (Enden höher als die Mitte); die Kugel läuft
    /// darauf entlang und steigt zu den Enden hin an. Die Röhrenenden sind vollständig
    /// sichtbar und enden TubeEndMargin (10) vor dem inneren Rand des Zifferblatts.
    /// </summary>
    public partial class SlipIndicatorGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;

        private const double BallDiameter = 40;      // Breite der Kugel; die Höhe füllt die Röhre (TubeThickness) - Kugel ist oval
        // Max. seitlicher Weg der Kugelmitte: Mittelpunkt der Endkappe, so bleibt die Kugel bei Vollausschlag komplett in der Röhre.
        private static double BallMaxOffset => TubeHalfExtent;

        private const double TubeCurveRadius = 450;  // Krümmungsradius der Röhren-Mittellinie (kleiner = stärker gebogen)
        private const double TubeLowestY = CenterY + 28; // Mittellinie der Röhre an ihrem tiefsten Punkt (Mitte)
        private const double TubeThickness = 50;      // Röhrendicke (Höhe); Kugelhöhe = gleicher Wert
        private const double TubeOutlineWidth = 1.5;  // Kontur außen um die Röhre (je Seite)
        private const double FaceRadius = 132;        // Radius des schwarzen Zifferblatts (innerer Rand)
        private const double TubeEndMargin = 10;     // Abstand der Röhrenenden zum inneren Rand

        // Waagrechte Halblänge der Röhren-Mittellinie: so, dass das äußerste Ende der Röhre
        // (inkl. Rundung und Kontur) genau TubeEndMargin vor dem inneren Rand liegt.
        private static readonly double TubeHalfExtent = ComputeTubeHalfExtent();

        private static double ComputeTubeHalfExtent()
        {
            double target = FaceRadius - TubeEndMargin - (TubeThickness / 2 + TubeOutlineWidth);
            double lo = 0, hi = TubeCurveRadius * 0.9;
            for (int i = 0; i < 60; i++)
            {
                double mid = (lo + hi) / 2;
                double dy = TubeCenterlineY(mid) - CenterY;
                if (Math.Sqrt(mid * mid + dy * dy) < target) lo = mid; else hi = mid;
            }
            return lo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SlipStruct
        {
            public double Ball; // Rohwert (Einheit "position"), per ballDivisor auf -1..1 normiert
        }

        private TranslateTransform _ballTranslate;

        public SlipIndicatorGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            service.Register<SlipStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("TURN COORDINATOR BALL", "position", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private void OnData(SlipStruct data)
        {
            if (!IsFinite(data.Ball)) return;

            // Gleiche Einstellung wie beim Turn Coordinator ("turnCoordinator.ballDivisor").
            double ballScale = AppSettings.TurnCoordinatorBallDivisor;
            if (!IsFinite(ballScale) || ballScale == 0) ballScale = 1.0;

            double clamped = Math.Max(-1, Math.Min(1, data.Ball / ballScale));
            double dx = clamped * BallMaxOffset;

            if (_ballTranslate != null)
            {
                // Kugelmitte folgt dem Kreisbogen: seitlicher Weg dx, Höhenanstieg dy (negativ = nach oben).
                _ballTranslate.X = dx;
                _ballTranslate.Y = Math.Sqrt(TubeCurveRadius * TubeCurveRadius - dx * dx) - TubeCurveRadius;
            }
        }

        // Y der Röhren-Mittellinie bei seitlichem Abstand x von der Mitte.
        private static double TubeCenterlineY(double x) =>
            TubeLowestY + Math.Sqrt(TubeCurveRadius * TubeCurveRadius - x * x) - TubeCurveRadius;

        private void DrawGaugeFace()
        {
            // Bezel: identisch zum Turn Coordinator
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
            GaugeCanvas.Children.Add(outerBezel);

            var innerBezel = new Ellipse { Width = 282, Height = 282, Fill = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2c)) };
            Canvas.SetLeft(innerBezel, 9);
            Canvas.SetTop(innerBezel, 9);
            GaugeCanvas.Children.Add(innerBezel);

            var face = new Ellipse { Width = 264, Height = 264, Fill = Brushes.Black };
            Canvas.SetLeft(face, 18);
            Canvas.SetTop(face, 18);
            GaugeCanvas.Children.Add(face);

            // Beschriftung: L/R über den Röhrenenden
            GaugeDrawing.AddCenteredText(GaugeCanvas, "L", CenterX - 100, CenterY - 42, 26, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "R", CenterX + 100, CenterY - 42, 26, Brushes.White, true);

            DrawBallTube();
        }

        private void DrawBallTube()
        {
            // Rund geklippt auf das Zifferblatt (Radius 132): die Röhre läuft unter den Bezel.
            var ballArea = new Canvas
            {
                Clip = new EllipseGeometry(new Point(CenterX, CenterY), 132, 132)
            };

            // Röhre als gebogene Linie mit dicker Strichstärke (Kreisbogen, Mitte unten).
            var start = new Point(CenterX - TubeHalfExtent, TubeCenterlineY(-TubeHalfExtent));
            var end = new Point(CenterX + TubeHalfExtent, TubeCenterlineY(TubeHalfExtent));
            var geometry = new PathGeometry(new[]
            {
                new PathFigure(start, new PathSegment[]
                {
                    new ArcSegment(end, new Size(TubeCurveRadius, TubeCurveRadius), 0, false,
                        SweepDirection.Counterclockwise, true)
                }, false)
            });

            // Äußere, hellgraue Kontur (leicht dicker), darüber die Glasröhre mit Verlauf.
            ballArea.Children.Add(new Path
            {
                Data = geometry,
                Stroke = Brushes.Gainsboro,
                StrokeThickness = TubeThickness + TubeOutlineWidth * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            ballArea.Children.Add(new Path
            {
                Data = geometry,
                Stroke = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0xF6, 0xF6, 0xF2), 0.0),
                        new GradientStop(Color.FromRgb(0xD8, 0xD8, 0xD2), 0.5),
                        new GradientStop(Color.FromRgb(0x98, 0x98, 0x92), 1.0)
                    }, 90),
                StrokeThickness = TubeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            // Mittenmarkierungen: radial zur Röhre, genau eine Kugelbreite auseinander.
            double centerOfCurvatureY = TubeLowestY - TubeCurveRadius;
            double markAngle = Math.Asin((BallDiameter / 2) / TubeCurveRadius);
            foreach (int side in new[] { -1, 1 })
            {
                double sin = side * Math.Sin(markAngle);
                double cos = Math.Cos(markAngle);
                double rIn = TubeCurveRadius - TubeThickness / 2;
                double rOut = TubeCurveRadius + TubeThickness / 2;
                ballArea.Children.Add(new Line
                {
                    X1 = CenterX + rIn * sin, Y1 = centerOfCurvatureY + rIn * cos,
                    X2 = CenterX + rOut * sin, Y2 = centerOfCurvatureY + rOut * cos,
                    Stroke = Brushes.Black,
                    StrokeThickness = 2.5
                });
            }

            // Kugel: dunkel mit leichtem Glanzpunkt; Ruhelage = tiefster Punkt der Röhre.
            var ball = new Ellipse
            {
                Width = BallDiameter,
                Height = TubeThickness,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x7A, 0x7A, 0x7A), 0.0),
                        new GradientStop(Color.FromRgb(0x1C, 0x1C, 0x1C), 0.55),
                        new GradientStop(Colors.Black, 1.0)
                    })
                {
                    GradientOrigin = new Point(0.35, 0.3),
                    Center = new Point(0.5, 0.5)
                },
                Stroke = Brushes.Gray,
                StrokeThickness = 1
            };
            Canvas.SetLeft(ball, CenterX - BallDiameter / 2);
            Canvas.SetTop(ball, TubeLowestY - TubeThickness / 2);
            _ballTranslate = new TranslateTransform(0, 0);
            ball.RenderTransform = _ballTranslate;
            ballArea.Children.Add(ball);

            GaugeCanvas.Children.Add(ballArea);
        }
    }
}
