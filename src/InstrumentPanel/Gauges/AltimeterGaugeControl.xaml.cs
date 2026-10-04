// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.FlightSimulator.SimConnect;

namespace InstrumentPanel
{
    public partial class AltimeterGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 150;
        private const double CenterY = 150;
        private const double OuterRadius = 132; // entspricht dem Rand des schwarzen Zifferblatts

        // Wie viele Pixel horizontaler Mausbewegung einem Knopf-Klick (=1 INC/DEC) entsprechen.
        private const double DragPixelsPerStep = 5;
        private const double KnobDegreesPerStep = 12; // rein optische Rückmeldung, kein reales Übersetzungsverhältnis

        [StructLayout(LayoutKind.Sequential)]
        private struct AltimeterStruct
        {
            public double Altitude;   // Fuß
            public double QnhInHg;    // Zoll Quecksilbersäule
            public double QnhHpa;     // Hektopascal / Millibar
        }

        private SimConnectService _service;
        private RotateTransform _hundredsRotation;
        private RotateTransform _thousandsRotation;
        private RotateTransform _tenThousandsRotation;
        private RotateTransform _knobRotation;
        private TextBlock _qnhInHgText;
        private TextBlock _qnhHpaText;

        private bool _dragging;
        private double _dragStartX;
        private double _dragAccumulated;
        private int _wheelAccumulated; // aufsummiertes Mausrad-Delta (120 = ein Rastschritt)

        public AltimeterGaugeControl()
        {
            InitializeComponent();
            DrawGaugeFace();
        }

        public void Initialize(SimConnectService service)
        {
            _service = service;
            service.Register<AltimeterStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64),
                    ("KOHLSMAN SETTING HG", "inHg", SIMCONNECT_DATATYPE.FLOAT64),
                    ("KOHLSMAN SETTING HG", "mbar", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        /// <summary>
        /// Modulo, das auch für negative Werte im Bereich 0..period liefert
        /// (z.B. -200 ft bei 1000 ft/Umdrehung -> 800).
        /// </summary>
        private static double WrapModulo(double value, double period)
        {
            return ((value % period) + period) % period;
        }

        private void OnData(AltimeterStruct data)
        {
            // NaN/Infinity nie an die RotateTransform weiterreichen - Update überspringen.
            if (double.IsNaN(data.Altitude) || double.IsInfinity(data.Altitude)
                || double.IsNaN(data.QnhInHg) || double.IsInfinity(data.QnhInHg)
                || double.IsNaN(data.QnhHpa) || double.IsInfinity(data.QnhHpa))
                return;

            // Negative Höhen (z.B. unter dem Meeresspiegel) laufen modulo-korrekt weiter.
            double alt = data.Altitude;

            if (_hundredsRotation != null)
                _hundredsRotation.Angle = WrapModulo(alt, 1000) / 1000.0 * 360.0;
            if (_thousandsRotation != null)
                _thousandsRotation.Angle = WrapModulo(alt, 10000) / 10000.0 * 360.0;
            if (_tenThousandsRotation != null)
                _tenThousandsRotation.Angle = WrapModulo(alt, 100000) / 100000.0 * 360.0;

            if (_qnhInHgText != null)
                _qnhInHgText.Text = data.QnhInHg.ToString("00.00", CultureInfo.InvariantCulture);
            if (_qnhHpaText != null)
                _qnhHpaText.Text = Math.Round(data.QnhHpa).ToString("0000", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------
        // Drehknopf: Mausrad ODER horizontales Ziehen verstellt die QNH-Einstellung
        // im Simulator direkt (KOHLSMAN_INC / KOHLSMAN_DEC).
        // ---------------------------------------------------------------
        private void Knob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Delta akkumulieren: pro 120 (ein Rastschritt) genau ein Schritt, damit
            // hochauflösende Mäuse/Touchpads nicht zu viele Schritte auslösen.
            if (_wheelAccumulated != 0 && Math.Sign(_wheelAccumulated) != Math.Sign(e.Delta))
                _wheelAccumulated = 0; // Richtungswechsel: Rest verwerfen
            _wheelAccumulated += e.Delta;

            while (_wheelAccumulated >= 120)
            {
                SendKnobStep(1);
                _wheelAccumulated -= 120;
            }
            while (_wheelAccumulated <= -120)
            {
                SendKnobStep(-1);
                _wheelAccumulated += 120;
            }
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
            if (e.LeftButton != MouseButtonState.Pressed) { _dragging = false; return; }

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

        private void Knob_LostMouseCapture(object sender, MouseEventArgs e)
        {
            // Mausaufnahme verloren (z.B. Fensterwechsel): Drag beenden, sonst hängt er fest.
            _dragging = false;
        }

        private void SendKnobStep(int direction)
        {
            _service?.SendEvent(direction > 0 ? "KOHLSMAN_INC" : "KOHLSMAN_DEC");

            if (_knobRotation != null)
                _knobRotation.Angle -= direction * KnobDegreesPerStep; // Vorzeichen umgekehrt (Drehrichtung war verkehrt herum)
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

            // Striche alle 20ft (minor) / alle 100ft (major, mit Ziffer 0-9)
            for (int v = 0; v < 1000; v += 20)
            {
                bool isMajor = v % 100 == 0;
                double angle = v / 1000.0 * 360.0;
                double outer = OuterRadius - 4;
                double inner = isMajor ? outer - 22 : outer - 13;

                var p0 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, inner);
                var p1 = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, outer);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = p0.X,
                    Y1 = p0.Y,
                    X2 = p1.X,
                    Y2 = p1.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = isMajor ? 3 : 1.5
                });

                if (isMajor)
                {
                    var labelPoint = GaugeDrawing.PointOnCircle(CenterX, CenterY, angle, OuterRadius - 40);
                    GaugeDrawing.AddCenteredText(GaugeCanvas, (v / 100).ToString(), labelPoint.X, labelPoint.Y, 24, Brushes.White, true);
                }
            }

            var altTextPos = GaugeDrawing.PointOnCircle(CenterX, CenterY, 315, 55);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "ALT", altTextPos.X, altTextPos.Y, 13, Brushes.White, true);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "FEET", CenterX, CenterY + 60, 11, Brushes.LightGray, false);

            // QNH-Anzeige (inHg links, hPa rechts), auf Höhe der Zeiger-Achse - VOR den
            // Zeigern gezeichnet, damit die Zeiger beim Überstreichen sichtbar darüber
            // liegen (nicht die Beschriftung über den Zeigern).
            _qnhInHgText = new TextBlock
            {
                Text = "29.92",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.White
            };
            Canvas.SetLeft(_qnhInHgText, CenterX - 95);
            Canvas.SetTop(_qnhInHgText, CenterY - 9);
            GaugeCanvas.Children.Add(_qnhInHgText);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "inHg", CenterX - 70, CenterY + 12, 9, Brushes.LightGray, false);

            _qnhHpaText = new TextBlock
            {
                Text = "1013",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.White
            };
            Canvas.SetLeft(_qnhHpaText, CenterX + 62);
            Canvas.SetTop(_qnhHpaText, CenterY - 9);
            GaugeCanvas.Children.Add(_qnhHpaText);
            GaugeDrawing.AddCenteredText(GaugeCanvas, "hPa", CenterX + 80, CenterY + 12, 9, Brushes.LightGray, false);

            // Zehntausender: Dreieck doppelt so breit wie zuvor, plus dünner
            // Verbindungsstrich zum Drehpunkt, der ab dem ersten Drittel (von außen)
            // doppelt so dick wird.
            _tenThousandsRotation = new RotateTransform(0, CenterX, CenterY);

            var tenThousandsTriangle = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, CenterY - 112),
                    new Point(CenterX - 12, CenterY - 128),
                    new Point(CenterX + 12, CenterY - 128)
                }),
                Fill = Brushes.White
            };
            tenThousandsTriangle.RenderTransform = _tenThousandsRotation;
            GaugeCanvas.Children.Add(tenThousandsTriangle);

            const double stickOuterY = CenterY - 112;  // Ansatzpunkt am Dreieck
            const double stickThirdY = stickOuterY + 112.0 / 3.0; // erstes Drittel von außen
            var stickOuter = new Line
            {
                X1 = CenterX, Y1 = stickOuterY,
                X2 = CenterX, Y2 = stickThirdY,
                Stroke = Brushes.White,
                StrokeThickness = 2
            };
            var stickInner = new Line
            {
                X1 = CenterX, Y1 = stickThirdY,
                X2 = CenterX, Y2 = CenterY,
                Stroke = Brushes.White,
                StrokeThickness = 4 // doppelt so dick wie der äußere Abschnitt
            };
            stickOuter.RenderTransform = _tenThousandsRotation;
            stickInner.RenderTransform = _tenThousandsRotation;
            GaugeCanvas.Children.Add(stickOuter);
            GaugeCanvas.Children.Add(stickInner);

            // Tausender-Zeiger: Spitze mit 30°-Öffnungswinkel (spitzer als zuvor - die
            // 60° waren als Öffnungswinkel-Basis zu stumpf), Schaft verjüngt sich zum
            // Zentrum hin nochmal um 1/4 (insgesamt also stärker verjüngt als zuvor).
            const double tanHalfTip = 0.36397023; // tan(20°) - Winkel pro Seite zur Mittelachse
            const double thousandsHalfWidth = 9;
            const double thousandsBaseHalfWidth = thousandsHalfWidth * 0.75 * 0.75; // nochmal 1/4 schmaler
            const double thousandsTipY = CenterY - 78;
            double thousandsShaftTopY = thousandsTipY + thousandsHalfWidth / tanHalfTip;
            var thousands = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, thousandsTipY),
                    new Point(CenterX - thousandsHalfWidth, thousandsShaftTopY),
                    new Point(CenterX - thousandsBaseHalfWidth, CenterY - 5),
                    new Point(CenterX + thousandsBaseHalfWidth, CenterY - 5),
                    new Point(CenterX + thousandsHalfWidth, thousandsShaftTopY)
                }),
                Fill = Brushes.White
            };
            _thousandsRotation = new RotateTransform(0, CenterX, CenterY);
            thousands.RenderTransform = _thousandsRotation;
            GaugeCanvas.Children.Add(thousands);

            // Hunderter-Zeiger: gleiches Prinzip - 30°-Spitze, Basis nochmal 1/4 schmaler.
            const double hundredsHalfWidth = 6; // doppelt so dick wie ursprünglich (3)
            const double hundredsBaseHalfWidth = hundredsHalfWidth * 0.75 * 0.75; // nochmal 1/4 schmaler
            const double hundredsTipY = CenterY - 115;
            double hundredsShaftTopY = hundredsTipY + hundredsHalfWidth / tanHalfTip;
            var hundreds = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(CenterX, hundredsTipY),
                    new Point(CenterX - hundredsHalfWidth, hundredsShaftTopY),
                    new Point(CenterX - hundredsBaseHalfWidth, CenterY - 10),
                    new Point(CenterX + hundredsBaseHalfWidth, CenterY - 10),
                    new Point(CenterX + hundredsHalfWidth, hundredsShaftTopY)
                }),
                Fill = Brushes.White
            };
            _hundredsRotation = new RotateTransform(0, CenterX, CenterY);
            hundreds.RenderTransform = _hundredsRotation;
            GaugeCanvas.Children.Add(hundreds);

            // Schwarzer Deckkreis über den Zeiger-Ansätzen (verdeckt das unschöne
            // Überlappen der drei Zeigerbasen in der Mitte).
            var centerCap = new Ellipse { Width = 22, Height = 22, Fill = Brushes.Black };
            Canvas.SetLeft(centerCap, CenterX - 11);
            Canvas.SetTop(centerCap, CenterY - 11);
            GaugeCanvas.Children.Add(centerCap);

            // Drehknopf (Kollsman-Fenster-Einstellung): Innenkante des Knopfs auf halber
            // Höhe der Umrandung (Bezel-Ring zwischen Zifferblatt-Rand 132 und
            // Gehäuse-Außenkante 150, Mitte davon = 141). So groß wie die Kugel beim
            // Turn Coordinator, +20%. Per Mausrad oder horizontalem Ziehen bedienbar.
            const double knobDiameter = 48; // 20% größer als vorher (40)
            const double knobRadius = knobDiameter / 2;
            const double bezelMidRadius = 141; // Hälfte der Umrandung (zwischen 132 und 150)
            var knobCenter = GaugeDrawing.PointOnCircle(CenterX, CenterY, 135, bezelMidRadius + knobRadius); // Innenkante des Knopfs liegt auf bezelMidRadius

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

            // Gerändelte Textur: viele dünne Striche vom Zentrum bis zur Außenkante
            // rundherum (statt einem einzelnen Strich), damit die Drehung erkennbar ist.
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
                    X1 = inner.X,
                    Y1 = inner.Y,
                    X2 = outer.X,
                    Y2 = outer.Y,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1
                };
                knurlLine.RenderTransform = _knobRotation;
                GaugeCanvas.Children.Add(knurlLine);
            }

            var knobHitArea = new System.Windows.Controls.Canvas
            {
                Width = knobDiameter * 1.5,
                Height = knobDiameter * 1.5,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(knobHitArea, knobCenter.X - knobRadius * 1.5);
            Canvas.SetTop(knobHitArea, knobCenter.Y - knobRadius * 1.5);
            knobHitArea.MouseWheel += Knob_MouseWheel;
            knobHitArea.MouseLeftButtonDown += Knob_MouseLeftButtonDown;
            knobHitArea.MouseMove += Knob_MouseMove;
            knobHitArea.MouseLeftButtonUp += Knob_MouseLeftButtonUp;
            knobHitArea.LostMouseCapture += Knob_LostMouseCapture;

            GaugeCanvas.Children.Add(knobHitArea);
        }
    }
}
