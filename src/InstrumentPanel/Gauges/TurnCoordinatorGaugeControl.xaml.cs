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
    public partial class TurnCoordinatorGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;

        private const double BallDiameter = 40;      // Durchmesser der Kugel (auch als Referenzgröße fürs Flugzeug-Symbol genutzt)
        private const double WingspanRadius = 104;  // ~2px Luft vor der Innenkante der Markierungen (bei 106)
        private const double MarkRadius = 122;       // Radius der weißen Referenzmarkierungen, nah am Rand
        private const double StdRateBankDeg = 30;    // Symbol-Neigung bei "standard rate turn" (3°/s) - 50% mehr als zuvor (20)
        private const double MaxBankDeg = 55;         // maximale Symbol-Neigung (Anschlag) - +5° gegenüber MSFS-Vergleich
        private const double StdRateTurnPerSec = 3;   // "standard rate": 3°/s Kursänderung = 2-Minuten-Kurve

        private const double BallMaxOffset = 98;      // maximaler seitlicher Weg der Kugel in px - Kugelmitte
                                                        // erreicht damit den sichtbaren Rand, sodass bei Vollausschlag
                                                        // die Hälfte der Kugel aus dem Sichtbereich verschwindet

        // Klapp-Anzeige-Geometrie (als Klassenkonstanten, da UpdateDisplay() sie
        // ebenfalls braucht, um die Schiebebewegung zu berechnen).
        private const double FlipMarkLength = 16;               // Länge eines Referenzstrichs
        private const double FlipMarkThickness = 8;              // Strichstärke der Referenzstriche
        private const double FlipW = FlipMarkLength;
        private const double FlipPanelH = FlipMarkThickness * 1.5; // Höhe je Farbband (rot/grau/schwarz)

        [StructLayout(LayoutKind.Sequential)]
        private struct TcStruct
        {
            public double TurnRate;         // Grad/Sekunde (direkt aus SimConnect)
            public double Ball;             // -127..127 (SimConnect-Einheit "position" für dieses SimVar)
            public double SuctionPressure;  // Vakuum-Systemdruck (inHg) - bestimmt, ob der Kreisel läuft
        }

        private RotateTransform _planeRotation;
        private TranslateTransform _ballTranslate;
        private TranslateTransform _offStripTranslate;
        private double _smoothedRate;

        public TurnCoordinatorGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            service.Register<TcStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("TURN INDICATOR RATE", "degrees per second", SIMCONNECT_DATATYPE.FLOAT64),
                    ("TURN COORDINATOR BALL", "position", SIMCONNECT_DATATYPE.FLOAT64),
                    ("SUCTION PRESSURE", "Inches of Mercury", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird jetzt zentral einmal im Fenster (MainWindow)
            // angezeigt, nicht mehr pro Anzeige - hier bewusst keine Aktion nötig.
        }

        private void OnData(TcStruct data)
        {
            // Leichte Glättung (Tiefpass), damit die Anzeige nicht flattert.
            _smoothedRate += (data.TurnRate - _smoothedRate) * 0.3;

            // Konfigurierbar über settings.json ("turnCoordinator": { "ballDivisor": ... }).
            // Kleinerer Wert = empfindlicher. Bei Bedarf dort anpassen, ohne den Code
            // ändern zu müssen.
            double ballScale = AppSettings.TurnCoordinatorBallDivisor;
            double normalizedBall = data.Ball / ballScale;
            DebugLog.Write("[TC] OnData: raw Ball=" + data.Ball + ", ballScale=" + ballScale +
                ", normalized=" + normalizedBall + ", BallMaxOffset=" + BallMaxOffset +
                ", SuctionPressure=" + data.SuctionPressure);
            UpdateDisplay(_smoothedRate, normalizedBall, data.SuctionPressure);
        }

        private const double OffRestBankDeg = 60; // Ruhelage bei ausgelaufenem Kreisel (unabhängig vom normalen Anschlag)

        private double _displayFactor = 1.0; // 1=Kreisel läuft, 0=komplett ausgelaufen
        private DateTime? _lastUpdateTime;
        private const double TransitionSeconds = 0.7; // feste Dauer für den kompletten Übergang, unabhängig vom Druckverlauf

        private void UpdateDisplay(double turnRateDegPerSec, double ball, double suctionPressure)
        {
            double threshold = AppSettings.TurnCoordinatorVacuumThreshold;
            bool switchOn = suctionPressure >= threshold;

            // Rein binäres Ziel (0 oder 1) je nach Schwellwert-Überschreitung - der
            // Schaltzeitpunkt selbst bleibt an den echten Druck gekoppelt, aber die
            // sichtbare Bewegung dorthin läuft mit fester Dauer (TransitionSeconds),
            // komplett unabhängig davon, wie lange der Druck real zum Auslaufen braucht.
            double targetFactor = switchOn ? 1.0 : 0.0;
            var now = DateTime.UtcNow;
            double dt = _lastUpdateTime.HasValue ? (now - _lastUpdateTime.Value).TotalSeconds : 0;
            _lastUpdateTime = now;
            double step = dt / TransitionSeconds;
            if (targetFactor > _displayFactor)
                _displayFactor = Math.Min(targetFactor, _displayFactor + step);
            else
                _displayFactor = Math.Max(targetFactor, _displayFactor - step);
            double gyroFactor = _displayFactor;

            double normalBankDeg = turnRateDegPerSec / StdRateTurnPerSec * StdRateBankDeg;
            normalBankDeg = Math.Max(-MaxBankDeg, Math.Min(MaxBankDeg, normalBankDeg));

            // Ruhelage bei komplett ausgelaufenem Kreisel: voller Ausschlag nach links, 15° mehr
            // als der normale Anschlag - Übergang dahin läuft weich mit gyroFactor statt schlagartig.
            double bankDeg = gyroFactor * normalBankDeg + (1 - gyroFactor) * (-OffRestBankDeg);

            if (_planeRotation != null)
                _planeRotation.Angle = bankDeg;

            if (_ballTranslate != null)
            {
                double clampedBall = Math.Max(-1, Math.Min(1, ball));
                _ballTranslate.X = clampedBall * BallMaxOffset;
            }

            if (_offStripTranslate != null)
            {
                // Dreiband-Streifen (rot/grau/schwarz) hinter einem Sichtfenster, das nur
                // zwei Bänder zeigt - schiebt sich wie eine echte mechanische Klappe:
                // OFF (gyroFactor=0): Streifen unverschoben -> rot+grau sichtbar (oben rot mit "OFF").
                // ON (gyroFactor=1): Streifen um eine Bandhöhe nach oben verschoben -> grau+schwarz sichtbar.
                _offStripTranslate.Y = -FlipPanelH * gyroFactor;
            }
        }

        // ---------------------------------------------------------------
        // Zifferblatt zeichnen (einmalig beim Start)
        // ---------------------------------------------------------------
        private void DrawGaugeFace()
        {
            // Metallischer Außenring, gleicher Stil wie beim Fahrtmesser
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

            var face = new Ellipse
            {
                Width = 264,
                Height = 264,
                Fill = Brushes.Black
            };
            Canvas.SetLeft(face, 18);
            Canvas.SetTop(face, 18);
            GaugeCanvas.Children.Add(face);

            // Weiße Referenzmarkierungen für die Standardkurve (±20°), nah am Rand
            // Obere Markierung: waagrecht, entspricht "Flügel level"
            DrawFixedBar(-1, Brushes.White);
            DrawFixedBar(1, Brushes.White);

            // Untere Markierung: Standardkurve (3°/s = 20° Symbol-Neigung),
            // rechts = Referenz für Rechtskurve, links = Referenz für Linkskurve
            DrawDoghouseMark(StdRateBankDeg, Brushes.White);

            GaugeDrawing.AddCenteredText(GaugeCanvas, "L", CenterX - 118, CenterY + 26, 15, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "R", CenterX + 118, CenterY + 26, 15, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "TURN COORDINATOR", CenterX, CenterY - 78, 11, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "2 MIN.", CenterX, CenterY + 36, 12, Brushes.LightGray, false);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "NO PITCH INFORMATION", CenterX, CenterY + 50, 8, Brushes.LightGray, false);

            // Klapp-Anzeige: ein Streifen mit drei Farbbändern (rot+OFF-Text / grau / schwarz)
            // hinter einem Sichtfenster, das nur zwei Bänder zeigt. Der Streifen schiebt sich
            // wie eine echte mechanische Klappe nach oben, wenn der Kreisel hochläuft:
            // OFF: rot (oben) + grau (unten) sichtbar. ON: grau (oben) + schwarz (unten) sichtbar.
            double flipCenterX = CenterX + (MarkRadius - 8) - BallDiameter; // ca. eine Kugelbreite links der rechten Markierung
            double bottomEdge = CenterY - FlipMarkThickness / 2; // Oberkante der rechten waagrechten Markierung -
                                                                   // Unterkante der GESAMTEN Klapp-Anzeige sitzt hier
            double viewportTop = bottomEdge - FlipPanelH * 2;
            const double pad = 2; // Innenabstand zum Rahmen

            var stripRed = new Rectangle { Width = FlipW, Height = FlipPanelH, Fill = Brushes.Red };
            var stripGray = new Rectangle { Width = FlipW, Height = FlipPanelH, Fill = Brushes.Gray };
            var stripBlack = new Rectangle { Width = FlipW, Height = FlipPanelH, Fill = Brushes.Black };
            Canvas.SetTop(stripRed, 0);
            Canvas.SetTop(stripGray, FlipPanelH);
            Canvas.SetTop(stripBlack, FlipPanelH * 2);

            var offText = new TextBlock
            {
                Text = "OFF",
                FontSize = 8,   // so groß wie "NO PITCH INFORMATION"
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black
            };
            Canvas.SetTop(offText, 1);

            // Streifen: 3 Bänder hoch (36px), bekommt die Schiebe-Transformation
            var strip = new System.Windows.Controls.Canvas { Width = FlipW, Height = FlipPanelH * 3 };
            strip.Children.Add(stripRed);
            strip.Children.Add(stripGray);
            strip.Children.Add(stripBlack);
            strip.Children.Add(offText);
            _offStripTranslate = new TranslateTransform(0, 0);
            strip.RenderTransform = _offStripTranslate;

            // Sichtfenster: zeigt nur 2 Bänder (der Rest des Streifens wird abgeschnitten)
            var viewport = new System.Windows.Controls.Canvas
            {
                Width = FlipW,
                Height = FlipPanelH * 2,
                Clip = new RectangleGeometry(new Rect(0, 0, FlipW, FlipPanelH * 2))
            };
            Canvas.SetLeft(strip, 0);
            Canvas.SetTop(strip, 0);
            viewport.Children.Add(strip);
            Canvas.SetLeft(viewport, pad);
            Canvas.SetTop(viewport, pad);

            // Rahmen mit leicht runden Ecken um den gesamten Klappbereich, damit sich
            // das Schwarz der ON-Anzeige vom schwarzen Zifferblatt-Hintergrund abhebt.
            var flipBorder = new Rectangle
            {
                Width = FlipW + pad * 2,
                Height = FlipPanelH * 2 + pad * 2,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Brushes.Transparent,
                Stroke = Brushes.Gainsboro,
                StrokeThickness = 1
            };
            Canvas.SetLeft(flipBorder, 0);
            Canvas.SetTop(flipBorder, 0);

            var offFlag = new System.Windows.Controls.Canvas { Width = FlipW + pad * 2, Height = FlipPanelH * 2 + pad * 2 };
            offFlag.Children.Add(flipBorder);
            offFlag.Children.Add(viewport);
            Canvas.SetLeft(offFlag, flipCenterX - FlipW / 2 - pad);
            Canvas.SetTop(offFlag, viewportTop - pad);
            GaugeCanvas.Children.Add(offFlag);

            // Inklinometer (Kugel-Röhre) VOR dem Flugzeug-Symbol zeichnen, damit das
            // Flugzeug bei starker Neigung nicht hinter der (undurchsichtigen) Röhre
            // verschwindet, sondern immer im Vordergrund bleibt.
            DrawBallTube();

            // Flugzeug-Symbol: Rumpf (weißer Kreis) + Leitwerk fix, Tragfläche kippt
            // um die Mitte - repräsentiert die Wenderate (nicht den echten Roll-Winkel).
            // Wichtig: ALLE Teile als Polygon/Line mit absoluten Koordinaten (nicht per
            // Canvas.Left/Top positioniert) - sonst dreht sich das Element um einen
            // Punkt weit außerhalb seiner selbst, da RotateTransform.CenterX/Y bei
            // Canvas.Left/Top-positionierten Elementen relativ zur eigenen Bounding-Box
            // gilt, nicht zur Canvas. Das war der Grund für das "wandernde" Leitwerk.

            const double fuselageRadius = BallDiameter / 2;      // Rumpf so groß wie die Kugel
            const double wingRootThickness = BallDiameter / 5 * 2;  // am Rumpf: doppelt so dick wie zuvor (1/5 der Rumpfgröße)
            // Gleiche Verjüngungsrate wie vorher (Spannweite 100 -> Spitze = 1/4 der Rumpf-Dicke)
            // beibehalten, nur auf die jetzt längere Spannweite angewendet - dadurch wird die
            // Spitze automatisch dünner/spitzer, statt bei gleichem Verhältnis nur weiter rauszurücken.
            const double oldSpan = 100;
            const double taperPerUnit = (wingRootThickness - wingRootThickness / 4) / oldSpan;
            const double wingTipThickness = wingRootThickness - taperPerUnit * WingspanRadius;
            const double tailStrokeThickness = 10.0 / 3.0;        // 1/3 der bisherigen 10px-Rumpfbreite
            const double elevatorWidth = BallDiameter * 1.75;     // Höhenruder: 175% der Kugelgröße
            const double rudderTopY = CenterY - fuselageRadius - BallDiameter / 2; // 50% Kugelgröße über der Kreis-Oberkante

            // Tragfläche: gerade Oberkante, spitz nach außen zulaufend (dünner an den Spitzen)
            var wings = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX - WingspanRadius, CenterY),
                    new Point(CenterX, CenterY),
                    new Point(CenterX + WingspanRadius, CenterY),
                    new Point(CenterX + WingspanRadius, CenterY + wingTipThickness),
                    new Point(CenterX, CenterY + wingRootThickness),
                    new Point(CenterX - WingspanRadius, CenterY + wingTipThickness)
                }),
                Fill = Brushes.White
            };

            // Höhenruder: bündig mit der Rumpf-Oberkante
            var elevator = new Line
            {
                X1 = CenterX - elevatorWidth / 2,
                Y1 = CenterY - fuselageRadius,
                X2 = CenterX + elevatorWidth / 2,
                Y2 = CenterY - fuselageRadius,
                Stroke = Brushes.White,
                StrokeThickness = tailStrokeThickness
            };

            // Seitenruder: beginnt an der Rumpf-Oberkante, ragt 50% der Kugelgröße darüber hinaus
            var rudder = new Line
            {
                X1 = CenterX,
                Y1 = CenterY - fuselageRadius,
                X2 = CenterX,
                Y2 = rudderTopY,
                Stroke = Brushes.White,
                StrokeThickness = tailStrokeThickness
            };

            // Rumpf: weißer Kreis, Mittelpunkt = Drehpunkt
            var fuselage = new Ellipse
            {
                Width = fuselageRadius * 2,
                Height = fuselageRadius * 2,
                Fill = Brushes.White
            };
            Canvas.SetLeft(fuselage, CenterX - fuselageRadius);
            Canvas.SetTop(fuselage, CenterY - fuselageRadius);

            _planeRotation = new RotateTransform(0, CenterX, CenterY);
            wings.RenderTransform = _planeRotation;
            elevator.RenderTransform = _planeRotation;
            rudder.RenderTransform = _planeRotation;
            // Der Rumpf-Kreis ist per Canvas.Left/Top positioniert - für den dreht sich
            // ohnehin nichts sichtbar um den eigenen Mittelpunkt, aber zur Konsistenz
            // (und falls der Rumpf später off-center verschoben wird) trotzdem eine
            // eigene, korrekt zentrierte RotateTransform-Instanz statt der von wings/tail.
            fuselage.RenderTransform = new RotateTransform(0, fuselageRadius, fuselageRadius);
            GaugeCanvas.Children.Add(wings);
            GaugeCanvas.Children.Add(elevator);
            GaugeCanvas.Children.Add(rudder);
            GaugeCanvas.Children.Add(fuselage);
        }

        private void DrawFixedBar(int side, Brush brush)
        {
            // Waagrechte Referenzmarkierung ("Flügel level") am Rand, dreht sich NICHT mit.
            // Um die halbe Strichstärke (4px) nach unten verschoben, damit die
            // OBERKANTE exakt auf 3/9-Uhr-Position liegt (bündig mit der Tragfläche,
            // statt die Markierung mittig darüber zu zentrieren - das erzeugte eine
            // sichtbare Stufe).
            const double thickness = 8;
            double angle = side > 0 ? 0 : 180;
            var p0 = GaugeDrawing.PointOnHorizontalCircle(CenterX, CenterY, angle, MarkRadius - 16);
            var p1 = GaugeDrawing.PointOnHorizontalCircle(CenterX, CenterY, angle, MarkRadius);
            GaugeCanvas.Children.Add(new Line
            {
                X1 = p0.X, Y1 = p0.Y + thickness / 2,
                X2 = p1.X, Y2 = p1.Y + thickness / 2,
                Stroke = brush,
                StrokeThickness = thickness
            });
        }

        private void DrawDoghouseMark(double bankDeg, Brush brush)
        {
            // Referenzmarkierung für die Standardkurve: zeigt, wo die jeweilige
            // Flügelspitze bei einer Standardkurve (3°/s) in DIESE Richtung steht.
            // Rechte Seite = Referenz für Rechtskurve (Flügelspitze bei +bankDeg),
            // linke Seite = Referenz für Linkskurve (Flügelspitze bei -bankDeg,
            // was rechnerisch der Winkel 180-bankDeg ist). Beide liegen dadurch
            // symmetrisch UNTERHALB der waagrechten Mittellinie.
            double rightTipAngle = bankDeg;
            var p0 = GaugeDrawing.PointOnHorizontalCircle(CenterX, CenterY, rightTipAngle, MarkRadius - 16);
            var p1 = GaugeDrawing.PointOnHorizontalCircle(CenterX, CenterY, rightTipAngle, MarkRadius);
            GaugeCanvas.Children.Add(new Line { X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y, Stroke = brush, StrokeThickness = 8 });

            double leftTipAngle = 180 - bankDeg;
            var p2 = GaugeDrawing.PointOnHorizontalCircle(CenterX, CenterY, leftTipAngle, MarkRadius - 16);
            var p3 = GaugeDrawing.PointOnHorizontalCircle(CenterX, CenterY, leftTipAngle, MarkRadius);
            GaugeCanvas.Children.Add(new Line { X1 = p2.X, Y1 = p2.Y, X2 = p3.X, Y2 = p3.Y, Stroke = brush, StrokeThickness = 8 });
        }

        private void DrawBallTube()
        {
            const double tubeY = CenterY + 88;
            const double tubeHalfWidth = 130;  // wieder breit, wie zuvor - Ecken dürfen vom Clip abgeschnitten werden
            const double tubeHalfHeight = 20;

            // Eigener Sub-Canvas mit rundem Clip auf den Radius des schwarzen Zifferblatts
            // (132) - dadurch kann die breite Röhre nicht über den Rahmen/Bezel hinausragen,
            // sie wirkt so, als würde der Rahmen sie an den Ecken überlagern.
            var ballArea = new System.Windows.Controls.Canvas
            {
                Clip = new EllipseGeometry(new Point(CenterX, CenterY), 132, 132)
            };

            var tube = new Rectangle
            {
                Width = tubeHalfWidth * 2 + 20,
                Height = tubeHalfHeight * 2,
                RadiusX = tubeHalfHeight,
                RadiusY = tubeHalfHeight,
                Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xDC)), // Beige
                Stroke = Brushes.Gainsboro,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(tube, CenterX - tubeHalfWidth - 10);
            Canvas.SetTop(tube, tubeY - tubeHalfHeight);
            ballArea.Children.Add(tube);

            // Mittenmarkierungen: exakt so weit auseinander wie die Kugel breit ist,
            // damit klar ist, wo die Kugel bei koordiniertem Flug stehen soll.
            foreach (var x in new[] { CenterX - BallDiameter / 2, CenterX + BallDiameter / 2 })
            {
                ballArea.Children.Add(new Line
                {
                    X1 = x, Y1 = tubeY - tubeHalfHeight, X2 = x, Y2 = tubeY + tubeHalfHeight,
                    Stroke = Brushes.Black, StrokeThickness = 2
                });
            }

            var ball = new Ellipse
            {
                Width = BallDiameter,
                Height = BallDiameter,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Colors.Black, 0.0),
                        new GradientStop(Color.FromRgb(0x33, 0x33, 0x33), 1.0)
                    }),
                Stroke = Brushes.Gray,
                StrokeThickness = 1
            };
            Canvas.SetLeft(ball, CenterX - BallDiameter / 2);
            Canvas.SetTop(ball, tubeY - BallDiameter / 2);
            _ballTranslate = new TranslateTransform(0, 0);
            ball.RenderTransform = _ballTranslate;
            ballArea.Children.Add(ball);

            GaugeCanvas.Children.Add(ballArea);
        }
    }
}
