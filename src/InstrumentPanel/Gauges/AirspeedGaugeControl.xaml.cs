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
    public partial class AirspeedGaugeControl : UserControl, IGauge
    {
        // ---------------------------------------------------------------
        // Typische V-Geschwindigkeiten einer Cessna 172S (Knoten, KIAS).
        // Bei einer anderen 172-Variante hier einfach anpassen.
        // ---------------------------------------------------------------
        private const double DialMaxSpeed = 200;   // Skalenende (entspricht der echten Anzeige im Simulator)
        private const double VFE = 85;              // weißer Bogen: Ende (max. Klappengeschwindigkeit)
        private const double VS1 = 48;              // grüner Bogen: Anfang (VS0, Stall in Landekonfiguration)
        private const double VNO = 129;             // grüner Bogen: Ende / gelber Bogen: Anfang
        private const double VNE = 163;             // rote Strichmarkierung (nie überschreiten)

        // Zeiger-Geometrie: nicht-lineare Skala, umgekehrt gestaucht - die höheren
        // Geschwindigkeiten liegen enger beieinander als die niedrigeren.
        // Kalibriert auf zwei Referenzpunkte: 40 kt ≈ 35°, 200 kt ≈ 317,5°,
        // mit Winkel ∝ Wurzel(Geschwindigkeit). Gilt nur für den sichtbaren
        // Bereich ab 40 kt; darunter (0-40, nicht beschriftet) wird linear bis
        // zum Ruhebereich interpoliert.
        private const double CalSpeedLow = 40, CalAngleLow = 35;
        private const double CalSpeedHigh = 200, CalAngleHigh = 317.5;
        private static readonly double SqrtB =
            (CalAngleHigh - CalAngleLow) / (Math.Sqrt(CalSpeedHigh) - Math.Sqrt(CalSpeedLow));
        private static readonly double SqrtA = CalAngleLow - SqrtB * Math.Sqrt(CalSpeedLow);
        private const double ScaleStart = 40; // erster gezeichneter Skalenstrich
        private const double RestThreshold = 30; // bis hierhin bleibt der Zeiger unbewegt oben stehen
        private static readonly double AngleAtScaleStart = SqrtA + SqrtB * Math.Sqrt(ScaleStart);

        /// <summary>
        /// Winkel (0° = 12 Uhr/oben, im Uhrzeigersinn) für eine gegebene Geschwindigkeit.
        /// Ab dem Skalenanfang (40 kt) gilt die Wurzel-Kalibrierung. Darunter, bis
        /// RestThreshold (30 kt), bleibt der Zeiger unbewegt bei 0°. Dazwischen
        /// (30-40 kt) wird linear zur Ruheposition interpoliert.
        /// </summary>
        private static double AngleForSpeed(double speed)
        {
            if (speed <= RestThreshold)
                return 0;

            if (speed <= ScaleStart)
                return (speed - RestThreshold) / (ScaleStart - RestThreshold) * AngleAtScaleStart;

            return SqrtA + SqrtB * Math.Sqrt(speed);
        }

        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // entspricht dem Rand des schwarzen Zifferblatts

        [StructLayout(LayoutKind.Sequential)]
        private struct AirspeedStruct
        {
            public double Airspeed;
        }

        private RotateTransform _needleRotation;

        public AirspeedGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            service.Register<AirspeedStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("AIRSPEED INDICATED", "knots", SIMCONNECT_DATATYPE.FLOAT64)
                },
                data => UpdateNeedle(data.Airspeed));
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird jetzt zentral einmal im Fenster (MainWindow)
            // angezeigt, nicht mehr pro Anzeige - hier bewusst keine Aktion nötig.
        }

        private void UpdateNeedle(double speedKnots)
        {
            double clamped = Math.Max(0, Math.Min(DialMaxSpeed, speedKnots));
            double angle = AngleForSpeed(clamped);

            if (_needleRotation != null)
                _needleRotation.Angle = angle;
        }

        // ---------------------------------------------------------------
        // Zifferblatt zeichnen (einmalig beim Start)
        // ---------------------------------------------------------------
        private void DrawGaugeFace()
        {
            // Metallischer Außenring (mehrere Kreise mit Verlauf für Tiefenwirkung)
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

            // Schwarzes Zifferblatt
            var face = new Ellipse
            {
                Width = 264,
                Height = 264,
                Fill = Brushes.Black
            };
            Canvas.SetLeft(face, 18);
            Canvas.SetTop(face, 18);
            GaugeCanvas.Children.Add(face);

            // Hauptbögen: Grün (Normalbereich) und Gelb (Vorsicht), dick und durchgehend.
            // 30% weiter innen als der weiße Bogen (Abstand 10 -> 13).
            DrawArcBand(VS1, VNO, Brushes.LimeGreen, 17.5, 115);
            DrawArcBand(VNO, VNE, new SolidColorBrush(Color.FromRgb(0xF5, 0xD0, 0x20)), 17.5, 115);

            // Weißer Markierungsstrich am Anfang der Skala (bei 40) - VOR dem weißen
            // Akzentbogen gezeichnet, damit er dahinter liegt (beide weiß, daher kein
            // sichtbarer Unterschied, aber sauberere Ebenen-Reihenfolge).
            DrawRadialLine(ScaleStart, Brushes.White, 3, 108, 128);

            // Weißer Akzentbogen (Landeklappenbereich): beginnt zusammen mit der
            // "40"-Markierung (nicht davor), deutlich dick. Deckt jetzt die äußere
            // Hälfte des grünen Bogens ab (Grün: 106,25-123,75 -> Mitte bei 115).
            // Start um einen halben GROSSEN Strich früher, Ende um einen halben
            // KLEINEN Strich später (Striche sind Kreisbögen an einem festen Radius -
            // Umrechnung Strichbreite -> Winkel über den mittleren Radius ~121).
            const double avgTickRadius = 121;
            double majorHalfAngle = (3.5 / 2) / avgTickRadius * (180 / Math.PI);
            double minorHalfAngle = (2.0 / 2) / avgTickRadius * (180 / Math.PI);
            double bandStartAngle = AngleForSpeed(ScaleStart) - majorHalfAngle;
            double bandEndAngle = AngleForSpeed(VFE) + minorHalfAngle;
            GaugeDrawing.DrawArcBand(GaugeCanvas, CenterX, CenterY, bandStartAngle, bandEndAngle, Brushes.White, 17, 123.5);

            // Rote Markierung bei Vne (nie überschreiten) - NACH dem weißen Bogen,
            // damit sie sichtbar bleibt (nicht vom Weiß überdeckt).
            DrawRadialLine(VNE, Brushes.Red, 4, 108, 128);

            // Striche alle 5 kt: alle 20 kt groß+beschriftet, alle 10 kt (dazwischen)
            // jetzt genauso groß wie die 20er (nur unbeschriftet), und die neuen
            // 5kt-Zwischenstriche (45, 55, ...) in der bisherigen "kleinen" Stärke.
            for (double v = ScaleStart; v <= DialMaxSpeed; v += 5)
            {
                int tier;
                if (v % 20 == 0) tier = 0;      // groß, beschriftet
                else if (v % 10 == 0) tier = 1;  // jetzt genauso groß wie Stufe 0, nur ohne Zahl
                else tier = 2;                    // neu: kleiner Zwischenstrich

                DrawTick(v, tier);
                if (tier == 0)
                    DrawTickLabel(v);
            }

            // Beschriftung "AIRSPEED" / "KNOTS" / "TAS" wie auf einem echten Instrument
            GaugeDrawing.AddCenteredText(GaugeCanvas, "AIRSPEED", CenterX, CenterY - 50, 14, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "KNOTS", CenterX, CenterY + 50, 12, Brushes.LightGray, false);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "TAS", CenterX - 20, CenterY + 95, 11, Brushes.LightGray, false);

            // Zeiger: schlanke, spitz zulaufende Nadel mit kurzem Gegengewicht
            var needle = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, CenterY - 112),           // Spitze
                    new Point(CenterX - 4, CenterY - 20),
                    new Point(CenterX - 9, CenterY),
                    new Point(CenterX, CenterY + 22),             // Gegengewicht-Ende
                    new Point(CenterX + 9, CenterY),
                    new Point(CenterX + 4, CenterY - 20)
                }),
                Fill = Brushes.White
            };
            _needleRotation = new RotateTransform(AngleForSpeed(0), CenterX, CenterY);
            needle.RenderTransform = _needleRotation;
            GaugeCanvas.Children.Add(needle);
        }

        private void DrawArcBand(double fromSpeed, double toSpeed, Brush brush, double thickness, double radius)
            => GaugeDrawing.DrawArcBand(GaugeCanvas, CenterX, CenterY,
                AngleForSpeed(fromSpeed), AngleForSpeed(toSpeed), brush, thickness, radius);

        private void DrawRadialLine(double speed, Brush brush, double thickness, double innerRadius, double outerRadius)
            => GaugeDrawing.DrawRadialLine(GaugeCanvas, CenterX, CenterY,
                AngleForSpeed(speed), brush, thickness, innerRadius, outerRadius);

        private void DrawTick(double speed, int tier)
        {
            double angle = AngleForSpeed(speed);
            double outer = OuterRadius - 4;
            // Stufe 0/1 (20kt- und 10kt-Striche): bis zum Anfang (Außenkante) des
            // grünen Bogens (Radius 115, Dicke 17.5 -> Außenkante 123.75).
            // Stufe 2 (neue 5kt-Zwischenstriche): 80% der Dicke des grünen Bogens lang.
            double inner = tier <= 1 ? outer - 28.6 : outer - 19.5; // große Striche 10% länger (28.6 = 26*1.1), kleine unverändert
            double thickness = tier <= 1 ? 3.5 : 2;

            var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, inner);
            var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, outer);

            // Dünner schwarzer Rand zuerst (etwas dicker als der weiße Strich selbst),
            // damit der Strich auch dort sichtbar bleibt, wo er über den weißen
            // Klappen-Bogen läuft (sonst weiß auf weiß, praktisch unsichtbar).
            GaugeCanvas.Children.Add(new Line
            {
                X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                Stroke = Brushes.Black,
                StrokeThickness = thickness + 1.5
            });

            var line = new Line
            {
                X1 = p0.X,
                Y1 = p0.Y,
                X2 = p1.X,
                Y2 = p1.Y,
                Stroke = Brushes.White,
                StrokeThickness = thickness
            };
            GaugeCanvas.Children.Add(line);
        }

        private void DrawTickLabel(double speed)
        {
            double angle = AngleForSpeed(speed);
            var p = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, OuterRadius - 56);
            GaugeDrawing.AddCenteredText(GaugeCanvas, ((int)speed).ToString(), p.X, p.Y, 25, Brushes.White, true);
        }
    }
}
