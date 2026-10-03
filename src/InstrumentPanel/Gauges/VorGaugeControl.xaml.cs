using System;
using System.Windows.Threading;
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
    public partial class VorGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132;
        private const double RingInnerEdge = 95; // Ringbreite 37 - bevorzugt gegenüber der umgerechneten 96,8
        private const double FlagHeight = 27; // Höhe (Spitze-zu-Spitze), 50% größer (war 18)
        private const double FlagHalfWidth = 19.5; // Breite bleibt UNABHÄNGIG von der Höhe fest, 50% größer (war 13)
        private const double TrapFraction = 0.6; // wie weit die Trapez-Kante zur Spitze hin reicht (0=flach, 1=spitz)
        private static readonly double FlagY1 = FlagHeight / 2 - FlagHalfWidth / Math.Tan(60 * Math.PI / 180.0);
        private const double FlagOverlap = 2.25; // Vorhänge fahren um diesen Betrag weniger weit -> Überlappung beim Kreuzen, 50% größer (war 1.5)
        private const double SmallTickLength = 4; // Länge der kleinen (5°-)Ticks am Außenring
        private static readonly double FlagPanelReach = (FlagHeight / 2 + FlagY1) + TrapFraction * (FlagHeight / 2 - FlagY1) - FlagOverlap;

        // Gleiche Beschriftung wie beim Kursanzeiger (HDG).
        private static readonly Dictionary<int, string> CardLabels = new Dictionary<int, string>
        {
            { 0, "N" }, { 30, "3" }, { 60, "6" }, { 90, "E" }, { 120, "12" }, { 150, "15" },
            { 180, "S" }, { 210, "21" }, { 240, "24" }, { 270, "W" }, { 300, "30" }, { 330, "33" }
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct VorStruct
        {
            public double Obs;         // Grad (0-359), eingestellter Kurs
            public double ToFrom;      // Enum: 0=kein Signal, 1=TO, 2=FROM
            public double Cdi;         // Nadel-Auslenkung, ±127 (Vollausschlag)
            public double CdiSource;   // L-Var AS430_CDI_Source_1: 1=GPS, 0=VLOC
            public double AvionicsMasterBus1; // AVIONICS MASTER SWITCH:1: 0=aus, 1=an
            public double AvionicsMasterBus2; // AVIONICS MASTER SWITCH:2: 0=aus, 1=an
            public double AvionicsMasterNoIndex; // AVIONICS MASTER SWITCH (ohne Index): 0=aus, 1=an
            public double CircuitAvionicsOn; // CIRCUIT AVIONICS ON (ohne Index)
            public double CircuitAvionicsOn1; // CIRCUIT AVIONICS ON:1
            public double CircuitAvionicsOn2; // CIRCUIT AVIONICS ON:2
            public double LineConnectionBus1; // O:ELECTRICAL:...:ELECTRICAL_Line_BUS_1_To_AVIONICS_BUS_1_Position
            public double LineConnectionBus2; // O:ELECTRICAL:...:ELECTRICAL_Line_BUS_2_To_AVIONICS_BUS_2_Position
            public double BatteryMaster; // ELECTRICAL MASTER BATTERY: 0=aus, 1=an
            public double GeneratorSwitch1; // GENERAL ENG GENERATOR SWITCH:1: 0=aus, 1=an
        }

        private SimConnectService _service;
        private RotateTransform _cardRotation;
        private RotateTransform _needleRotation;
        private RotateTransform _obsKnobRotation;
        private bool _dragging;
        private double _dragStartX;
        private double _dragAccum;

        private const double DragPixelsPerStep = 5; // Zwischenwert (war 6, dann 3)
        private const double KnobDegreesPerStep = 1.5; // rein optische Dreh-Rückmeldung des Knopfes selbst

        public VorGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            _service = service;
            service.Register<VorStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("NAV OBS:2", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
                    ("NAV TOFROM:2", "enum", SIMCONNECT_DATATYPE.FLOAT64),
                    ("NAV CDI:2", "number", SIMCONNECT_DATATYPE.FLOAT64),
                    ("L:AS430_CDI_Source_1", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("AVIONICS MASTER SWITCH:1", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("AVIONICS MASTER SWITCH:2", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("AVIONICS MASTER SWITCH", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("CIRCUIT AVIONICS ON", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("CIRCUIT AVIONICS ON:1", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("CIRCUIT AVIONICS ON:2", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("O:ELECTRICAL:ELECTRICAL_SWITCH_AVIONICS_BUS_1:ELECTRICAL_Line_BUS_1_To_AVIONICS_BUS_1_Position", "number", SIMCONNECT_DATATYPE.FLOAT64),
                    ("O:ELECTRICAL:ELECTRICAL_SWITCH_AVIONICS_BUS_2:ELECTRICAL_Line_BUS_2_To_AVIONICS_BUS_2_Position", "number", SIMCONNECT_DATATYPE.FLOAT64),
                    ("ELECTRICAL MASTER BATTERY", "Bool", SIMCONNECT_DATATYPE.FLOAT64),
                    ("GENERAL ENG GENERATOR SWITCH:1", "Bool", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        public void UpdateStatus(string text, Brush color)
        {
        }

        private double _displayedToY = -FlagPanelReach;   // Standard: versteckt (oben)
        private double _displayedFromY = FlagPanelReach;  // Standard: versteckt (unten)
        private double? _displayedObsAngle; // geglätteter Winkel der Kompassscheibe (null = noch nicht initialisiert)
        private double? _displayedNeedleAngle; // geglätteter Winkel der CDI-Nadel (null = noch nicht initialisiert)
        private bool? _previousAnyAvionicsBusOn; // zur Erkennung des Einschalt-Moments (null = noch unbekannt)
        private DateTime? _selfTestStartTime; // gesetzt, während die Einschalt-Selbsttest-Sequenz läuft
        private DateTime? _lastUpdateTime;

        private void OnData(VorStruct data)
        {
            var now = DateTime.UtcNow;
            double deltaTimeSec = _lastUpdateTime.HasValue ? (now - _lastUpdateTime.Value).TotalSeconds : 0;
            deltaTimeSec = Math.Max(0, Math.Min(1, deltaTimeSec)); // gegen Ausreißer (z.B. nach Pause) absichern
            _lastUpdateTime = now;

            if (_cardRotation != null)
            {
                // MSFS liefert NAV OBS:2 offenbar in sprunghaften Schritten statt
                // kontinuierlich - hier geglättet, mit kürzestem Weg über die
                // 0°/360°-Grenze hinweg (z.B. 359°->1° sind nur 2°, nicht 358°).
                double targetAngle = -data.Obs;
                if (!_displayedObsAngle.HasValue)
                {
                    _displayedObsAngle = targetAngle;
                }
                else
                {
                    double diff = ((targetAngle - _displayedObsAngle.Value + 540) % 360) - 180; // kürzester Weg, -180..+180
                    const double maxDegPerSec = 180; // Höchstgeschwindigkeit der Glättung
                    double maxStep = maxDegPerSec * deltaTimeSec;
                    _displayedObsAngle += Math.Abs(diff) <= maxStep ? diff : Math.Sign(diff) * maxStep;
                }
                _cardRotation.Angle = _displayedObsAngle.Value; // Scheibe dreht sich entgegen dem eingestellten Kurs, damit dieser oben steht
            }

            // CdiSource: 1=GPS, 0=VLOC (direkt aus der L-Var gelesen).
            bool isVloc = data.CdiSource == 0;

            // Einschalt-Selbsttest: 0,5s warten, Nadel schlägt nach links aus, hält
            // 1,5s, schlägt dann voll nach rechts aus (0,5s) und geht danach sanft
            // zum echten Sollwert über (nicht mehr erzwungen zur Mitte) - erkannt am
            // Wechsel von "beide Busse aus" zu "mindestens einer an".
            // Ist bereits einer an und der zweite kommt dazu, löst das NICHT
            // nochmal aus. Zusätzlich muss Batterie ODER Generator Strom liefern -
            // sonst kein Selbsttest, selbst wenn ein Avionik-Bus-Schalter an ist.
            bool hasElectricalPower = data.BatteryMaster != 0 || data.GeneratorSwitch1 != 0;
            bool anyBusOn = hasElectricalPower && (data.LineConnectionBus1 != 0 || data.LineConnectionBus2 != 0);
            if (_previousAnyAvionicsBusOn.HasValue && !_previousAnyAvionicsBusOn.Value && anyBusOn)
                _selfTestStartTime = now;
            DebugLog.Write("[VOR-SELFTEST] bus1=" + data.AvionicsMasterBus1 + ", bus2=" + data.AvionicsMasterBus2
                + ", noIndex=" + data.AvionicsMasterNoIndex
                + ", circuitNoIdx=" + data.CircuitAvionicsOn
                + ", circuit1=" + data.CircuitAvionicsOn1
                + ", circuit2=" + data.CircuitAvionicsOn2
                + ", lineBus1=" + data.LineConnectionBus1
                + ", lineBus2=" + data.LineConnectionBus2
                + ", battery=" + data.BatteryMaster + ", generator=" + data.GeneratorSwitch1
                + ", hasElectricalPower=" + hasElectricalPower
                + ", anyBusOn=" + anyBusOn + ", previousAnyBusOn=" + _previousAnyAvionicsBusOn
                + ", selfTestActive=" + _selfTestStartTime.HasValue);
            _previousAnyAvionicsBusOn = anyBusOn;

            double? selfTestAngle = null;
            if (_selfTestStartTime.HasValue)
            {
                double elapsed = (now - _selfTestStartTime.Value).TotalSeconds;
                const double initialDelaySec = 0.5; // erst warten, Nadel bleibt in Mitte
                const double swingToLeftSec = 0.3;
                const double holdLeftSec = 1.0; // vorher 1.5
                const double swingToRightSec = 0.5; // vorher 0.3
                double t0 = initialDelaySec;
                double t1 = t0 + swingToLeftSec;
                double t2 = t1 + holdLeftSec;
                double t3 = t2 + swingToRightSec;

                // Warten, dann nach links (-35°), halten, dann nach rechts (+35°) -
                // danach übernimmt die normale Logik (geht sanft zum echten Sollwert,
                // kein erzwungenes "zur Mitte" mehr).
                if (elapsed < t0)
                    selfTestAngle = 0.0;
                else if (elapsed < t1)
                    selfTestAngle = 35.0 * ((elapsed - t0) / swingToLeftSec);
                else if (elapsed < t2)
                    selfTestAngle = 35.0;
                else if (elapsed < t3)
                    selfTestAngle = 35.0 - 70.0 * ((elapsed - t2) / swingToRightSec);
                else
                    _selfTestStartTime = null; // Sequenz beendet, normale Logik übernimmt
            }

            if (_needleRotation != null)
            {
                if (selfTestAngle.HasValue)
                {
                    // Selbsttest hat sein eigenes, exaktes Timing - hier nicht
                    // zusätzlich glätten, sonst verzerrt sich die Sequenz.
                    _displayedNeedleAngle = selfTestAngle.Value;
                }
                else
                {
                    double targetNeedleAngle = isVloc
                        ? -(Math.Max(-127, Math.Min(127, data.Cdi)) / 127.0) * 35.0 // ±127 -> ±35° (Vorzeichen umgekehrt)
                        : 0; // GPS-Modus (OFF): Nadel steht senkrecht

                    // Geglättet, max. 140°/s (=70° von Anschlag zu Anschlag in 0,5s).
                    if (!_displayedNeedleAngle.HasValue)
                    {
                        _displayedNeedleAngle = targetNeedleAngle;
                    }
                    else
                    {
                        const double maxDegPerSec = 140;
                        double maxStep = maxDegPerSec * deltaTimeSec;
                        double diff = targetNeedleAngle - _displayedNeedleAngle.Value;
                        _displayedNeedleAngle += Math.Abs(diff) <= maxStep ? diff : Math.Sign(diff) * maxStep;
                    }
                }
                _needleRotation.Angle = _displayedNeedleAngle.Value;
            }

            if (_toPanelTranslate != null && _fromPanelTranslate != null)
            {
                const double swingDurationSec = 0.25; // doppelt so schnell (war 0.5)

                // Bei GPS-Modus (nicht VLOC) zwingend OFF, unabhängig vom realen
                // NAV TOFROM:2 - MSFS kann hier noch einen veralteten TO/FROM-Wert
                // liefern, obwohl VOR2 gar nicht die CDI-Quelle ist.
                double effectiveToFrom = isVloc ? data.ToFrom : 0;

                // TO und FROM sind zwei unabhängige "Vorhänge": TO fährt von oben
                // (Y: -FlagPanelReach versteckt -> 0 deckt ab), FROM von unten
                // (Y: FlagPanelReach versteckt -> 0 deckt ab). Bei TO<->FROM-Wechsel
                // fahren beide gleichzeitig mit gleicher Geschwindigkeit - sie
                // übergeben sich nahtlos (ohne Lücke) in der Mitte, die Schraffur
                // wird dabei nie sichtbar.
                double targetToY = effectiveToFrom == 1 ? 0 : -FlagPanelReach;
                double targetFromY = effectiveToFrom == 2 ? 0 : FlagPanelReach;
                DebugLog.Write("[VOR] toFrom=" + data.ToFrom + ", cdiSource=" + data.CdiSource + ", isVloc=" + isVloc + ", effectiveToFrom=" + effectiveToFrom);

                double maxStep = FlagPanelReach / swingDurationSec * deltaTimeSec; // größtmögliche Strecke je Vorhang

                double diffTo = targetToY - _displayedToY;
                _displayedToY += Math.Abs(diffTo) <= maxStep ? diffTo : Math.Sign(diffTo) * maxStep;

                double diffFrom = targetFromY - _displayedFromY;
                _displayedFromY += Math.Abs(diffFrom) <= maxStep ? diffFrom : Math.Sign(diffFrom) * maxStep;

                _toPanelTranslate.Y = _displayedToY;
                _fromPanelTranslate.Y = _displayedFromY;
            }
        }

        // ---------------------------------------------------------------
        // Zifferblatt zeichnen (einmalig beim Start) - erster Schritt: Bezel +
        // drehbare Kompassscheibe + feste Kurspfeile.
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

            double ringInnerEdge = RingInnerEdge;

            // Fester Hintergrund (nicht drehend) - 10% heller als der Ring, sichtbar
            // im Loch des Rings (dort, wo später TO/FROM-Flagge und Nadel sitzen).
            var background = new Ellipse
            {
                Width = OuterRadius * 2,
                Height = OuterRadius * 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x39, 0x39, 0x3b))
            };
            Canvas.SetLeft(background, CenterX - OuterRadius);
            Canvas.SetTop(background, CenterY - OuterRadius);
            GaugeCanvas.Children.Add(background);

            // Drehbarer Außenring (dunkler als der Hintergrund) mit rundem Loch -
            // enthält die Kompassscheiben-Striche/Zahlen.
            var cardHost = new Canvas
            {
                Clip = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(CenterX, CenterY), OuterRadius, OuterRadius),
                    new EllipseGeometry(new Point(CenterX, CenterY), ringInnerEdge, ringInnerEdge))
            };
            var face = new Ellipse
            {
                Width = OuterRadius * 2,
                Height = OuterRadius * 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x31, 0x31, 0x33)) // 5% dunkler (war 0x34,0x34,0x36)
            };
            Canvas.SetLeft(face, CenterX - OuterRadius);
            Canvas.SetTop(face, CenterY - OuterRadius);
            cardHost.Children.Add(face);

            var cardContent = new Canvas();

            // Strich-Hierarchie wie beim HDG-Kursanzeiger übernommen (alle 5°, mit
            // großen/mittleren/kleinen Strichen), aber OHNE die roten Dreiecke.
            // Striche liegen an der Ring-Innenkante an und wachsen von dort nach
            // außen; Zahlen/Buchstaben stehen noch weiter außen auf dem Ring, aber
            // innerhalb des Randes (sonst abgeschnitten durch den Scheiben-Clip bei
            // OuterRadius).
            const double majorLen = 20;
            const double majorWidth = 4; // Breite für alle drei Strich-Größen (war 3, jetzt breiter)
            for (int deg = 0; deg < 360; deg += 5)
            {
                bool isMajor = deg % 30 == 0;
                bool isMinor = !isMajor && deg % 10 == 0;
                double inner = ringInnerEdge;
                // Länge: groß und mittel je auf 50% ihrer bisherigen Länge verkürzt,
                // klein auf 30% seiner bisherigen Länge.
                double len = isMajor ? majorLen * 0.5
                    : isMinor ? majorLen * 0.5 // jetzt gleich lang wie groß
                    : SmallTickLength;
                double outer = inner + len;
                var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, inner);
                var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, outer);
                cardContent.Children.Add(new Line
                {
                    X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = majorWidth
                });

                if (CardLabels.TryGetValue(deg, out string label))
                {
                    var labelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, deg, OuterRadius - 12);
                    GaugeDrawing.AddCenteredText(cardContent, label, labelPoint.X, labelPoint.Y, 22, Brushes.White, true, deg);
                }
            }

            cardHost.Children.Add(cardContent);
            _cardRotation = new RotateTransform(0, CenterX, CenterY);
            cardContent.RenderTransform = _cardRotation;

            // Nadel VOR dem Ring gezeichnet, damit der Ring den Drehpunkt der Nadel
            // optisch verdeckt (liegt in der Ebene dahinter).
            DrawCdiNeedle();

            GaugeCanvas.Children.Add(cardHost);

            DrawObsKnob();

            DrawToFromFlagFrame();

            // Feste gelbe Kurspfeile oben/unten (zeigen den eingestellten Kurs an
            // der Kompassscheibe, drehen NICHT mit). Spitzen liegen auf der
            // Oberkante der kleinen Ticks (ringInnerEdge + kleine-Tick-Länge).
            // Nach der Nadel gezeichnet, damit sie über der Nadel liegen (nicht
            // darunter verdeckt werden).
            const double smallTickOuterEdge = RingInnerEdge + SmallTickLength; // 95 + 4 = 99
            DrawCourseTriangle(0, tipRadius: smallTickOuterEdge);
            DrawCourseTriangle(180, tipRadius: smallTickOuterEdge, scale: 0.75);
        }

        private void DrawCdiNeedle()
        {
            // Nadel oben fest gelagert (Drehpunkt am oberen Rand des Rings-Lochs,
            // 5 weiter nach oben verschoben), hängt nach unten, schwenkt max. ±35°
            // von der Y-Achse (senkrecht) je nach NAV CDI:2 (±127 = Vollausschlag).
            double ringInnerEdge = RingInnerEdge;
            var pivot = new Point(CenterX, CenterY - ringInnerEdge - 5);
            double needleLength = 162; // Länge der CDI-Nadel

            var needle = new Line
            {
                X1 = pivot.X, Y1 = pivot.Y,
                X2 = pivot.X, Y2 = pivot.Y + needleLength,
                Stroke = Brushes.White,
                StrokeThickness = 5
            };
            _needleRotation = new RotateTransform(0, pivot.X, pivot.Y);
            needle.RenderTransform = _needleRotation;
            GaugeCanvas.Children.Add(needle);


            // Weißer Ring (nur Kontur, keine Füllung) im Mittelpunkt der Anzeige.
            // Außendurchmesser jetzt verdoppelt (12/135 statt 6/135), der weiße
            // Strich selbst bleibt in seiner Dicke unverändert (aus der
            // ursprünglichen 6/135 zu 3,5/135-Differenz berechnet).
            double centerRingOuterD = 18; // fest, statt weiter aus dem Maßstab abgeleitet
            double centerRingWidth = 2.5; // fest, statt weiter aus dem Maßstab abgeleitet
            double centerRingD = centerRingOuterD - centerRingWidth; // Mittellinie des Strichs
            var centerRing = new Ellipse
            {
                Width = centerRingD,
                Height = centerRingD,
                Stroke = Brushes.White,
                StrokeThickness = centerRingWidth,
                Fill = null
            };
            Canvas.SetLeft(centerRing, CenterX - centerRingD / 2);
            Canvas.SetTop(centerRing, CenterY - centerRingD / 2);
            GaugeCanvas.Children.Add(centerRing);

            // Punktreihe auf einer Kreisbahn um den NADEL-Drehpunkt (nicht den
            // Anzeigen-Mittelpunkt), Radius 158 (aus 80/135 gerundet). 0° = gerade
            // nach unten (Y-Achse), PointOnCircle nutzt 0°=oben, daher 180°-Basis.
            const double dotRadius = 150; // Radius der Punktreihe um den Nadel-Drehpunkt
            double dotDiameter = 6; // fest, statt weiter aus dem Ring-Innendurchmesser abgeleitet
            double centerMarkerOuterD = centerRingOuterD * 0.75;
            double centerMarkerD = centerMarkerOuterD - centerRingWidth;

            var centerMarker = new Ellipse
            {
                Width = centerMarkerD,
                Height = centerMarkerD,
                Stroke = Brushes.White,
                StrokeThickness = centerRingWidth,
                Fill = null
            };
            var centerMarkerPos = GaugeDrawing.PointOnCircle(pivot.X, pivot.Y, 180, dotRadius);
            Canvas.SetLeft(centerMarker, centerMarkerPos.X - centerMarkerD / 2);
            Canvas.SetTop(centerMarker, centerMarkerPos.Y - centerMarkerD / 2);
            GaugeCanvas.Children.Add(centerMarker);

            for (int i = 1; i <= 5; i++)
            {
                double offsetDeg = i * 5;
                foreach (double angle in new[] { 180 + offsetDeg, 180 - offsetDeg })
                {
                    var dotPos = GaugeDrawing.PointOnCircle(pivot.X, pivot.Y, angle, dotRadius);
                    var dot = new Ellipse { Width = dotDiameter, Height = dotDiameter, Fill = Brushes.White };
                    Canvas.SetLeft(dot, dotPos.X - dotDiameter / 2);
                    Canvas.SetTop(dot, dotPos.Y - dotDiameter / 2);
                    GaugeCanvas.Children.Add(dot);
                }
            }
        }

        private TranslateTransform _toPanelTranslate;
        private TranslateTransform _fromPanelTranslate;

        private void DrawToFromFlagFrame()
        {
            // Sechseck-Form: Spitzen oben/unten mit 120°-Winkel, Breite=Höhe,
            // symmetrisch. Position: rechts vom Mittelpunkt (wie im Vorbild).
            const double flagCenterX = CenterX + 65 - 10; // 10 nach links verschoben
            const double flagCenterY = CenterY - 10; // 10 nach oben verschoben
            double h = FlagHeight;
            double halfW = FlagHalfWidth;
            double y1 = h / 2 - halfW / Math.Tan(60 * Math.PI / 180.0);

            Point HexPoint(int i, double cx, double cy) => i switch
            {
                0 => new Point(cx, cy - h / 2),
                1 => new Point(cx + halfW, cy - y1),
                2 => new Point(cx + halfW, cy + y1),
                3 => new Point(cx, cy + h / 2),
                4 => new Point(cx - halfW, cy + y1),
                _ => new Point(cx - halfW, cy - y1),
            };

            var frameHex = new PointCollection();
            for (int i = 0; i < 6; i++) frameHex.Add(HexPoint(i, flagCenterX, flagCenterY));
            GaugeCanvas.Children.Add(new Polygon { Points = frameHex, Fill = Brushes.Black });

            // Sichtfenster in Sechseck-Form (fest, an der Flaggen-Position).
            var viewportFigure = new PathFigure { StartPoint = HexPoint(0, halfW, h / 2) };
            for (int i = 1; i < 6; i++) viewportFigure.Segments.Add(new LineSegment(HexPoint(i, halfW, h / 2), true));
            viewportFigure.IsClosed = true;
            var viewport = new Canvas
            {
                Width = h,
                Height = h,
                Clip = new PathGeometry(new[] { viewportFigure })
            };
            Canvas.SetLeft(viewport, flagCenterX - halfW);
            Canvas.SetTop(viewport, flagCenterY - h / 2);

            // OFF: rot-weiß gestreift, IMMER im Hintergrund sichtbar (kein eigenes
            // Verschieben) - TO/FROM schieben sich als eigenständige "Vorhänge" von
            // oben/unten darüber.
            double offStripeHeight = (2 * y1) * (2.0 / 3.0) + 7.5; // +5 skaliert auf +7.5 (50% größer)
            var offGroup = new Canvas();
            string[] stripeColors = { "Red", "White", "Red", "White", "Red" };
            double stripeSliceW = (halfW * 2) / stripeColors.Length;
            for (int i = 0; i < stripeColors.Length; i++)
            {
                var stripe = new Rectangle
                {
                    Width = stripeSliceW + 1,
                    Height = offStripeHeight,
                    Fill = stripeColors[i] == "Red" ? Brushes.Red : Brushes.White
                };
                Canvas.SetLeft(stripe, i * stripeSliceW);
                Canvas.SetTop(stripe, h / 2 - offStripeHeight / 2.0);
                offGroup.Children.Add(stripe);
            }
            viewport.Children.Add(offGroup);

            // TO: eigener "Vorhang" von OBEN - schwarzer Hintergrund mit trapezförmiger
            // Unterkante (statt einer spitzen Ecke oder einer flachen Kante) - ein
            // Kompromiss zwischen beidem. Weißes Dreieck + "TO"-Text darüber.
            // Y=0 = deckt komplett ab, Y=-h = ganz oben versteckt.
            // Reichweite des Vorhangs (Abstand Mittelpunkt->Trapez-Kante) - die
            // "versteckt"-Position muss nur bis hierhin fahren (nicht bis ganz h),
            // da der Vorhang wegen des Trapezes ohnehin nicht weiter reicht. Das
            // bringt TO/FROM beim Kreuzen näher zusammen (keine Lücke mehr).
            double panelReach = (h / 2 + y1) + TrapFraction * (h / 2 - y1) - FlagOverlap;
            Point Lerp(Point a, Point b, double t) => new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

            var toWaistRight = HexPoint(2, halfW, h / 2);
            var toApex = HexPoint(3, halfW, h / 2);
            var toWaistLeft = HexPoint(4, halfW, h / 2);
            var toTrapRight = Lerp(toWaistRight, toApex, TrapFraction);
            var toTrapLeft = Lerp(toWaistLeft, toApex, TrapFraction);

            var toGroup = new Canvas { Width = halfW * 2, Height = h };
            toGroup.Children.Add(new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(0, 0), new Point(halfW * 2, 0),
                    toWaistRight, toTrapRight, toTrapLeft, toWaistLeft
                }),
                Fill = Brushes.Black
            });
            toGroup.Children.Add(new Polygon
            {
                Points = new PointCollection(new[] { HexPoint(0, halfW, h / 2), HexPoint(1, halfW, h / 2), HexPoint(5, halfW, h / 2) }),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 3
            });
            GaugeDrawing.AddCenteredText(toGroup, "TO", halfW, h / 2 + 1.5, 12, Brushes.White, true);
            Canvas.SetLeft(toGroup, 0);
            _toPanelTranslate = new TranslateTransform(0, -panelReach); // Standard: versteckt (oben)
            toGroup.RenderTransform = _toPanelTranslate;
            viewport.Children.Add(toGroup);

            // FROM: eigener "Vorhang" von UNTEN - gleiches Prinzip, trapezförmige Oberkante.
            var fromWaistLeft = HexPoint(5, halfW, h / 2);
            var fromApex = HexPoint(0, halfW, h / 2);
            var fromWaistRight = HexPoint(1, halfW, h / 2);
            var fromTrapLeft = Lerp(fromWaistLeft, fromApex, TrapFraction);
            var fromTrapRight = Lerp(fromWaistRight, fromApex, TrapFraction);

            var fromGroup = new Canvas { Width = halfW * 2, Height = h };
            fromGroup.Children.Add(new Polygon
            {
                Points = new PointCollection(new[]
                {
                    fromWaistLeft, fromTrapLeft, fromTrapRight, fromWaistRight,
                    new Point(halfW * 2, h), new Point(0, h)
                }),
                Fill = Brushes.Black
            });
            fromGroup.Children.Add(new Polygon
            {
                Points = new PointCollection(new[] { HexPoint(3, halfW, h / 2), HexPoint(2, halfW, h / 2), HexPoint(4, halfW, h / 2) }),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 3
            });
            GaugeDrawing.AddCenteredText(fromGroup, "FR", halfW, h / 2 - 4.5, 12, Brushes.White, true);
            Canvas.SetLeft(fromGroup, 0);
            _fromPanelTranslate = new TranslateTransform(0, panelReach); // Standard: versteckt (unten)
            fromGroup.RenderTransform = _fromPanelTranslate;
            viewport.Children.Add(fromGroup);

            GaugeCanvas.Children.Add(viewport);
        }

        private void DrawObsKnob()
        {
            const double knobDiameter = 48;
            const double knobRadius = knobDiameter / 2;
            const double bezelMidRadius = 141; // Hälfte der Umrandung (zwischen 132 und 150)
            const double placementAngle = 225; // gleiche Position wie der linke HDG-Knopf
            var knobCenter = GaugeDrawing.PointOnCircle(CenterX, CenterY, placementAngle, bezelMidRadius + knobRadius);
            _obsKnobRotation = new RotateTransform(0, knobCenter.X, knobCenter.Y);

            var knobBody = new Ellipse
            {
                Width = knobDiameter,
                Height = knobDiameter,
                Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(knobBody, knobCenter.X - knobRadius);
            Canvas.SetTop(knobBody, knobCenter.Y - knobRadius);
            GaugeCanvas.Children.Add(knobBody);

            // Nur "OBS"-Text (kein Bogen/Pfeil wie beim HDG-PUSH-Knopf) - echte
            // Vermessung statt geschätzter Breite, damit es exakt zentriert ist.
            // In eine eigene Gruppe bei (0,0) gepackt (kein Canvas.Left/Top auf der
            // Gruppe selbst), damit die RotateTransform mit absoluten
            // Canvas-Koordinaten korrekt funktioniert - sonst dreht sich der Text
            // um einen falschen Punkt, weil RenderTransform sonst relativ zur
            // eigenen (verschobenen) Position des Elements arbeitet.
            var obsTextGroup = new Canvas();
            var obsText = new TextBlock
            {
                Text = "OBS",
                FontSize = knobDiameter / 2.8,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Arial Narrow"),
                FontWeight = FontWeights.Bold
            };
            obsText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(obsText, knobCenter.X - obsText.DesiredSize.Width / 2.0);
            Canvas.SetTop(obsText, knobCenter.Y - obsText.DesiredSize.Height / 2.0);
            obsTextGroup.Children.Add(obsText);
            obsTextGroup.RenderTransform = _obsKnobRotation;
            GaugeCanvas.Children.Add(obsTextGroup);

            var hitArea = new Canvas { Width = knobDiameter * 1.5, Height = knobDiameter * 1.5, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            Canvas.SetLeft(hitArea, knobCenter.X - knobRadius * 1.5);
            Canvas.SetTop(hitArea, knobCenter.Y - knobRadius * 1.5);
            hitArea.MouseWheel += (s, e) => { SendKnobStep(e.Delta > 0 ? 1 : -1); e.Handled = true; };
            hitArea.MouseLeftButtonDown += (s, e) =>
            {
                _dragging = true;
                _dragStartX = e.GetPosition(GaugeCanvas).X;
                _dragAccum = 0;
                ((UIElement)s).CaptureMouse();
                e.Handled = true;
            };
            hitArea.MouseMove += (s, e) => HandleDrag(e);
            hitArea.MouseLeftButtonUp += (s, e) => { _dragging = false; ((UIElement)s).ReleaseMouseCapture(); };
            GaugeCanvas.Children.Add(hitArea);
        }

        private void HandleDrag(MouseEventArgs e)
        {
            if (!_dragging) return;

            double x = e.GetPosition(GaugeCanvas).X;
            double delta = x - _dragStartX;
            _dragStartX = x;
            _dragAccum += delta;

            while (_dragAccum >= DragPixelsPerStep)
            {
                SendKnobStep(1);
                _dragAccum -= DragPixelsPerStep;
            }
            while (_dragAccum <= -DragPixelsPerStep)
            {
                SendKnobStep(-1);
                _dragAccum += DragPixelsPerStep;
            }
        }

        private void SendKnobStep(int direction)
        {
            _service?.SendEvent(direction > 0 ? "VOR2_OBI_INC" : "VOR2_OBI_DEC");
            if (_obsKnobRotation != null)
                _obsKnobRotation.Angle += direction * KnobDegreesPerStep;
        }

        private void DrawCourseTriangle(double angle, double tipRadius, double scale = 1.0)
        {
            const double baseTipRadius = OuterRadius - 6;
            const double baseBaseRadius = baseTipRadius - 18;

            double length = (baseTipRadius - baseBaseRadius) * scale;
            double baseRadius = tipRadius - length;
            // Gleichseitiges Dreieck: Basisbreite (Sehne) = Höhe * 2/√3. Aus der
            // gewünschten Sehnenlänge den nötigen Winkel-Halbwert zurückrechnen.
            double halfWidthDeg = Math.Asin(length * (1.0 / Math.Sqrt(3)) / baseRadius) * 180.0 / Math.PI;

            var tip = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, tipRadius);
            var baseLeft = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle - halfWidthDeg, baseRadius);
            var baseRight = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle + halfWidthDeg, baseRadius);
            GaugeCanvas.Children.Add(new Polygon
            {
                Points = new PointCollection(new[] { tip, baseLeft, baseRight }),
                Fill = new SolidColorBrush(Color.FromRgb(0xD4, 0xB8, 0x5A)) // Beige-Gelb statt reines Gelb
            });
        }
    }
}
