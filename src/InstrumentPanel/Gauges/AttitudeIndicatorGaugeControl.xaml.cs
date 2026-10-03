// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.FlightSimulator.SimConnect;

namespace InstrumentPanel
{
    public partial class AttitudeIndicatorGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132;   // Zifferblatt-Rand (äußerer Ring)
        private const double InnerDiscRadius = OuterRadius * 50.0 / 60.0; // Ringbreite = 10/50 des Innenkreis-Radius

        private const double PixelsPerDegreePitch = 1.875; // kalibriert: Ellipsen-Kante (75px) = 40° (empirisch gemessen)

        private const double DragPixelsPerStep = 5;
        private const double KnobDegreesPerStep = 12; // rein optische Rückmeldung

        // Farbe wie der Bezel-Rahmen, für das schwarze Trapez / den unteren Segmentbereich.
        private static readonly Color BezelBlack = Color.FromRgb(0x1a, 0x1a, 0x1c);

        [StructLayout(LayoutKind.Sequential)]
        private struct AttitudeStruct
        {
            public double Pitch;        // Grad (roh von MSFS, Vorzeichen invertiert - siehe OnData)
            public double Bank;         // Grad
            public double BarsPosition; // Percent Over 100 - Stellung des Nick-Trimm-Knopfs
            public double SuctionPressure; // Inches of Mercury - für die rote GYRO-Warnklappe
        }

        private SimConnectService _service;
        private TranslateTransform _discTranslate;   // innere Scheibe: Verschiebung (Nick)
        private RotateTransform _discRotate;         // innere Scheibe: jetzt auch Rotation (Rollen)
        private RotateTransform _backgroundRotate;   // Hintergrund: auch Rotation (Rollen)
        private Canvas _gyroFlagGroup;                // Rote GYRO-Warnklappe bei Unterdruck
        private RotateTransform _gyroFlagRotate;      // Schwenk-Rotation der Klappe um den Drehpunkt außerhalb der Anzeige
        private double _displayedPitch;
        private double _displayedBank = double.NaN; // NaN = noch nicht initialisiert
        private bool? _previousSwitchOn; // zur Erkennung des Wiederanlaufens (null = noch unbekannt)
        private bool _isRecoveringToRealValue; // true = gerade erst wieder an, noch am sanften Angleichen
        private double _displayedGyroFlagAngle = double.NaN; // NaN = noch nicht initialisiert
        private DateTime? _lastUpdateTime;
        private RotateTransform _ringRotate;         // äußerer Ring: nur Rotation (Rollen)
        private TranslateTransform _aircraftGroupTranslate; // Flugzeug-Symbol + Trapez (Nick-Trimm)
        private RotateTransform _knobRotation;

        private bool _dragging;
        private double _dragStartX;
        private double _dragAccumulated;

        public AttitudeIndicatorGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            _service = service;
            service.Register<AttitudeStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("PLANE PITCH DEGREES", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
                    ("PLANE BANK DEGREES", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
                    ("ATTITUDE BARS POSITION", "percent over 100", SIMCONNECT_DATATYPE.FLOAT64),
                    ("SUCTION PRESSURE", "Inches of Mercury", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird zentral im Fenster (MainWindow) angezeigt.
        }

        private void OnData(AttitudeStruct data)
        {
            // MSFS liefert PLANE PITCH DEGREES mit umgekehrtem Vorzeichen (negativ =
            // Nase hoch) - hier auf "positiv = Nase hoch" gedreht.
            double realPitch = -data.Pitch;
            double realBank = data.Bank;

            bool switchOn = data.SuctionPressure >= AppSettings.TurnCoordinatorVacuumThreshold;

            // Verstrichene Zeit seit dem letzten Update - für zeitbasierte (statt
            // update-zahl-basierte) Animationen, unabhängig vom Refresh-Intervall.
            var now = DateTime.UtcNow;
            double deltaTimeSec = _lastUpdateTime.HasValue ? (now - _lastUpdateTime.Value).TotalSeconds : 0;
            deltaTimeSec = Math.Max(0, Math.Min(1, deltaTimeSec)); // gegen große Ausreißer (z.B. nach Pause) absichern
            _lastUpdateTime = now;

            if (double.IsNaN(_displayedBank))
            {
                _displayedPitch = realPitch;
                _displayedBank = realBank;
            }

            // Bei Unterdruck-Ausfall (Motor aus): Anzeige "kippt" langsam (über ca.
            // 10 Sekunden) zu einer festen Fehlstellung (20° Rollen rechts, 20°
            // Pitch) statt die reale Fluglage zu zeigen - wie beim echten Kreisel,
            // der beim Ausfall allmählich zur Seite wegkippt statt sofort zu springen.
            const double failedTargetPitch = 20;
            const double failedTargetBank = 25;
            const double settleTimeConstantSec = 10.0 / 3.0; // ~10s bis größtenteils angeglichen

            double targetPitch = switchOn ? realPitch : failedTargetPitch;
            double targetBank = switchOn ? realBank : failedTargetBank;

            // Beim Wiederanlaufen (Motor an, Unterdruck baut sich auf) einmalig
            // sanft zum echten Wert hinbewegen (wie der echte Kreisel, der wieder
            // hochläuft), danach direkt reagieren - nicht dauerhaft träge.
            if (!switchOn)
            {
                _isRecoveringToRealValue = false; // beim nächsten Wiederanlaufen neu starten
            }
            else if (_previousSwitchOn.HasValue && !_previousSwitchOn.Value)
            {
                _isRecoveringToRealValue = true; // gerade erst wieder angegangen
            }

            if (switchOn && !_isRecoveringToRealValue)
            {
                // Normalbetrieb (bereits eingeschwungen): sofort den echten Werten
                // folgen, keine Trägheit.
                _displayedPitch = targetPitch;
                _displayedBank = targetBank;
            }
            else
            {
                // Entweder Unterdruck-Ausfall ODER gerade erst wieder angegangen:
                // sanft angleichen (10s-Zeitkonstante), wie beim echten Kreisel.
                double alpha = deltaTimeSec > 0 ? 1 - Math.Exp(-deltaTimeSec / settleTimeConstantSec) : 0;
                _displayedPitch += (targetPitch - _displayedPitch) * alpha;
                _displayedBank += (targetBank - _displayedBank) * alpha;

                // Sobald nah genug am echten Wert angekommen: ab jetzt direkt reagieren.
                const double settledThresholdDeg = 0.3;
                if (switchOn && Math.Abs(targetPitch - _displayedPitch) < settledThresholdDeg
                    && Math.Abs(targetBank - _displayedBank) < settledThresholdDeg)
                {
                    _isRecoveringToRealValue = false;
                }
            }
            _previousSwitchOn = switchOn;

            double pitch = _displayedPitch;
            double bank = _displayedBank;

            if (_discTranslate != null)
            {
                // Anschlag oben/unten identisch bei 78°.
                double clampedPitch = Math.Max(-78, Math.Min(78, pitch)); // Endanschlag
                _discTranslate.Y = clampedPitch * PixelsPerDegreePitch;
                DebugLog.Write("[ATTITUDE] pitch=" + pitch + ", discTranslate.Y=" + _discTranslate.Y);
            }
            if (_ringRotate != null)
            {
                _ringRotate.Angle = bank;
                DebugLog.Write("[ATTITUDE] bank=" + bank + ", ringRotate.Angle=" + _ringRotate.Angle);
            }
            if (_discRotate != null)
                _discRotate.Angle = bank;
            if (_backgroundRotate != null)
                _backgroundRotate.Angle = bank;

            // Rote GYRO-Warnklappe: sichtbar bei zu niedrigem Unterdruck (Motor aus,
            // 20° von der Senkrechten), sonst um 70° weiter weggedreht (versteckt).
            // Schwenkt in 1 Sekunde linear zwischen beiden Stellungen.
            if (_gyroFlagRotate != null)
            {
                const double visibleAngle = -20;
                const double hiddenAngle = 70;
                double targetFlagAngle = switchOn ? hiddenAngle : visibleAngle;
                if (double.IsNaN(_displayedGyroFlagAngle))
                    _displayedGyroFlagAngle = targetFlagAngle;

                const double swingDurationSec = 1.0;
                double maxStep = Math.Abs(hiddenAngle - visibleAngle) / swingDurationSec * deltaTimeSec;
                double diff = targetFlagAngle - _displayedGyroFlagAngle;
                if (Math.Abs(diff) <= maxStep)
                    _displayedGyroFlagAngle = targetFlagAngle;
                else
                    _displayedGyroFlagAngle += Math.Sign(diff) * maxStep;

                _gyroFlagRotate.Angle = _displayedGyroFlagAngle;
            }

            // Skalierung geschätzt (Einheit "Percent Over 100" nicht sicher spezifiziert) -
            // ggf. nach Test anpassen.
            if (_aircraftGroupTranslate != null)
            {
                // Realer Wertebereich laut Log: -1 bis +1 (nicht ±100 wie angenommen).
                // +1 -> +10°-Strich (Y=-40), -1 -> -5°-Strich (Y=+20).
                // -1 -> -7°-Strich (Y=+28, statt vorher -5°/+20).
                double barsFactor = data.BarsPosition >= 0 ? 40 : 28;
                _aircraftGroupTranslate.Y = -data.BarsPosition * barsFactor;
                DebugLog.Write("[ATTITUDE] barsPosition=" + data.BarsPosition + ", aircraftGroupTranslate.Y=" + _aircraftGroupTranslate.Y);
            }
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
                        new GradientStop(BezelBlack, 1.0)
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

            // -----------------------------------------------------------
            // HINTERGRUND: kompletter Vollkreis, 10% dunkler als die Ellipsen-
            // Farben - liegt hinter allem, scheint im Ringbereich durch (der Ring
            // selbst hat keine eigene Füllfarbe mehr).
            // -----------------------------------------------------------
            var backgroundClipHost = new Canvas
            {
                Clip = new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, OuterRadius)
            };
            var bgSky = new Rectangle { Width = 900, Height = 900, Fill = new SolidColorBrush(Color.FromRgb(0x5e, 0x8e, 0x95)) };
            Canvas.SetLeft(bgSky, CenterX - 450);
            Canvas.SetTop(bgSky, CenterY - 900);
            backgroundClipHost.Children.Add(bgSky);
            var bgGround = new Rectangle { Width = 900, Height = 900, Fill = new SolidColorBrush(Color.FromRgb(0x84, 0x62, 0x42)) };
            Canvas.SetLeft(bgGround, CenterX - 450);
            Canvas.SetTop(bgGround, CenterY);
            backgroundClipHost.Children.Add(bgGround);
            _backgroundRotate = new RotateTransform(0, CenterX, CenterY);
            backgroundClipHost.RenderTransform = _backgroundRotate;
            GaugeCanvas.Children.Add(backgroundClipHost);

            // -----------------------------------------------------------
            // ÄUSSERER RING: zeigt direkt die Querneigung (Rollwinkel) - nur
            // Rotation, KEINE Verschiebung. Volle Kreisfläche gezeichnet (wird
            // später von der kleineren inneren Scheibe in der Mitte überdeckt,
            // wodurch der sichtbare "Ring" entsteht).
            // -----------------------------------------------------------
            var ringClipHost = new Canvas
            {
                // Echte Ring-Form (Loch in der Mitte, passend zur Ellipsen-Form) -
                // Ring hat wieder eine EIGENE Füllfarbe (95%), damit er sich sichtbar
                // von der Ellipse (100%) abhebt und deren Außenkanten verdeckt.
                Clip = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, OuterRadius),
                    new EllipseGeometry(new Point(CenterX, CenterY), InnerDiscRadius, InnerDiscRadius)) // rundes Loch, genau so groß, dass die großen Striche an der Kante anstoßen
            };
            var ringContent = new Canvas();

            var ringSky = new Rectangle { Width = 900, Height = 900, Fill = new SolidColorBrush(Color.FromRgb(0x7c, 0xbc, 0xc6)) };
            Canvas.SetLeft(ringSky, CenterX - 450);
            Canvas.SetTop(ringSky, CenterY - 900);
            ringContent.Children.Add(ringSky);

            var ringGround = new Rectangle { Width = 900, Height = 900, Fill = new SolidColorBrush(Color.FromRgb(0xaf, 0x81, 0x56)) };
            Canvas.SetLeft(ringGround, CenterX - 450);
            Canvas.SetTop(ringGround, CenterY);
            ringContent.Children.Add(ringGround);

            // Rollwinkel-Skala (dreht mit dem Ring mit): 10°/20° klein, 30°/60° dick,
            // bei 90° (waagerecht, Sehne des Rings) eine weiße horizontale Linie.
            foreach (int deg in new[] { -60, -30, -20, -10, 10, 20, 30, 60 })
            {
                bool major = Math.Abs(deg) == 30 || Math.Abs(deg) == 60;
                double outer = OuterRadius - 4;
                double inner = major ? InnerDiscRadius : outer - 12; // große Striche bis exakt zur Scheiben-Kante, kleine unverändert
                var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, inner);
                var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, outer);
                ringContent.Children.Add(new Line
                {
                    X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = major ? 4.5 : 3 // 30°/60° (major) 50% dicker
                });
            }

            foreach (int deg in new[] { 90, -90 })
            {
                double outer = OuterRadius;
                double inner = InnerDiscRadius;
                var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, inner);
                var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, outer);
                ringContent.Children.Add(new Line
                {
                    X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = 4.5 // 50% dicker (0°-Referenz)
                });
            }

            // Vierecktiges weißes Element bei 0° (Wings-Level-Referenz) statt Dreieck:
            // am inneren Rand (Kreis/Scheibe) halb so breit wie am äußeren Rand (Ring).
            {
                double innerRadius = InnerDiscRadius; // innerer Rand (zur Scheibe)
                double outerRadius = OuterRadius;      // äußerer Rand (Rahmen)
                const double halfWidthDegOuter = 3 * 0.7;
                double outerLinearHalfWidth = outerRadius * Math.Sin(halfWidthDegOuter * Math.PI / 180.0);
                double innerLinearHalfWidth = outerLinearHalfWidth / 2.0; // halb so breit
                double halfWidthDegInner = Math.Asin(innerLinearHalfWidth / innerRadius) * 180.0 / Math.PI;

                var innerLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, -halfWidthDegInner, innerRadius);
                var innerRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, halfWidthDegInner, innerRadius);
                var outerLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, -halfWidthDegOuter, outerRadius);
                var outerRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, halfWidthDegOuter, outerRadius);
                ringContent.Children.Add(new Polygon
                {
                    Points = new PointCollection(new[] { innerLeft, innerRight, outerRight, outerLeft }),
                    Fill = Brushes.White
                });
            }

            _ringRotate = new RotateTransform(0, CenterX, CenterY);
            ringContent.RenderTransform = _ringRotate;
            ringClipHost.Children.Add(ringContent);
            // Wird weiter unten hinzugefügt (NACH der Scheibe), damit der Ring vorne liegt.

            // -----------------------------------------------------------
            // INNERE SCHEIBE: verschiebt sich mit dem Nick-Winkel - nur
            // Verschiebung, KEINE Rotation. Kleinerer Radius, liegt über dem
            // äußeren Ring und lässt dessen Rand als "Ring" sichtbar.
            // -----------------------------------------------------------
            var discClipHost = new Canvas
            {
                // Ellipse-Form wie zuvor - dieser Host bewegt/dreht sich jetzt
                // (Clip UND Inhalt zusammen).
                Clip = new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, 75)
            };
            // Fester äußerer Clip (unbeweglich, einfacher Kreis bei OuterRadius) -
            // verhindert, dass die (jetzt bewegliche) Ellipse bei extremen Winkeln
            // über den Anzeigenrand hinaus sichtbar wird.
            var discOuterClipHost = new Canvas
            {
                Clip = new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, OuterRadius)
            };
            var discContent = new Canvas();

            var discSky = new Rectangle { Width = 700, Height = 700, Fill = new SolidColorBrush(Color.FromRgb(0x8a, 0xd0, 0xdb)) };
            Canvas.SetLeft(discSky, CenterX - 350);
            Canvas.SetTop(discSky, CenterY - 700);
            discContent.Children.Add(discSky);

            var discGround = new Rectangle { Width = 700, Height = 700, Fill = new SolidColorBrush(Color.FromRgb(0xc2, 0x8f, 0x60)) };
            Canvas.SetLeft(discGround, CenterX - 350);
            Canvas.SetTop(discGround, CenterY);
            discContent.Children.Add(discGround);

            discContent.Children.Add(new Line
            {
                X1 = CenterX - 350, Y1 = CenterY, X2 = CenterX + 350, Y2 = CenterY,
                Stroke = Brushes.White,
                StrokeThickness = 3
            });

            // Nick-Referenzstriche: nach oben nur 10°/20° (schwarz, aktuelle Breite),
            // nach unten 10°/20°/30° (weiß, werden nach unten hin breiter).
            const double groundGap = 2 * 3; // Lücke = 2x Strichdicke, für Himmel und Boden gleich

            foreach (int deg in new[] { 10, 20 })
            {
                double yOffset = deg * PixelsPerDegreePitch;
                double halfWidth = yOffset - groundGap; // gleiche Formel wie unten
                double yAbove = CenterY - yOffset;
                discContent.Children.Add(new Line
                {
                    X1 = CenterX - halfWidth, Y1 = yAbove, X2 = CenterX + halfWidth, Y2 = yAbove,
                    Stroke = Brushes.Black,
                    StrokeThickness = 3
                });
            }

            // Breite wächst nach unten so, dass die Striche fast die 45°-Querneigungs-
            // linie berühren - Lücke von 2x Strichdicke (3 -> Lücke 6). Die 45°-Linie
            // hat X-Versatz = Y-Versatz vom Mittelpunkt, also halfWidth = Y-Versatz - 6.
            foreach (int deg in new[] { 10, 20, 30 })
            {
                double yOffset = deg * PixelsPerDegreePitch;
                double halfWidth = yOffset - groundGap;
                double yBelow = CenterY + yOffset;
                discContent.Children.Add(new Line
                {
                    X1 = CenterX - halfWidth, Y1 = yBelow, X2 = CenterX + halfWidth, Y2 = yBelow,
                    Stroke = Brushes.White,
                    StrokeThickness = 3
                });
            }

            // Querneigungsstriche (Bank-Referenz) an den Seiten, nach unten, bei 15°
            // und 45° - kurze diagonale Striche, feste Position auf der Scheibe
            // (drehen nicht mit dem Rollwinkel mit).
            foreach (int bankDeg in new[] { 15, 45 })
            {
                foreach (int side in new[] { -1, 1 })
                {
                    // Winkel von der Waagerechten aus (90°/270°), nicht von der
                    // Senkrechten - Linie kippt von dort um bankDeg nach unten.
                    double angle = side > 0 ? 90 + bankDeg : 270 - bankDeg;
                    double rad = angle * Math.PI / 180.0;
                    double dx = Math.Sin(rad), dy = -Math.Cos(rad);
                    // Radius, bei dem die Linie exakt die (jetzt größere) Ellipsen-Kante
                    // trifft (Ellipse: Breite = OuterRadius, Höhe = 75*1.1), nicht mehr
                    // ein fester Kreis-Radius.
                    const double ellipseRy = 75;
                    double denom = (dx / OuterRadius) * (dx / OuterRadius) + (dy / ellipseRy) * (dy / ellipseRy);
                    double edgeRadius = Math.Sqrt(1.0 / denom);

                    var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, 0);
                    var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, edgeRadius);
                    discContent.Children.Add(new Line
                    {
                        X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                        Stroke = Brushes.White,
                        StrokeThickness = 3
                    });
                }
            }

            _discTranslate = new TranslateTransform(0, 0);
            _discRotate = new RotateTransform(0, CenterX, CenterY);
            // BEIDE Transforms auf den ÄUSSEREN Host (der den Clip trägt) - dadurch
            // bewegt/dreht sich die Clip-Form (das Fenster selbst) mit, nicht nur der
            // Inhalt darunter. Reihenfolge: erst verschieben (Nick, im noch
            // unrotierten Bezugssystem), dann drehen (Rollen).
            discClipHost.RenderTransform = new TransformGroup
            {
                Children = { _discTranslate, _discRotate }
            };
            discClipHost.Children.Add(discContent);
            discOuterClipHost.Children.Add(discClipHost);
            GaugeCanvas.Children.Add(discOuterClipHost);

            // Ring jetzt NACH der Scheibe hinzufügen, damit er vorne liegt (mit
            // transparentem Loch in der Mitte, siehe Clip oben).
            GaugeCanvas.Children.Add(ringClipHost);


            // -----------------------------------------------------------
            // Feste Elemente
            // -----------------------------------------------------------
            var orangeBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
            var stemBrush = new SolidColorBrush(Color.FromRgb(0x5a, 0x38, 0x20));

            // Fester Rollwinkel-Zeiger bei 12 Uhr, bleibt immer aufrecht. Gleiche
            // Breite/Länge (in Pixeln, nicht nur im Winkel - sonst wirkt er bei
            // kleinerem Radius automatisch schmaler) wie das weiße Dreieck vom
            // Außenring, zeigt aber nach außen (zur Skala) - Spitzen überlappen sich
            // um 5% der Länge.
            {
                const double arrowLength = OuterRadius - InnerDiscRadius; // = Länge des weißen Dreiecks
                const double whiteHalfWidthDeg = 3 * 0.7;
                double whiteBaseLinearHalfWidth = OuterRadius * Math.Sin(whiteHalfWidthDeg * Math.PI / 180.0);

                double tipRadius = InnerDiscRadius + 0.20 * arrowLength; // Überlappung vergrößert (5% war nicht sichtbar)
                double baseRadius = tipRadius - arrowLength;
                // Winkel neu berechnen, damit die LINEARE Breite an der Basis gleich
                // groß ist wie beim weißen Dreieck (nicht nur der Gradwert).
                double halfWidthDeg = Math.Asin(whiteBaseLinearHalfWidth / baseRadius) * 180.0 / Math.PI;

                double baseLinearHalfWidth = baseRadius * Math.Sin(halfWidthDeg * Math.PI / 180.0);
                double tipLinearHalfWidth = baseLinearHalfWidth / 2.0; // halb so breit wie an der Basis
                double tipHalfWidthDeg = Math.Asin(tipLinearHalfWidth / tipRadius) * 180.0 / Math.PI;

                var tipLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, -tipHalfWidthDeg, tipRadius);
                var tipRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, tipHalfWidthDeg, tipRadius);
                var baseLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, -halfWidthDeg, baseRadius);
                var baseRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, halfWidthDeg, baseRadius);
                GaugeCanvas.Children.Add(new Polygon
                {
                    Points = new PointCollection(new[] { tipLeft, tipRight, baseRight, baseLeft }),
                    Fill = orangeBrush
                });

                // Kleines blaues Dreieck (Farbe wie die Ellipse/Himmel), liegt AUF dem
                // orangen Trapez (nicht dessen Spitze verlängernd), mittig, halb so
                // hoch, mit demselben Spitzwinkel wie das orange Trapez, aber als
                // echtes (nicht abgeschnittenes) Dreieck.
                double orangeLength = tipRadius - baseRadius;
                double orangeCenterRadius = (tipRadius + baseRadius) / 2.0;
                double blueHeight = orangeLength / 2.0;
                double blueTipRadiusFixed = orangeCenterRadius + blueHeight / 2.0; // Spitze bleibt hier
                double blueHeightExtended = blueHeight * 1.25; // 25% länger, nur nach unten (Richtung Mitte)
                double blueBaseRadius = blueTipRadiusFixed - blueHeightExtended;
                blueHeight = blueHeightExtended; // für die Verjüngungsrate weiter unten
                // Für parallele Seiten zum orangen Trapez: gleiche Verjüngungsrate
                // (linear pro Radius-Einheit) verwenden, nicht dieselbe Gradzahl.
                double taperRate = (baseLinearHalfWidth - baseLinearHalfWidth / 2.0) / orangeLength;
                double blueBaseLinearHalfWidth = taperRate * blueHeight;
                double blueHalfWidthDeg = Math.Asin(blueBaseLinearHalfWidth / blueBaseRadius) * 180.0 / Math.PI;
                double blueTipRadius = blueTipRadiusFixed;

                var blueTip = GaugeDrawing.PointOnCircle(CenterX, CenterY, 0, blueTipRadius);
                var blueBaseLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, -blueHalfWidthDeg, blueBaseRadius);
                var blueBaseRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, blueHalfWidthDeg, blueBaseRadius);
                GaugeCanvas.Children.Add(new Polygon
                {
                    Points = new PointCollection(new[] { blueTip, blueBaseLeft, blueBaseRight }),
                    Fill = new SolidColorBrush(Color.FromRgb(0x8a, 0xd0, 0xdb))
                });
            }

            // Flugzeug-Symbol + schwarzes Trapez: EINE Gruppe, verschiebt sich
            // gemeinsam vertikal mit dem Nick-Trimm-Knopf.
            //   - zwei Tragflächen-Striche (links/rechts), mit Lücke zur Mitte
            //   - ein nach unten offener Halbkreisbogen, der die inneren Enden
            //     der Tragflächen verbindet ("Bogen")
            //   - eine dünne Nadel, die von dort nach oben zum Punkt läuft
            //     (mit Lücke zwischen Tragfläche und Punkt)
            //   - das schwarze Trapez darunter, mit "VACUUM"-Beschriftung
            var aircraftGroup = new Canvas();
            _aircraftGroupTranslate = new TranslateTransform(0, 0);
            aircraftGroup.RenderTransform = _aircraftGroupTranslate;

            const double wingY = CenterY;
            // Verhältnis: DURCHMESSER der inneren Anzeige (Scheibe) = 10 Einheiten
            // (nicht der Radius - das war der Fehler zuvor), ein Flügel = 2/10 lang,
            // Abstand Flügel-zu-Punkt = 1/10.
            const double unit = (2 * InnerDiscRadius) / 10.0;
            const double wingInnerX = 1 * unit;  // Lücke zur Mitte
            const double wingOuterX = wingInnerX + 2.5 * unit;
            const double dotY = wingY; // Punkt jetzt auf gleicher Ebene wie die Tragflächen
            const double bowlDepth = wingInnerX; // gleich wie die Halbbreite, damit der Bogen rund (nicht oval) ist
            double bowlBottomY = wingY + bowlDepth;

            // Brauner Bogen (nach unten gedreht/geöffnet) + der verjüngende Steg, der
            // den Punkt hält - BEIDE zuerst gezeichnet, damit die orangenen Teile
            // (Tragflächen, Punkt) darüber (im Vordergrund) liegen.
            var bowlFigure = new PathFigure { StartPoint = new Point(CenterX - wingInnerX, wingY) };
            bowlFigure.Segments.Add(new ArcSegment(
                new Point(CenterX + wingInnerX, wingY),
                new Size(wingInnerX, bowlDepth), 0, false, SweepDirection.Counterclockwise, true));
            aircraftGroup.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { bowlFigure }),
                Stroke = stemBrush,
                StrokeThickness = 4
            });

            // Verjüngender Steg vom Bogen-Boden hoch zum Punkt (hält ihn fest),
            // verjüngt sich zur Spitze (Punkt) hin auf 50% der Basis-Breite.
            const double stemBaseHalfWidth = 3;
            const double stemTipHalfWidth = stemBaseHalfWidth * 0.5;
            aircraftGroup.Children.Add(new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX - stemBaseHalfWidth, bowlBottomY),
                    new Point(CenterX + stemBaseHalfWidth, bowlBottomY),
                    new Point(CenterX + stemTipHalfWidth, dotY),
                    new Point(CenterX - stemTipHalfWidth, dotY)
                }),
                Fill = stemBrush
            });

            // Tragflächen (mit Lücke zur Mitte) - NACH dem Bogen/Steg, damit sie im
            // Vordergrund liegen.
            aircraftGroup.Children.Add(new Line
            {
                X1 = CenterX - wingOuterX, Y1 = wingY, X2 = CenterX - wingInnerX, Y2 = wingY,
                Stroke = orangeBrush, StrokeThickness = 4.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
            aircraftGroup.Children.Add(new Line
            {
                X1 = CenterX + wingInnerX, Y1 = wingY, X2 = CenterX + wingOuterX, Y2 = wingY,
                Stroke = orangeBrush, StrokeThickness = 4.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });

            // Trapez-Geometrie vorab berechnen (wird von Nadel-Stiel UND Trapez selbst
            // gebraucht): Unterkante sitzt exakt auf der Oberkante des Bogenelements
            // (CenterY+75). Seiten im 60°-Winkel zur Waagerechten. Breite unten/oben
            // als Anteil der waagerechten Sehnenbreite des Bogenelements an dessen
            // Oberkante.
            double chordHalfWidth = Math.Sqrt(OuterRadius * OuterRadius - 75 * 75);
            double chordWidth = chordHalfWidth * 2;
            double trapTopHalfWidth = (4.0 / 50.0) * chordWidth / 2.0;
            double trapTopY = CenterY + 37.5; // 3/8 der Anzeige (300*3/8=112.5) von der Unterkante aus = CenterY+37.5
            double trapBottomY = CenterY + 76; // 1px mehr, damit kein brauner Pixel am Übergang durchscheint
            double trapHeight = trapBottomY - trapTopY;
            // Untere Breite aus oberer Breite + Höhe + 60°-Winkel berechnet (die 9/50-
            // Schätzung war falsch).
            double trapBottomHalfWidth = trapTopHalfWidth + trapHeight / Math.Tan(60.0 * Math.PI / 180.0);

            // Nadel-Stiel nach unten - jetzt Teil der BEWEGLICHEN Gruppe (bewegt sich
            // mit), dafür deutlich verlängert (bis über die Trapez-Unterkante hinaus),
            // damit er auch bei Verschiebung immer bis ins/hinter das Trapez reicht.
            aircraftGroup.Children.Add(new Line
            {
                X1 = CenterX, Y1 = bowlBottomY, X2 = CenterX, Y2 = trapBottomY + 20,
                Stroke = stemBrush,
                StrokeThickness = 6
            });

            // Punkt, mittig auf gleicher Ebene wie die Tragflächen - NACH allem
            // anderen, damit er ganz oben liegt.
            var dot = new Ellipse { Width = 9, Height = 9, Fill = orangeBrush };
            Canvas.SetLeft(dot, CenterX - 4.5);
            Canvas.SetTop(dot, dotY - 4.5);
            aircraftGroup.Children.Add(dot);

            // Bewegliche Gruppe ZUERST hinzufügen, damit Trapez/VACUUM danach (im
            // Vordergrund) darüber liegen und sie verdecken, wo sie sich überlappen.
            GaugeCanvas.Children.Add(aircraftGroup);

            // Schwarzes Trapez (Farbe wie der Bezel-Rahmen) mit "VACUUM" - bleibt FEST
            // (nicht Teil der beweglichen Gruppe, direkt auf GaugeCanvas), liegt jetzt
            // VOR der beweglichen Gruppe.
            GaugeCanvas.Children.Add(new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX - trapTopHalfWidth, trapTopY),
                    new Point(CenterX + trapTopHalfWidth, trapTopY),
                    new Point(CenterX + trapBottomHalfWidth, trapBottomY),
                    new Point(CenterX - trapBottomHalfWidth, trapBottomY)
                }),
                Fill = new SolidColorBrush(BezelBlack)
            });
            // Echte Vermessung statt geschätzter Breite (die Schätzformel lag daneben,
            // Text wirkte nicht mittig) - Unterkante des Wortes 2px über der
            // Trapez-Unterkante.
            var vacuumText = new TextBlock
            {
                Text = "VACUUM",
                FontSize = 9,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold
            };
            vacuumText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(vacuumText, CenterX - vacuumText.DesiredSize.Width / 2.0);
            Canvas.SetTop(vacuumText, trapBottomY - 2 - vacuumText.DesiredSize.Height);
            GaugeCanvas.Children.Add(vacuumText);

            // Fester unterer Kreissegment-Bereich (~10% der Anzeige), immer
            // schwarz (Bezel-Farbe) - unabhängig vom Trimm-Knopf, damit der
            // untere Rand der Anzeige immer sauber abgedeckt ist.
            // Unterer Bereich schwarz: oben eine waagerechte Kante, unten der
            // natürliche Radius der Anzeige (echtes Kreissegment, keine Ecken wie
            // bei einem Rechteck) - Schnittmenge aus Zifferblatt-Kreis und "alles
            // unterhalb dieser Höhe".
            // Oberkante bei 1/4 der gesamten Anzeige (Bezel-Durchmesser 300), von der
            // Unterkante aus gemessen: 300/4 = 75 -> CenterY+75.
            const double segmentTopY = CenterY + 75;
            var segmentGeometry = new CombinedGeometry(
                GeometryCombineMode.Intersect,
                new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius + 1, OuterRadius + 1),
                new RectangleGeometry(new Rect(CenterX - OuterRadius - 1, segmentTopY, (OuterRadius + 1) * 2, (OuterRadius + 1) * 2)));
            var bottomSegment = new System.Windows.Shapes.Path
            {
                Data = segmentGeometry,
                Fill = new SolidColorBrush(BezelBlack)
            };
            GaugeCanvas.Children.Add(bottomSegment);

            // Dünner, 10% hellerer Strich genau auf der waagerechten Oberkante des
            // Bogenelements - liegt AUF dem Element (macht es nicht höher).
            double segmentChordHalfWidth = Math.Sqrt((OuterRadius + 1) * (OuterRadius + 1) - 75 * 75);
            GaugeCanvas.Children.Add(new Line
            {
                X1 = CenterX - segmentChordHalfWidth, Y1 = segmentTopY + 1,
                X2 = CenterX + segmentChordHalfWidth, Y2 = segmentTopY + 1,
                Stroke = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x47)),
                StrokeThickness = 2
            });

            // -----------------------------------------------------------
            // Drehknopf (Nick-Trimm-Einstellung): Unterkante liegt auf der
            // AUSSENKANTE der Anzeige (150) - anders als beim Höhenmesser
            // (dort: halbe Bezel-Höhe), dadurch weiter oben/eingerückter als
            // in der Vorlage, wo der Knopf unten übersteht. Stil (Rändelung,
            // Bedienung) 1:1 vom Höhenmesser übernommen.
            // -----------------------------------------------------------
            const double knobDiameter = 48;
            const double knobRadius = knobDiameter / 2;
            double knobDistance = (OuterRadius + 0) - knobRadius; // Unterkante des Knopfs = Innenkante des Rahmens (132) + 0 (5 weiter nach oben als +5)
            var knobCenter = GaugeDrawing.PointOnCircle(CenterX, CenterY, 180, knobDistance);

            var knobShadow = new Ellipse
            {
                Width = knobDiameter,
                Height = knobDiameter,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x8a, 0x8a, 0x8e), 0.0),
                        new GradientStop(Color.FromRgb(0x30, 0x30, 0x33), 1.0)
                    }),
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(knobShadow, knobCenter.X - knobRadius);
            Canvas.SetTop(knobShadow, knobCenter.Y - knobRadius);
            GaugeCanvas.Children.Add(knobShadow);

            _knobRotation = new RotateTransform(0, knobCenter.X, knobCenter.Y);
            const int knurlCount = 28;
            for (int i = 0; i < knurlCount; i++)
            {
                double angle = i * 360.0 / knurlCount;
                double rad = angle * Math.PI / 180.0;
                var inner = new Point(knobCenter.X + 2 * Math.Sin(rad), knobCenter.Y - 2 * Math.Cos(rad));
                var outer = new Point(knobCenter.X + (knobRadius - 1) * Math.Sin(rad), knobCenter.Y - (knobRadius - 1) * Math.Cos(rad));
                var knurlLine = new Line
                {
                    X1 = inner.X, Y1 = inner.Y, X2 = outer.X, Y2 = outer.Y,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1
                };
                knurlLine.RenderTransform = _knobRotation;
                GaugeCanvas.Children.Add(knurlLine);
            }

            var knobHitArea = new Canvas { Width = knobDiameter * 1.5, Height = knobDiameter * 1.5, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            Canvas.SetLeft(knobHitArea, knobCenter.X - knobRadius * 1.5);
            Canvas.SetTop(knobHitArea, knobCenter.Y - knobRadius * 1.5);
            knobHitArea.MouseWheel += Knob_MouseWheel;
            knobHitArea.MouseLeftButtonDown += Knob_MouseLeftButtonDown;
            knobHitArea.MouseMove += Knob_MouseMove;
            knobHitArea.MouseLeftButtonUp += Knob_MouseLeftButtonUp;
            GaugeCanvas.Children.Add(knobHitArea);

            // -----------------------------------------------------------
            // Rote GYRO-Warnklappe bei 10:30-Uhr-Position: sichtbar bei zu niedrigem
            // Unterdruck (Motor aus), sonst ausgeblendet. Buchstaben gestapelt, "O"
            // am Scharnier-Ende (unten/innen).
            // -----------------------------------------------------------
            // Rote GYRO-Warnklappe: Drehpunkt liegt AUSSERHALB der Anzeige (hinter dem
            // Rahmen, oben), echtes Rechteck (konstante Breite), hängt im sichtbaren
            // Zustand mit 70° zum Horizont (=20° von der Senkrechten) nach unten in
            // die Anzeige. Im ausgeblendeten Zustand um 70° weiter in dieselbe
            // Richtung gedreht (dann waagerecht, hinter dem Rahmen verborgen).
            {
                double pivotAngleDeg = 315; // absolute Position
                const double pivotRadius = 145;
                var pivot = GaugeDrawing.PointOnCircle(CenterX, CenterY, pivotAngleDeg, pivotRadius);

                const double flagLength = 132 * 0.75; // 3/4 so lang wie zuvor
                const double flagHalfWidth = 8 * 0.8; // 20% schmaler

                // Rechteck in absoluten Koordinaten, "unten" (gerade nach unten vom
                // Drehpunkt) als Referenz-Nullstellung vor der Rotation.
                var p1 = new Point(pivot.X - flagHalfWidth, pivot.Y);
                var p2 = new Point(pivot.X + flagHalfWidth, pivot.Y);
                var p3 = new Point(pivot.X + flagHalfWidth, pivot.Y + flagLength);
                var p4 = new Point(pivot.X - flagHalfWidth, pivot.Y + flagLength);

                var gyroFlag = new Polygon
                {
                    Points = new PointCollection(new[] { p1, p2, p3, p4 }),
                    Fill = Brushes.Red
                };

                var gyroFlagGroup = new Canvas();
                gyroFlagGroup.Children.Add(gyroFlag);

                // Buchstaben gestapelt: "G" am fernen Ende, "O" nahe am Drehpunkt -
                // in derselben Referenz-Nullstellung (gerade nach unten) positioniert,
                // drehen sich mit der Gruppe mit.
                char[] letters = { 'O', 'R', 'Y', 'G' };
                // "O" bleibt an seiner bisherigen Position (fernes Ende), die
                // restlichen Buchstaben rücken mit festem, engerem Abstand darüber.
                const double letterSpacing = 13;
                double oY = flagLength - (0 + 0.5) / letters.Length * flagLength;
                for (int i = 0; i < letters.Length; i++)
                {
                    double localY = oY - i * letterSpacing;
                    var letterCenter = new Point(pivot.X, pivot.Y + localY);
                    var letterText = new TextBlock
                    {
                        Text = letters[i].ToString(),
                        FontSize = 11,
                        Foreground = Brushes.White,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontWeight = FontWeights.Bold
                    };
                    letterText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(letterText, letterCenter.X - letterText.DesiredSize.Width / 2.0);
                    Canvas.SetTop(letterText, letterCenter.Y - letterText.DesiredSize.Height / 2.0);
                    gyroFlagGroup.Children.Add(letterText);
                }

                // Zwei feste Winkel-Positionen um den Drehpunkt: sichtbar (20° von der
                // Senkrechten = 70° vom Horizont) bzw. ausgeblendet (70° weiter gedreht).
                _gyroFlagRotate = new RotateTransform(-20, pivot.X, pivot.Y);
                gyroFlagGroup.RenderTransform = _gyroFlagRotate;

                _gyroFlagGroup = gyroFlagGroup;

                // Fester äußerer Clip (Zifferblatt-Radius) - alles außerhalb (Drehpunkt
                // und der Teil der Fahne jenseits des Randes) liegt "hinter dem
                // Rahmen" und darf nicht sichtbar sein.
                var gyroFlagClipHost = new Canvas
                {
                    Clip = new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, OuterRadius)
                };
                gyroFlagClipHost.Children.Add(gyroFlagGroup);
                GaugeCanvas.Children.Add(gyroFlagClipHost);
            }
        }

        // ---------------------------------------------------------------
        // Drehknopf-Bedienung (Mausrad oder horizontales Ziehen), analog
        // zum Höhenmesser-Knopf.
        // ---------------------------------------------------------------
        private void Knob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            SendKnobStep(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        private void Knob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStartX = e.GetPosition(GaugeCanvas).X;
            _dragAccumulated = 0;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void Knob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            double x = e.GetPosition(GaugeCanvas).X;
            double delta = x - _dragStartX; // nach rechts ziehen = positiv = erhöhen
            _dragStartX = x;
            _dragAccumulated += delta;

            while (_dragAccumulated >= DragPixelsPerStep)
            {
                SendKnobStep(1);
                _dragAccumulated -= DragPixelsPerStep;
            }
            while (_dragAccumulated <= -DragPixelsPerStep)
            {
                SendKnobStep(-1);
                _dragAccumulated += DragPixelsPerStep;
            }
        }

        private void Knob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        private void SendKnobStep(int direction)
        {
            _service?.SendEvent(direction > 0 ? "ATTITUDE_BARS_POSITION_UP" : "ATTITUDE_BARS_POSITION_DOWN");
            DebugLog.Write("[ATTITUDE-KNOB] direction=" + direction);

            if (_knobRotation != null)
                _knobRotation.Angle -= direction * KnobDegreesPerStep; // Richtung umgekehrt
        }
    }
}
