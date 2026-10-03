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
    public partial class VerticalSpeedGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // entspricht dem Rand des schwarzen Zifferblatts

        // Kalibrierpunkte (ausgemessen): 0 bei 9-Uhr (=270° in unserer Konvention,
        // 0°=oben/12-Uhr, im Uhrzeigersinn). Winkel-OFFSET von 270° für die
        // Skala ist linear (finale MSFS-Messwerte: 42°/43°/43°/44° pro 5-Einheiten-
        // Abschnitt - praktisch identisch). Steigung aus den Endpunkten 0->0° und
        // 20->172°.
        private const double DegreesPerUnit = 172.0 / 20.0;

        [StructLayout(LayoutKind.Sequential)]
        private struct VsiStruct
        {
            public double Vsi; // ft/min
        }

        private RotateTransform _needleRotation;

        public VerticalSpeedGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            service.Register<VsiStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE.FLOAT64)
                },
                data => UpdateNeedle(data.Vsi));
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird zentral im Fenster (MainWindow) angezeigt.
        }

        /// <summary>
        /// Winkel für einen Wert in der x100-ft/min-Skala (z.B. 5 = 500 ft/min),
        /// linear (siehe DegreesPerUnit). Positiv = Steigen (im Uhrzeigersinn von
        /// 270° aus), negativ = Sinken (spiegelverkehrt, gegen den Uhrzeigersinn).
        /// Wird für die STRICHE verwendet - deren Position bleibt fix, unabhängig
        /// vom mechanischen Zeiger-Anschlag.
        /// </summary>
        private static double AngleForTickUnits(double units)
        {
            double sign = units >= 0 ? 1 : -1;
            double absUnits = Math.Min(20, Math.Abs(units));
            double offset = absUnits * DegreesPerUnit;

            return 270 + sign * offset;
        }

        /// <summary>
        /// Winkel für den ZEIGER: wie AngleForTickUnits, aber der mechanische
        /// Anschlag geht tatsächlich bis exakt 3-Uhr (180° Offset von 270°) -
        /// weiter als der 20er-Strich selbst (172°) -, sobald der reale Wert
        /// 20 erreicht oder überschreitet. Der Zeiger überragt den Strich also
        /// bei Vollausschlag bewusst etwas.
        /// </summary>
        private static double AngleForNeedleUnits(double units)
        {
            double sign = units >= 0 ? 1 : -1;
            double absUnitsRaw = Math.Abs(units);

            if (absUnitsRaw >= 20)
                return 270 + sign * 180;

            return AngleForTickUnits(units);
        }

        private static double AngleForFpm(double fpm) => AngleForNeedleUnits(fpm / 100.0);

        private void UpdateNeedle(double fpm)
        {
            double angle = AngleForFpm(fpm);
            DebugLog.Write("[VSI] raw fpm=" + fpm + ", units=" + (fpm / 100.0) + ", angle=" + angle);
            if (_needleRotation != null)
                _needleRotation.Angle = angle;
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

            // Striche: 0-10 alle 1 Einheit (100 ft/min), 10-20 alle 2.5 Einheiten
            // (250 ft/min) - auf beiden Seiten (Steigen positiv, Sinken negativ).
            DrawTicksForSign(1);
            DrawTicksForSign(-1);
            DrawMajorTickAndLabel(0); // die 0 gehört beiden Seiten gemeinsam

            // Die "20"-Beschriftung gibt es nur einmal, genau bei 3-Uhr (90°) -
            // gemeinsam für beide 20er-Striche (Steigen bei 83°, Sinken bei 97°).
            var label20Point = GaugeDrawing.PointOnCircle(CenterX, CenterY, 90, OuterRadius - 44);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "20", label20Point.X, label20Point.Y, 24, Brushes.White, true);

            // UP/DN-Markierungen (kleine Dreiecke), nah an der 0
            DrawUpDownMarker(3, Brushes.DodgerBlue, "UP");
            DrawUpDownMarker(-3, Brushes.OrangeRed, "DN");

            GaugeDrawing.AddCenteredText(GaugeCanvas, "VERTICAL", CenterX + 28, CenterY - 40, 13, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "SPEED", CenterX + 28, CenterY - 24, 13, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "100 FEET", CenterX + 28, CenterY + 30, 10, Brushes.LightGray, false);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "PER MIN", CenterX + 28, CenterY + 44, 10, Brushes.LightGray, false);

            // Zeiger: Form wie beim Drehzahlmesser (Basis mit kleiner 45°-Spitze,
            // Hauptspitze läuft davon geradlinig spitz zu), aber die breiteste
            // Stelle sitzt jetzt direkt am Drehpunkt statt weit dahinter -
            // "Ende an der dicksten Stelle stellt den Drehpunkt dar".
            const double needleLength = 118; // VSI-Spitze bleibt wie gehabt
            const double tanTailAngle = 0.26794919; // tan(15°)
            const double oldHubHalfWidth = (needleLength * 0.25) * tanTailAngle;
            const double hubHalfWidth = oldHubHalfWidth * 1.5;
            double wideY = CenterY;                    // breiteste Stelle direkt am Drehpunkt
            double tipY = CenterY - needleLength;
            double tailTipY = wideY + hubHalfWidth;     // kleine 45°-Spitze nur knapp dahinter

            var needle = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, tipY),                      // anzeigende Spitze
                    new Point(CenterX - hubHalfWidth, wideY),       // breiteste Stelle links
                    new Point(CenterX, tailTipY),                  // Heck: kleine 45°-Spitze
                    new Point(CenterX + hubHalfWidth, wideY)        // breiteste Stelle rechts
                }),
                Fill = Brushes.White
            };
            _needleRotation = new RotateTransform(270, CenterX, CenterY);
            needle.RenderTransform = _needleRotation;
            GaugeCanvas.Children.Add(needle);
        }

        private void DrawTicksForSign(int sign)
        {
            // 0-10 in 1er-Schritten (ohne die 0 selbst, die wird separat gezeichnet)
            for (double u = 1; u <= 10; u += 1)
                DrawTickAt(u * sign, u % 5 == 0);

            // 10-15-20: keine Teilstriche, nur die großen Striche selbst
            DrawTickAt(15 * sign, true);

            // Die 20 ist ein Sonderfall: beide Striche (Steigen/Sinken) haben ihre
            // eigene Position (83°/97°), aber es gibt nur EINE gemeinsame "20"-
            // Beschriftung genau bei 3-Uhr (90°) - daher hier nur der Strich,
            // die Beschriftung kommt separat (einmalig) in DrawGaugeFace.
            DrawMajorTick(20 * sign);
        }

        private void DrawTickAt(double units, bool isMajor)
        {
            if (isMajor)
            {
                DrawMajorTickAndLabel(units);
                return;
            }

            double angle = AngleForTickUnits(units);
            double outer = OuterRadius - 4;
            double inner = outer - 12;
            GaugeDrawing.DrawRadialLine(GaugeCanvas, CenterX, CenterY, angle, Brushes.White, 2, inner, outer);
        }

        private void DrawMajorTick(double units)
        {
            double angle = AngleForTickUnits(units);
            double outer = OuterRadius - 4;
            double inner = outer - 22;
            GaugeDrawing.DrawRadialLine(GaugeCanvas, CenterX, CenterY, angle, Brushes.White, 3.5, inner, outer);
        }

        private void DrawMajorTickAndLabel(double units)
        {
            DrawMajorTick(units);
            double angle = AngleForTickUnits(units);
            var labelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, OuterRadius - 44);
            GaugeDrawing.AddCenteredText(GaugeCanvas, Math.Abs(units).ToString("0"), labelPoint.X, labelPoint.Y, 24, Brushes.White, true);
        }

        private void DrawUpDownMarker(double units, Brush brush, string label)
        {
            double angle = AngleForTickUnits(units);
            double dirSign = units >= 0 ? 1 : -1; // Richtung wachsenden Werts: im Uhrzeigersinn (UP) bzw. gegen (DN)
            double outer = OuterRadius - 30;
            double inner = outer - 14;
            double midR = (outer + inner) / 2;

            // Basis quer zur Laufrichtung (hinten), Spitze zeigt in Richtung
            // wachsenden Werts - dadurch ein klar erkennbarer Richtungspfeil.
            var baseOuter = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle - dirSign * 8, outer);
            var baseInner = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle - dirSign * 8, inner);
            var tip = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle + dirSign * 6, midR);

            var marker = new Polygon
            {
                Points = new PointCollection(new[] { baseOuter, baseInner, tip }),
                Fill = brush
            };
            GaugeCanvas.Children.Add(marker);

            var labelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, midR - 22);
            GaugeDrawing.AddCenteredText(GaugeCanvas, label, labelPoint.X, labelPoint.Y, 9, Brushes.White, true);
        }
    }
}
