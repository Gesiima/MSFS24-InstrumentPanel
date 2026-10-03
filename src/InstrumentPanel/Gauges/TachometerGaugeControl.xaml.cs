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
    public partial class TachometerGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // entspricht dem Rand des schwarzen Zifferblatts

        // Skala: 500 U/min bei 270°, 3000 U/min bei 90° (Uhrzeigersinn über die
        // Oberseite hinweg, wie bei einem klassischen Drehzahlmesser).
        private const double RpmMin = 0;
        private const double RpmMax = 3500;

        // Kalibrierungs-Anker (bleiben fix bei 270°/90°), die Skala wird mit
        // dieser Steigung nach 0 bzw. 3500 hin verlängert (extrapoliert),
        // statt den ganzen 0-3500-Bereich in denselben Winkelbereich zu pressen.
        private const double CalRpmLow = 500, CalAngleLow = 270;
        private const double CalRpmHigh = 3000, CalAngleHigh = 90 + 360; // +360, Sweep über oben
        private const double DegPerRpm = (CalAngleHigh - CalAngleLow) / (CalRpmHigh - CalRpmLow);

        private const double GreenStart = 2100;
        private const double GreenEnd = 2700; // korrigiert von 2800 auf 2700

        [StructLayout(LayoutKind.Sequential)]
        private struct TachStruct
        {
            public double Rpm;
        }

        private RotateTransform _needleRotation;

        public TachometerGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            service.Register<TachStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("GENERAL ENG RPM:1", "Rpm", SIMCONNECT_DATATYPE.FLOAT64)
                },
                data => UpdateNeedle(data.Rpm));
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird zentral im Fenster (MainWindow) angezeigt.
        }

        private static double AngleForRpm(double rpm)
        {
            double clamped = Math.Max(RpmMin, Math.Min(RpmMax, rpm));
            return CalAngleLow + (clamped - CalRpmLow) * DegPerRpm;
        }

        private void UpdateNeedle(double rpm)
        {
            if (_needleRotation != null)
                _needleRotation.Angle = AngleForRpm(rpm);
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

            // Grüner Normalbereich - alle drei Segmente (100%/75%/50%) jetzt
            // zusammen VOR den Strichen gezeichnet (gleiche Ebene wie zuvor),
            // da keine Überdeckung von Ticks mehr nötig ist.
            const double greenThickness = 14;
            const double greenOuterRadius = 118 + greenThickness / 2; // feste Außenkante aller drei Segmente
            DrawArcBand(GreenStart, 2500, Brushes.LimeGreen, greenThickness, greenOuterRadius - greenThickness / 2);
            DrawArcBand(2500, 2600, Brushes.LimeGreen, greenThickness * 0.75, greenOuterRadius - greenThickness * 0.75 / 2);
            DrawArcBand(2600, GreenEnd, Brushes.LimeGreen, greenThickness * 0.5, greenOuterRadius - greenThickness * 0.5 / 2);

            // Striche alle 500 U/min (groß, mit Zahl X100) / alle 100 U/min (klein).
            // Strichlänge wie beim Höhenmesser, aber doppelt so dick.
            const double greenOuterEdge = 125; // muss zur Außenkante des grünen Bereichs oben passen
            const double labelTextRadius = 120; // Mittelpunkt des Textfelds für SL/5/10 (zum Testen)
            for (double rpm = RpmMin; rpm <= RpmMax; rpm += 100)
            {
                bool isMajor = rpm % 500 == 0;
                double angle = AngleForRpm(rpm);
                double outer = OuterRadius - 4;
                double inner = isMajor ? outer - 22 : outer - 13;

                // 2500 ("SL") und 2600 ("5") bekommen zusätzlich Text, UND sind nur
                // von 128 bis 125 sichtbar (also NUR oberhalb des grünen Bereichs,
                // reichen nicht bis in dessen Fläche hinein) - dadurch reichen sie
                // wie alle anderen Ticks bis 128 nach außen, brauchen aber KEINE
                // Überdeckung durch den grünen Bereich mehr, da kein Überlapp
                // besteht.
                if (rpm == 2500 || rpm == 2600)
                {
                    outer = OuterRadius - 4; // 128, wie alle anderen Ticks
                    inner = greenOuterEdge; // 125, endet direkt an der Grün-Außenkante
                    string replacementLabel = rpm == 2500 ? "SL" : "5";
                    double labelAngle = angle - 1 + 0.5; // 1° entgegen Uhrzeigersinn, dann 0,5° Richtung 3-Uhr (im Uhrzeigersinn) zurück
                    var replacementPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, labelAngle, labelTextRadius);
                    GaugeDrawing.AddCenteredText(GaugeCanvas, replacementLabel, replacementPoint.X, replacementPoint.Y, 10, Brushes.White, true, labelAngle - 90);
                }

                var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, inner);
                var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, outer);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = p0.X,
                    Y1 = p0.Y,
                    X2 = p1.X,
                    Y2 = p1.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = isMajor ? 6 : 3
                });

                if (isMajor)
                {
                    var labelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, OuterRadius - 42);
                    GaugeDrawing.AddCenteredText(GaugeCanvas, (rpm / 100).ToString("0"), labelPoint.X, labelPoint.Y, 22, Brushes.White, true);
                }
            }

            // Roter Strich am Ende des grünen Bereichs, so lang wie die 500er-Striche
            DrawRadialLine(GreenEnd, Brushes.Red, 6, OuterRadius - 4 - 22, OuterRadius - 4);

            // "10" auf dem roten Strich, an dessen Unterkante (Innenradius) - wie bei
            // "SL"/"5" oben, laut Referenzfoto.
            double redLineLabelAngle = AngleForRpm(GreenEnd) - 1; // 1° entgegen des Uhrzeigersinns
            var redLineLabelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, redLineLabelAngle, labelTextRadius);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "10", redLineLabelPoint.X, redLineLabelPoint.Y, 10, Brushes.White, true, redLineLabelAngle - 90);

            GaugeDrawing.AddCenteredText(GaugeCanvas, "RPM", CenterX, CenterY - 65, 14, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "X100", CenterX, CenterY - 45, 11, Brushes.LightGray, false);

            // Zeiger: die anzeigende Spitze (Messwert) bleibt scharf spitz. Das hintere
            // Ende (Gegengewicht, überragt den Drehpunkt) ist an seiner breitesten
            // Stelle flach - bekommt jetzt eine kleine 45°-Spitze, damit es nicht
            // flach endet (4 Ecken: Spitze, breit-links, Heck-Spitze, breit-rechts).
            const double needleLength = 115;
            const double tanTailAngle = 0.26794919; // tan(15°)
            const double oldHubHalfWidth = (needleLength * 0.25) * tanTailAngle; // bisherige Breite als Basis
            const double hubHalfWidth = oldHubHalfWidth * 1.5; // +50% an der breitesten Stelle
            double wideY = CenterY + hubHalfWidth / tanTailAngle; // breiteste Stelle, Überstand hinter dem Drehpunkt (~44)
            double tipY = CenterY - needleLength;
            double tailTipY = wideY + hubHalfWidth; // kleine 45°-Spitze dahinter (Einzug = Breite = 45°)

            var needle = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, tipY),                      // anzeigende Spitze (Messwert) - bleibt spitz
                    new Point(CenterX - hubHalfWidth, wideY),       // breiteste Stelle links
                    new Point(CenterX, tailTipY),                  // Heck: kleine 45°-Spitze
                    new Point(CenterX + hubHalfWidth, wideY)        // breiteste Stelle rechts
                }),
                Fill = Brushes.White
            };
            _needleRotation = new RotateTransform(AngleForRpm(RpmMin), CenterX, CenterY);
            needle.RenderTransform = _needleRotation;
            GaugeCanvas.Children.Add(needle);
        }

        private void DrawArcBand(double fromRpm, double toRpm, Brush brush, double thickness, double radius)
            => GaugeDrawing.DrawArcBand(GaugeCanvas, CenterX, CenterY,
                AngleForRpm(fromRpm), AngleForRpm(toRpm), brush, thickness, radius);

        private void DrawRadialLine(double rpm, Brush brush, double thickness, double innerRadius, double outerRadius)
            => GaugeDrawing.DrawRadialLine(GaugeCanvas, CenterX, CenterY,
                AngleForRpm(rpm), brush, thickness, innerRadius, outerRadius);
    }
}
