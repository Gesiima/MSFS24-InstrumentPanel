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
    /// <summary>
    /// EGT (links) / Fuel Flow (rechts) Kombianzeige - geometrisch 1:1 vom
    /// Fuel-Template (FuelGaugeControl) übernommen, nur Beschriftung und
    /// Wertebereiche angepasst (siehe Vorbild-Foto: zwei Halbskalen mit
    /// gemeinsamem Gehäuse, "EGT REF"-Knopf links, "FUEL"/"FLOW" rechts,
    /// "GAL"/"HR" unten).
    ///
    /// WICHTIG zum "EGT REF"-Knopf: Das ist beim echten Vorbild ein rein
    /// mechanischer Referenz-Zeiger, den der Pilot beim Abmagern von Hand auf
    /// den aktuellen (Peak-)EGT-Wert setzt, um die Abweichung davon abzulesen.
    /// MSFS/SimConnect liefert dafür keinen Wert (keine "Peak-EGT"- oder
    /// "Referenz"-Variable) - der Knopf ist hier daher rein dekorativ ohne
    /// Funktion, nur der tatsächliche EGT-Wert selbst wird angezeigt.
    /// </summary>
    public partial class EgtFlowGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132;

        [StructLayout(LayoutKind.Sequential)]
        private struct EgtFlowStruct
        {
            public double EgtRankine; // GENERAL ENG EXHAUST GAS TEMPERATURE:1 (Rankine)
            public double FlowGph;    // ENG FUEL FLOW GPH:1
        }

        private const double ArcCenterOffset = 28.5;
        private const double ArcRadius = 81.6;
        private static readonly Point LeftArcCenterPoint = new Point((CenterX - OuterRadius) + ArcCenterOffset, CenterY);
        private static readonly Point RightArcCenterPoint = new Point((CenterX + OuterRadius) - ArcCenterOffset, CenterY);
        // Zeiger-Drehpunkt = Skalen-Mittelpunkt (anders als beim Fuel-Template,
        // dort waren das zwei getrennte Punkte) - dadurch zeigt der Zeiger direkt
        // auf die Winkel-Position der Skala, ohne Umrechnung über einen
        // separaten Drehpunkt.
        private static readonly Point LeftNeedlePivotPoint = LeftArcCenterPoint;
        private static readonly Point RightNeedlePivotPoint = RightArcCenterPoint;

        // Wertebereiche der beiden Skalen - bei Bedarf leicht anpassbar, falls
        // sich nach dem ersten Live-Test eine andere Kalibrierung als sinnvoller
        // herausstellt (v.a. die EGT-Grenzen sind vom Vorbild-Foto nicht exakt
        // ablesbar, nur die "25°F je Teilstrich"-Beschriftung).
        private static readonly double EgtMinF = AppSettings.EgtMinF; // unterer Skalenrand ("kalt"-Seite) - unterster großer Tick (Index 17) = 1760 Rankine (1300,33°F)
        private static readonly double EgtMaxF = AppSettings.EgtMaxF; // oberer Skalenrand ("E"-Seite) - oberster großer Tick (Index 2) = 2060 Rankine (1600,33°F)
        private const double FlowMaxGph = 19; // oberer Skalenrand rechts (siehe Vorbild: höchste Zahl "19")
        private const double FlowGreenBandStart = 0;  // grüner Normalbereich
        private const double FlowGreenBandEnd = 12; // einen Tick (1 GAL/HR) weiter als zuvor

        private SimConnectService _service;
        private RotateTransform _egtNeedleRotate;
        private RotateTransform _flowNeedleRotate;
        private RotateTransform _egtRefKnobRotation; // rein visuell, keine SimConnect-Anbindung (siehe Klassen-Kommentar)
        private RotateTransform _egtRefTextRotation; // wie _egtRefKnobRotation, aber um -90° versetzt, damit die Schrift gerade steht, wenn der Zeiger waagerecht ist
        private bool _dragging;
        private double _dragStartX;
        private double _dragAccum;
        private const double DragPixelsPerStep = 5;
        private const double KnobDegreesPerStep = 110.0 / 19.0 / 2.0; // halber Tick-Abstand (20 Ticks = 19 Zwischenräume über 110°) - Rastung trifft abwechselnd Tick und Tick-Mitte
        private double _displayedEgtF = double.NaN;
        private double _displayedFlow = double.NaN;
        private DateTime? _lastUpdateTime;

        public EgtFlowGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            _service = service;
            service.Register<EgtFlowStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("GENERAL ENG EXHAUST GAS TEMPERATURE:1", "rankine", SIMCONNECT_DATATYPE.FLOAT64),
                    ("ENG FUEL FLOW GPH:1", "gallons per hour", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        public void UpdateStatus(string text, Brush color)
        {
            // Verbindungsstatus wird zentral im Fenster (MainWindow) angezeigt.
        }

        private void OnData(EgtFlowStruct data)
        {
            double egtF = data.EgtRankine - 459.67; // Rankine -> Fahrenheit
            double flow = data.FlowGph;

            // EGT ändert sich beim Gasgeben vergleichsweise schnell, Fuel Flow
            // etwas gemächlicher - je eigene Glättungsrate statt eines
            // gemeinsamen Werts.
            const double maxRatePerSecondEgt = 240.0;  // °F/s (+20%)
            const double maxRatePerSecondFlow = 12.0;  // GPH/s (+20%)
            var now = DateTime.UtcNow;
            double dt = _lastUpdateTime.HasValue ? (now - _lastUpdateTime.Value).TotalSeconds : 0;
            dt = Math.Max(0, Math.Min(1, dt));
            _lastUpdateTime = now;

            if (double.IsNaN(_displayedEgtF)) _displayedEgtF = egtF;
            if (double.IsNaN(_displayedFlow)) _displayedFlow = flow;

            _displayedEgtF = MoveTowards(_displayedEgtF, egtF, maxRatePerSecondEgt * dt);
            _displayedFlow = MoveTowards(_displayedFlow, flow, maxRatePerSecondFlow * dt);

            if (_egtNeedleRotate != null)
                _egtNeedleRotate.Angle = AngleForNeedle(FractionEgt(_displayedEgtF), LeftNeedlePivotPoint, LeftArcCenterPoint, mirrored: false);
            if (_flowNeedleRotate != null)
                _flowNeedleRotate.Angle = AngleForNeedle(FractionFlow(_displayedFlow), RightNeedlePivotPoint, RightArcCenterPoint, mirrored: true);
        }

        private static double MoveTowards(double current, double target, double maxStep)
        {
            double diff = target - current;
            if (Math.Abs(diff) <= maxStep) return target;
            return current + Math.Sign(diff) * maxStep;
        }

        private static double FractionEgt(double egtF) => Math.Max(0, Math.Min(1, (egtF - EgtMinF) / (EgtMaxF - EgtMinF)));

        // Flow-Skala ist NICHT durchgehend linear: 0 bis 5 GAL/HR ist auf der
        // Skala nur so breit wie EIN normaler kleiner Teilstrich (stark
        // gestaucht). Ab 5 auch NICHT mehr gleichmäßig linear, sondern durch
        // vier feste Kalibrierpunkte festgelegt: "10" sitzt 25° unterhalb,
        // "15" 15° oberhalb der waagerechten Skalen-Mitte (X-Achse) - dazwischen
        // (und darüber hinaus bis 19) jeweils linear interpoliert.
        private static readonly double[] FlowCalibrationValues = { 0, 5, 10, 15, FlowMaxGph };
        private static readonly double[] FlowCalibrationFractions = { 0.0, 1.0 / 15.0, 30.0 / 110.0, 70.0 / 110.0, 1.0 };

        private static double FractionFlow(double gph)
        {
            double v = Math.Max(0, Math.Min(FlowMaxGph, gph));
            for (int i = 0; i < FlowCalibrationValues.Length - 1; i++)
            {
                double v0 = FlowCalibrationValues[i], v1 = FlowCalibrationValues[i + 1];
                if (v <= v1 || i == FlowCalibrationValues.Length - 2)
                {
                    double f0 = FlowCalibrationFractions[i], f1 = FlowCalibrationFractions[i + 1];
                    double t = (v1 > v0) ? (v - v0) / (v1 - v0) : 0;
                    return f0 + t * (f1 - f0);
                }
            }
            return 1.0;
        }

        // Gemeinsame Winkel-Formel wie beim Fuel-Template, aber auf einen
        // normierten 0..1-Anteil statt eines festen 0-26-Wertebereichs
        // umgestellt - so können EGT und Fuel Flow trotz unterschiedlicher
        // Wertebereiche dieselbe Bogen-Geometrie nutzen.
        private static double AngleForFraction(double fraction, bool mirrored)
        {
            double clamped = Math.Max(0, Math.Min(1, fraction));
            double baseAngle = 55 - clamped * 110.0;
            double a = mirrored ? -baseAngle + 180 : baseAngle;
            return a + 90;
        }

        // Da Zeiger-Drehpunkt = Skalen-Mittelpunkt ist, entspricht der Zeiger-
        // Winkel direkt der Skalen-Winkel-Formel - keine Umrechnung über einen
        // separaten Drehpunkt mehr nötig (anders als beim Fuel-Template).
        private static double AngleForNeedle(double fraction, Point needlePivot, Point arcCenter, bool mirrored)
        {
            return AngleForFraction(fraction, mirrored);
        }

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

            DrawEgtScaleArc(LeftArcCenterPoint, ArcRadius);
            DrawFlowScaleArc(RightArcCenterPoint, ArcRadius);

            const double textEdgeOffset = (14.0 / 110.0) * (OuterRadius * 2);
            const double estimatedHalfLetterWidth = 6.75;

            // "EGT"-Beschriftung direkt neben der EGT-Skala, im selben Bereich
            // (radial nach innen versetzt), in dem bei FUEL FLOW die Zahlen
            // "10"/"15" stehen - mittlere Höhe der Skala. JETZT VOR beiden
            // Zeigern gezeichnet, damit sie im Hintergrund liegt (beide Zeiger,
            // weiß und rot, liegen darüber).
            var egtLabelPoint = GaugeDrawing.PointOnCircle(LeftArcCenterPoint.X, LeftArcCenterPoint.Y, AngleForFraction(0.5, false), ArcRadius - 30);
            DrawStackedLabel("EGT", egtLabelPoint.X, egtLabelPoint.Y);

            // "EGT REF"-Referenz-Strich VOR dem weißen Zeiger gezeichnet, damit
            // er hinter diesem liegt (nicht mehr darüber). Der Knopfkörper
            // selbst kommt weiterhin erst nach dem Rahmen (DrawSideSegment),
            // bleibt also immer vollständig sichtbar.
            DrawEgtRefMarker();

            _egtNeedleRotate = DrawNeedle(LeftNeedlePivotPoint);
            _flowNeedleRotate = DrawNeedle(RightNeedlePivotPoint);
            _egtNeedleRotate.Angle = AngleForNeedle(FractionEgt(EgtMinF), LeftNeedlePivotPoint, LeftArcCenterPoint, mirrored: false);
            _flowNeedleRotate.Angle = AngleForNeedle(FractionFlow(0), RightNeedlePivotPoint, RightArcCenterPoint, mirrored: true);

            const double chordOffset = 76.8;
            DrawSideSegment(-1, chordOffset);
            DrawSideSegment(1, chordOffset);

            DrawEgtRefKnobBody();

            // "FUEL"/"FLOW" zweispaltig am rechten Rand statt "LEFT"/"RIGHT" -
            // jede Spalte für sich gestapelt, wie im Vorbild (F/F, U/L, E/O, L/W
            // spaltenweise gelesen ergibt "FUEL" und "FLOW").
            double rightTextCenterX = (CenterX + OuterRadius) - textEdgeOffset - estimatedHalfLetterWidth;
            DrawStackedLabel("FUEL", rightTextCenterX - 9 + 3 + 4 - 4 - 4, CenterY, leftAlign: true);
            DrawStackedLabel("FLOW", rightTextCenterX + 9 + 3 + 4 - 4 - 4 - 4, CenterY, leftAlign: true);
        }

        /// <summary>
        /// "EGT REF"-Referenz-Knopf DIREKT auf dem Drehpunkt des linken (EGT-)
        /// Zeigers, per Maus-Ziehen drehbar (wie der OBS-Knopf beim VOR) - rein
        /// visuell, ohne SimConnect-Anbindung/Auswirkung (siehe Klassen-
        /// Kommentar oben: MSFS liefert keinen "Peak-EGT"-Wert).
        /// </summary>
        private const double KnobRadius = 15 * 1.2; // zurück auf die ursprüngliche Größe (15), dann nur 20% größer (=18)

        /// <summary>
        /// Nur der rote Referenz-"Zeiger" - wird VOR dem Rahmen (DrawSideSegment)
        /// gezeichnet, damit dieser ihn dort überdeckt, wo sich beide
        /// überschneiden (wie ein echter Zeiger, der teils unter dem Rahmen liegt).
        /// </summary>
        private void DrawEgtRefMarker()
        {
            var knobCenter = LeftNeedlePivotPoint;
            // Startposition: untere ("kalte"/MIN-)Skalen-Position, nicht der
            // technische Winkel-Nullpunkt und auch nicht MAX.
            _egtRefKnobRotation = new RotateTransform(AngleForFraction(0, false), knobCenter.X, knobCenter.Y);
            _egtRefTextRotation = new RotateTransform(AngleForFraction(0, false) - 90, knobCenter.X, knobCenter.Y);

            var markerGroup = new Canvas();
            var marker = new Line
            {
                X1 = knobCenter.X, Y1 = knobCenter.Y,
                X2 = knobCenter.X, Y2 = knobCenter.Y - NeedleLength,
                Stroke = Brushes.Red,
                StrokeThickness = 3
            };
            markerGroup.Children.Add(marker);
            markerGroup.RenderTransform = _egtRefKnobRotation;
            GaugeCanvas.Children.Add(markerGroup);
        }

        /// <summary>
        /// Knopfkörper + Beschriftung - wird NACH dem Rahmen gezeichnet, bleibt
        /// also (anders als der Zeiger) immer vollständig sichtbar. Die
        /// Beschriftung dreht sich zusammen mit dem Knopf mit.
        /// </summary>
        private void DrawEgtRefKnobBody()
        {
            var knobCenter = LeftNeedlePivotPoint;

            var knobBody = new Ellipse
            {
                Width = KnobRadius * 2,
                Height = KnobRadius * 2,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x5a, 0x5a, 0x5c), 0.0),
                        new GradientStop(Color.FromRgb(0x28, 0x28, 0x2a), 1.0)
                    }),
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand
                // KEINE Drehung hier: bei einem per Canvas.SetLeft/Top
                // positionierten Element bezieht sich der Rotationsmittelpunkt
                // auf dessen EIGENEN lokalen Ursprung, nicht auf die absolute
                // Canvas-Position - das führte dazu, dass sich der Knopf beim
                // Drehen sichtbar "wegbewegt" hat. Ein Kreis sieht gedreht
                // ohnehin identisch aus, die Drehung wird hier schlicht nicht gebraucht.
            };
            Canvas.SetLeft(knobBody, knobCenter.X - KnobRadius);
            Canvas.SetTop(knobBody, knobCenter.Y - KnobRadius);
            GaugeCanvas.Children.Add(knobBody);

            // "EGT"/"REF"-Beschriftung auf dem Knopf - dreht sich mit (eigene
            // Gruppe mit derselben Rotation wie der Knopfkörper/Zeiger).
            var textGroup = new Canvas { RenderTransform = _egtRefTextRotation };
            var line1 = new TextBlock { Text = "EGT", FontSize = 9, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold };
            line1.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(line1, knobCenter.X - line1.DesiredSize.Width / 2.0);
            Canvas.SetTop(line1, knobCenter.Y - line1.DesiredSize.Height + 1);
            textGroup.Children.Add(line1);

            var line2 = new TextBlock { Text = "REF", FontSize = 9, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold };
            line2.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(line2, knobCenter.X - line2.DesiredSize.Width / 2.0);
            Canvas.SetTop(line2, knobCenter.Y - 1);
            textGroup.Children.Add(line2);
            GaugeCanvas.Children.Add(textGroup);

            // Greifbereich deutlich größer als der sichtbare Knopf (2,5x) -
            // muss nicht mehr präzise auf den kleinen Knopf getroffen werden.
            const double hitAreaRadius = KnobRadius * 2.5;
            var hitArea = new Canvas { Width = hitAreaRadius * 2, Height = hitAreaRadius * 2, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            Canvas.SetLeft(hitArea, knobCenter.X - hitAreaRadius);
            Canvas.SetTop(hitArea, knobCenter.Y - hitAreaRadius);
            hitArea.MouseLeftButtonDown += (s, e) =>
            {
                _dragging = true;
                _dragStartX = e.GetPosition(GaugeCanvas).X;
                _dragAccum = 0;
                ((UIElement)s).CaptureMouse();
                e.Handled = true;
            };
            hitArea.MouseMove += (s, e) => HandleEgtRefDrag(e);
            hitArea.MouseLeftButtonUp += (s, e) => { _dragging = false; ((UIElement)s).ReleaseMouseCapture(); };
            // Drehen auch per Mausrad, solange der Mauszeiger über dem Knopf ist.
            // Negatives Vorzeichen: Mausrad nach oben/vorne = Zeiger im
            // Uhrzeigersinn (gefühlt "richtig herum").
            hitArea.MouseWheel += (s, e) =>
            {
                RotateEgtRefKnob(-Math.Sign(e.Delta) * KnobDegreesPerStep);
                e.Handled = true;
            };
            GaugeCanvas.Children.Add(hitArea);
        }

        /// <summary>
        /// Dreht den EGT-REF-Knopf (Zeiger UND Beschriftung, synchron mit
        /// konstantem Versatz zueinander) um das angegebene Winkel-Delta,
        /// begrenzt auf den Anschlag-Bereich, der dem tatsächlichen
        /// Skalenbereich des EGT-Zeigers entspricht (kein freies 360°-Drehen möglich).
        /// </summary>
        private void RotateEgtRefKnob(double deltaDegrees)
        {
            double angleAtZero = AngleForFraction(0, false);
            double angleAtOne = AngleForFraction(1, false);
            double minAngle = Math.Min(angleAtZero, angleAtOne);
            double maxAngle = Math.Max(angleAtZero, angleAtOne);

            double newAngle = _egtRefKnobRotation.Angle + deltaDegrees;
            double clamped = Math.Max(minAngle, Math.Min(maxAngle, newAngle));
            double appliedDelta = clamped - _egtRefKnobRotation.Angle;

            _egtRefKnobRotation.Angle = clamped;
            _egtRefTextRotation.Angle += appliedDelta; // bleibt mit konstantem -90°-Versatz synchron
        }

        private void HandleEgtRefDrag(MouseEventArgs e)
        {
            if (!_dragging) return;

            double x = e.GetPosition(GaugeCanvas).X;
            double delta = x - _dragStartX;
            _dragStartX = x;
            _dragAccum += delta;

            while (_dragAccum >= DragPixelsPerStep)
            {
                RotateEgtRefKnob(KnobDegreesPerStep);
                _dragAccum -= DragPixelsPerStep;
            }
            while (_dragAccum <= -DragPixelsPerStep)
            {
                RotateEgtRefKnob(-KnobDegreesPerStep);
                _dragAccum += DragPixelsPerStep;
            }
        }

        private const double NeedleLength = ArcRadius + 19; // reicht jetzt genau bis zur neuen Außenkante der Ticks (100,6), gilt für alle drei Zeiger (EGT, Flow, roter Referenz-Strich)

        private RotateTransform DrawNeedle(Point pivot)
        {
            const double tanTailAngle = 0.26794919;
            const double oldHubHalfWidth = (NeedleLength * 0.25) * tanTailAngle;
            const double hubHalfWidth = oldHubHalfWidth * 1.5 * 0.8;
            double wideY = pivot.Y;
            double tipY = pivot.Y - NeedleLength;

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

        private void DrawStackedLabel(string text, double centerX, double centerY, bool leftAlign = false)
        {
            const double fontSize = 20.25;
            const double letterSpacing = 24;
            double startY = centerY - (text.Length - 1) * letterSpacing / 2.0;

            // Bei linksbündiger Ausrichtung: Referenzbreite (Breite von "E")
            // ermitteln - breitere Buchstaben (z.B. "U", "O", "W") werden
            // horizontal darauf gestaucht, damit die Wörter trotz
            // unterschiedlicher Buchstabenbreiten gleichmäßig/linksbündig
            // aussehen. Schmalere Buchstaben (z.B. "F", "L", "I") bleiben
            // unverändert (kein künstliches Aufweiten).
            double referenceWidth = 0;
            if (leftAlign)
            {
                var reference = new TextBlock { Text = "E", FontSize = fontSize, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold };
                reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                referenceWidth = reference.DesiredSize.Width;
            }

            for (int i = 0; i < text.Length; i++)
            {
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

                if (leftAlign && letter.DesiredSize.Width > referenceWidth)
                {
                    double scaleX = referenceWidth / letter.DesiredSize.Width;
                    letter.RenderTransform = new ScaleTransform(scaleX, 1.0);
                }

                Canvas.SetLeft(letter, leftAlign ? centerX : centerX - letter.DesiredSize.Width / 2.0);
                Canvas.SetTop(letter, y - letter.DesiredSize.Height / 2.0);
                GaugeCanvas.Children.Add(letter);
            }
        }

        /// <summary>
        /// EGT-Skala links: viele feine, UNBESCHRIFTETE Teilstriche (wie ein
        /// Thermometer) statt einzelner Zahlen - "E" oben (heißes Ende, wie im
        /// Vorbild-Foto), "25°F"/"DIV" unten als Erklärung der Teilstrich-Größe.
        /// </summary>
        private void DrawEgtScaleArc(Point arcCenter, double radius)
        {
            double endAngle = AngleForFraction(0, false);   // unten

            // Kein durchgehender weißer Bogen mehr - nur die Teilstriche selbst.
            // Muster von "E" (oben) zur "25°F/DIV"-Seite (unten): 2 klein, 1
            // groß, 4 klein, 1 groß, 4 klein, 1 groß, 4 klein, 1 groß, 2 klein
            // (20 Teilstriche insgesamt).
            bool[] isMajor =
            {
                false, false, true,
                false, false, false, false, true,
                false, false, false, false, true,
                false, false, false, false, true,
                false, false
            };
            int tickCount = isMajor.Length - 1; // 19 Zwischenräume für 20 Striche
            for (int i = 0; i < isMajor.Length; i++)
            {
                double fraction = 1.0 - (double)i / tickCount; // i=0 -> oben ("E"/1.0), i=letzte -> unten (0.0)
                double angle = AngleForFraction(fraction, false);
                // Außenkante (zur Gehäuse-Mitte/zum Spalt zwischen den Skalen
                // hin) 4 weiter verlängert, damit der Abstand zwischen EGT- und
                // FLOW-Skala kleiner wird. Innenkante bleibt unverändert.
                double inner = (isMajor[i] ? radius - 10 : radius - 6) + 4; // 4 kürzer, Außenkante bleibt
                double outer = radius + (isMajor[i] ? 10 : 6) + 4 + 5;
                var tickInner = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, inner);
                var tickOuter = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, outer);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = tickInner.X, Y1 = tickInner.Y, X2 = tickOuter.X, Y2 = tickOuter.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = isMajor[i] ? 3 : 1.5
                });
            }

            // "25°F" / "DIV" unten - Erklärung der Teilstrich-Größe, nicht Teil
            // der eigentlichen Skala. Schriftgröße wie die Zahlen (20.25).
            // "25°F" auf Höhe von "GAL" (-9), "DIV" auf Höhe von "HR" (+15).
            var divPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, endAngle, radius + 28.5);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "25°F", divPoint.X, divPoint.Y - 9 + 3 + 2, 20.25, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "DIV", divPoint.X, divPoint.Y + 15, 20.25, Brushes.White, true);
        }

        /// <summary>
        /// Fuel-Flow-Skala rechts: beschriftete Teilstriche bei 0/5/10/15/19
        /// GAL/HR, mit grünem Normalbereich-Band (siehe FlowGreenBandStart/End).
        /// </summary>
        private void DrawFlowScaleArc(Point arcCenter, double radius)
        {
            double AngleFor(double v) => AngleForFraction(FractionFlow(v), true);

            double endAngle = AngleFor(0);

            // Grünes Normalbereich-Band: 0 bis 12 GAL/HR. Radius um 2,8 nach
            // außen verschoben (mittig zwischen den neuen kleinen Tick-Rändern),
            // damit es weiterhin zu den Strichen passt - Breite (Dicke)
            // unverändert bei 12. Bogenrichtung gegenläufig zur EGT-Seite
            // (gespiegelte/rechte Skala).
            // Innerer Rand bleibt wie zuvor (radius+1), äußerer schließt jetzt
            // Äußerer Rand bleibt an der Kante (radius+10, unverändert), Breite
            // wächst um 2 (9 -> 11), Mittelpunkt entsprechend bei radius+4,5.
            // Äußerer Rand bleibt an der (jetzt nochmal 5 weiter verschobenen)
            // Kante bei radius+19, Breite weiterhin nur um 2 gegenüber dem
            // ursprünglichen Stand gewachsen (9 -> 11), Mittelpunkt entsprechend
            // bei radius+13,5.
            double bandRadius = radius + 13.5;
            var greenStart = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, AngleFor(FlowGreenBandEnd), bandRadius);
            var greenEnd = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, AngleFor(FlowGreenBandStart), bandRadius);
            var greenFigure = new PathFigure { StartPoint = greenStart };
            greenFigure.Segments.Add(new ArcSegment(
                greenEnd, new Size(bandRadius, bandRadius), 0, false, SweepDirection.Counterclockwise, true));
            GaugeCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { greenFigure }),
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 11 // äußerer Rand bleibt an der (korrigierten) Kante bei radius+14, Breite um 2 zugenommen (9->11)
            });

            // Teilstriche: 0 und 5 groß, DAZWISCHEN keine (0-5 ist stark gestaucht -
            // siehe FractionFlow). Ab 5 linear mit kleinen Zwischenstrichen alle
            // 1 GAL/HR, 10 und 15 wieder groß, bis 19 nur noch kleine Striche
            // (19 selbst bekommt keinen großen Strich, nur die Zahl).
            void DrawTick(double v, bool major)
            {
                double angle = AngleFor(v);
                // Kleine Ticks jetzt genauso weit außen wie die großen (gleicher
                // äußerer Rand bei radius+10), dafür 20% länger als vorher (14.4
                // statt 12) - ragen also entsprechend weiter nach innen.
                // Außenkante (zur Gehäuse-Mitte/zum Spalt hin) 4 weiter verlängert,
                // Innenkante bleibt unverändert.
                double inner = (major ? radius - 10 : radius - 4.4) + 4; // 4 kürzer, Außenkante bleibt
                double outer = radius + 10 + 4 + 5;
                var tickInner = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, inner);
                var tickOuter = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, outer);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = tickInner.X, Y1 = tickInner.Y, X2 = tickOuter.X, Y2 = tickOuter.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = major ? 3 : 1.5
                });
            }

            DrawTick(0, major: true);
            DrawTick(5, major: true);
            for (double v = 6; v < FlowMaxGph; v += 1) // kleine Zwischenstriche 6..18
                DrawTick(v, major: false);
            DrawTick(10, major: true);
            DrawTick(15, major: true);
            DrawTick(FlowMaxGph, major: false); // 19: nur kleiner Strich, keine große Markierung

            const double labelFontSize = 20.25;
            const double numberFontSize = labelFontSize * 0.9; // alle Zahlen 10% kleiner, außer "5"
            const double smallLabelFontSize = labelFontSize * 0.8 * 0.8; // "5": 20% kleiner, jetzt nochmal 20% kleiner (insgesamt 0,64x)

            // "0": zusätzlich 10 höher, 35 nach rechts (Gesamt: 15 rechts, 34,5 höher).
            var zeroPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, AngleFor(0), radius + 28.5);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "0", zeroPoint.X - 20 + 35 + 15 - 5, zeroPoint.Y - 4.5 - 20 - 10 - 3, numberFontSize, Brushes.White, true);

            // "19": insgesamt 5 links/9 unten (weitere 4 unten dazu).
            var maxPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, AngleFor(FlowMaxGph), radius + 28.5);
            GaugeDrawing.AddCenteredText(GaugeCanvas, FlowMaxGph.ToString("0"), maxPoint.X + 13.5 - 5 - 10, maxPoint.Y - 3 + 5 + 4 - 7 - 3, numberFontSize, Brushes.White, true);

            // "5"/"10"/"15": rechts NEBEN dem eigenen Strich (nicht radial nach
            // außen versetzt, sondern horizontal rechts vom Strichende).
            // "10": insgesamt 8 links/2 hoch (weitere 4 links dazu). "15": 4
            // links/3 hoch. "5": unverändert an Position, aber 20% kleinere Schrift.
            var flowLabelOffsets = new Dictionary<double, (double dx, double dy)>
            {
                [5.0] = (-6 - 10 + 5 - 4, 0 + 10 - 4 + 3),
                [10.0] = (-8 - 5, -2 + 4),
                [15.0] = (-4 - 5, -3)
            };
            foreach (double v in new[] { 5.0, 10, 15 })
            {
                double angle = AngleFor(v);
                // Näher zum Mittelpunkt der Skala statt weiter außen (Radius
                // deutlich kleiner als der Strich selbst, statt außerhalb davon).
                var innerPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, angle, radius - 30);
                var (dx, dy) = flowLabelOffsets[v];
                double fontSize = v == 5.0 ? smallLabelFontSize : numberFontSize;
                GaugeDrawing.AddCenteredText(GaugeCanvas, v.ToString("0"), innerPoint.X + dx, innerPoint.Y + dy, fontSize, Brushes.White, true);
            }

            // "GAL"/"HR" unten - gleiche Position/Logik wie "25°F"/"DIV" bei der
            // EGT-Skala, nur auf der rechten Seite, gleiche Schriftgröße wie die
            // Zahlen - mit einem Bruchstrich dazwischen (GAL/HR als Bruch).
            // "GAL" + Strich nochmal 2 tiefer (insgesamt +8 gegenüber dem ersten Stand).
            var divPoint = GaugeDrawing.PointOnCircle(arcCenter.X, arcCenter.Y, endAngle, radius + 28.5);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "GAL", divPoint.X, divPoint.Y - 9 + 3 + 2, labelFontSize, Brushes.White, true);
            GaugeCanvas.Children.Add(new Line
            {
                X1 = divPoint.X - 15, Y1 = divPoint.Y + 3 + 3 + 2,
                X2 = divPoint.X + 15, Y2 = divPoint.Y + 3 + 3 + 2,
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            });
            GaugeDrawing.AddCenteredText(GaugeCanvas, "HR", divPoint.X, divPoint.Y + 15, labelFontSize, Brushes.White, true);
        }

        private void DrawSideSegment(int side, double chordOffset)
        {
            double chordX = CenterX + side * chordOffset;
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
