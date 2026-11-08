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
    /// Magnetkompass in doppelter Breite (600x300), Ansicht wie bei einem Schiffs-Kuppelkompass:
    /// Die Rose ist eine nach unten aufgeweitete Trommel (Kegelstumpf), die man flach von
    /// schräg oben sieht. Vorn (nah) steht das hohe Zahlenband mit der Strichskala am unteren Rand
    /// (Zahlen steigen nach LINKS an). Oben geht die Trommel über eine abgerundete Schulter in
    /// eine nach innen abfallende Schräge über (der Kegel läuft bis zum Drehpunkt; die Mitte ist
    /// vom vorderen Band verdeckt). Die Skalenstriche der Oberseite laufen über die Schulter
    /// bis auf die Vorderseite. Auf der Schräge stehen Zahl und darunter der Buchstabe, um 180°
    /// versetzt beschriftet: hinten steht dieselbe Peilung wie vorn unter dem Steuerstrich.
    /// Ein einziger fester Steuerstrich (gelb) läuft senkrecht durch die Mitte.
    /// Die Rose folgt dem Simulator-Wert mit Flüssigkeitsdämpfung (gedämpftes Feder-System:
    /// träges Nachlaufen, leichtes Überschwingen). Die simulierten Kompassfehler
    /// (Deviation, Drehfehler, Beschleunigungsfehler) liefert die SimConnect-Variable selbst.
    ///
    /// Projektion: Punkt auf der Trommel bei Azimut phi (0 = vorn/nah, positiv = nach rechts),
    /// Höhe h, Radius r:  x = CenterX + r*sin(phi),  y = BaseY + r*cos(phi)*sin(E) - h*cos(E)
    /// (E = Blickwinkel von oben). Die Außenkontur (Profil r(q), h(q)) besteht aus Vorderfläche,
    /// Schulterbogen und Schräge; q ist die Bogenlänge, q = 0 am Beginn des Schulterbogens.
    /// </summary>
    public partial class CompassGaugeControl : UserControl, IGauge
    {
        private const double CenterX = 300;

        // Glasfenster (Zeichenbereich der Rose)
        private const double GlassLeft = 30, GlassRight = 570, GlassTop = 26, GlassBottom = 274, GlassCorner = 56;

        // Trommel (Kegelstumpf: oben schmaler, unten weiter)
        private const double DrumTopRadius = 215;    // Radius der (gedachten) scharfen Oberkante
        private const double DrumBottomRadius = 262;
        private const double DrumHeight = 110;       // Höhe der (gedachten) scharfen Oberkante
        private const double ViewElevationDeg = 9;   // flacher Blick von oben; größer = mehr Draufsicht
        private const double BaseY = 190;            // Bildschirm-Y der Trommelmitte auf Höhe 0

        // Übergang Vorderseite -> Oberseite
        private const double ShoulderRadius = 26;    // Rundung der Schulter (größer = runder)
        private const double TopSlopeDeg = 30;       // Neigung der Oberseite nach innen (steiler = mehr Platz zum Beschriften)

        private static readonly double SinE = Math.Sin(ViewElevationDeg * Math.PI / 180);
        private static readonly double CosE = Math.Cos(ViewElevationDeg * Math.PI / 180);
        private static readonly double SlopeSin = Math.Sin(TopSlopeDeg * Math.PI / 180);
        private static readonly double SlopeCos = Math.Cos(TopSlopeDeg * Math.PI / 180);

        // Dämpfung der Rose (gedämpfte Schwingung): Eigenfrequenz und Dämpfungsgrad.
        private const double NaturalFrequency = 4.0; // rad/s - kleiner = träger
        private const double DampingRatio = 0.8;     // 1 = kein Überschwingen, kleiner = mehr Überschwingen

        // Große Zahlen am vorderen Band (schmale Schrift: Breite wird um TextCondense gestaucht)
        private const double FrontLabelFont = 62;
        private const double FrontLabelW = 140, FrontLabelH = 80;
        private const double FrontLabelHeightH = 56; // Höhe (h) der Zahlenmitte (unterhalb des Schulterbeginns)
        private const double TextCondense = 0.62;

        // Oberseite: Skalenstriche (Längen als Bogenlänge q) sowie Zahl und Buchstabe auf der Schräge.
        private const double TopTickNearReach = 16;  // wie weit die Striche vor der Oberkante auf die Schulter reichen
        private const double TopTickLong = 24;       // Länge der 10°-Striche hinter der Oberkante
        private const double TopTickShort = 15;      // Länge der 5°-Striche hinter der Oberkante
        private const double TopNumberOffset = 30;   // Mitte der Zahl, gemessen ab Beginn der Schräge
        private const double TopLetterOffset = 58;   // Mitte des Buchstabens (unter der Zahl)
        private const double TopNumberFont = 26;
        private const double TopLetterFont = 19;
        private const double TopNumberW = 80, TopNumberH = 36;
        private const double TopLetterW = 40, TopLetterH = 26;

        // Profil der Außenkontur (einmalig berechnet, siehe statischen Konstruktor)
        private static readonly double T1r, T1h, T2r, T2h;     // Beginn / Ende des Schulterbogens
        private static readonly double Dir1r, Dir1h;           // Richtung der Vorderfläche (aufwärts)
        private static readonly double Dir2r, Dir2h;           // Richtung der Schräge (nach innen/unten)
        private static readonly double Theta1, ArcLength, FrontLength;
        private static readonly double CrownQ, CrownR, CrownH; // höchster Punkt der Schulter

        static CompassGaugeControl()
        {
            double dr = DrumTopRadius - DrumBottomRadius, dh = DrumHeight;
            double len = Math.Sqrt(dr * dr + dh * dh);
            Dir1r = dr / len; Dir1h = dh / len;
            Theta1 = Math.Atan2(Dir1h, Dir1r);

            double alpha = TopSlopeDeg * Math.PI / 180;
            Dir2r = -Math.Cos(alpha); Dir2h = -Math.Sin(alpha);
            double theta2 = Math.PI + alpha;

            double delta = theta2 - Theta1;
            double d = ShoulderRadius * Math.Tan(delta / 2);
            T1r = DrumTopRadius - d * Dir1r; T1h = DrumHeight - d * Dir1h;
            T2r = DrumTopRadius + d * Dir2r; T2h = DrumHeight + d * Dir2h;
            ArcLength = ShoulderRadius * delta;
            FrontLength = len - d;

            CrownQ = ShoulderRadius * (Math.PI - Theta1);   // Tangente waagrecht
            Profile(CrownQ, out CrownR, out CrownH);
        }

        // Punkt der Außenkontur bei Bogenlänge q (q < 0: Vorderfläche, 0..ArcLength: Schulter, danach Schräge).
        private static void Profile(double q, out double r, out double h)
        {
            if (q < 0)
            {
                r = T1r + q * Dir1r;
                h = T1h + q * Dir1h;
            }
            else if (q <= ArcLength)
            {
                double theta = Theta1 + q / ShoulderRadius;
                r = T1r + ShoulderRadius * (Math.Sin(theta) - Math.Sin(Theta1));
                h = T1h - ShoulderRadius * (Math.Cos(theta) - Math.Cos(Theta1));
            }
            else
            {
                r = T2r + (q - ArcLength) * Dir2r;
                h = T2h + (q - ArcLength) * Dir2h;
            }
        }

        // Richtung der Außenkontur bei Bogenlänge q (Einheitsvektor in Richtung wachsendes q).
        private static void ProfileTangent(double q, out double tr, out double th)
        {
            if (q < 0) { tr = Dir1r; th = Dir1h; }
            else if (q <= ArcLength)
            {
                double theta = Theta1 + q / ShoulderRadius;
                tr = Math.Cos(theta);
                th = Math.Sin(theta);
            }
            else { tr = Dir2r; th = Dir2h; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompassStruct
        {
            public double Heading; // WISKEY COMPASS INDICATION DEGREES
        }

        private class FrontTick
        {
            public double Angle;
            public double Length; // in h-Einheiten
            public Line Line;
        }

        private class TopTick
        {
            public double Angle;
            public double Length; // Länge hinter der Oberkante
            public Polyline Line;
        }

        private class Label
        {
            public double Angle;
            public TextBlock Text;
            public MatrixTransform Transform;
            public double Width, Height;
            public double S;      // nur Oberseite: Abstand ab Beginn der Schräge
        }

        private readonly List<FrontTick> _frontTicks = new List<FrontTick>();
        private readonly List<TopTick> _topTicks = new List<TopTick>();
        private readonly List<Label> _topLabels = new List<Label>();
        private readonly List<Label> _frontLabels = new List<Label>();

        private double _cardAngle;        // aktuelle Rosenstellung (Grad)
        private double _cardVelocity;     // Grad/s
        private double _targetAngle;
        private bool _hasData;
        private DateTime _lastFrame;
        private bool _subscribed;

        public CompassGaugeControl()
        {
            InitializeComponent();
            DrawFace();
            Loaded += (s, e) => Subscribe();
            Unloaded += (s, e) => Unsubscribe();
            UpdateMarks();
        }

        public void Initialize(SimConnectService service)
        {
            service.Register<CompassStruct>(
                new List<(string, string, SIMCONNECT_DATATYPE)>
                {
                    ("WISKEY COMPASS INDICATION DEGREES", "degrees", SIMCONNECT_DATATYPE.FLOAT64)
                },
                OnData);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private void OnData(CompassStruct data)
        {
            if (!IsFinite(data.Heading)) return;
            _targetAngle = Wrap360(data.Heading);

            if (!_hasData)
            {
                // Erster Wert: Rose steht sofort richtig, statt von 0 aus loszulaufen.
                _hasData = true;
                _cardAngle = _targetAngle;
                _cardVelocity = 0;
                UpdateMarks();
            }
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            _lastFrame = DateTime.UtcNow;
            CompositionTarget.Rendering += OnRendering;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            double dt = (now - _lastFrame).TotalSeconds;
            _lastFrame = now;
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.05); // nach Pausen (z.B. Fenster verdeckt) keine Riesenschritte

            if (_hasData)
            {
                // Gedämpfte Feder: Beschleunigung ~ Abweichung, Bremse ~ Geschwindigkeit.
                double err = ShortestDiff(_targetAngle - _cardAngle);
                double acc = NaturalFrequency * NaturalFrequency * err - 2 * DampingRatio * NaturalFrequency * _cardVelocity;
                _cardVelocity += acc * dt;
                _cardAngle = Wrap360(_cardAngle + _cardVelocity * dt);
            }
            UpdateMarks();
        }

        private static double Wrap360(double deg)
        {
            deg %= 360;
            return deg < 0 ? deg + 360 : deg;
        }

        // Kürzeste Winkeldifferenz in (-180, 180].
        private static double ShortestDiff(double deg)
        {
            deg = Wrap360(deg);
            return deg > 180 ? deg - 360 : deg;
        }

        // Azimut auf der Trommel (Radiant) für einen Kartenwinkel: Zahlen steigen vorn nach LINKS an.
        private double Phi(double markAngle) => -ShortestDiff(markAngle - _cardAngle) * Math.PI / 180.0;

        // Radius der Vorderfläche in Höhe h (linear zwischen Unterkante und Schulterbeginn).
        private static double RadiusAt(double h) =>
            DrumBottomRadius + (DrumTopRadius - DrumBottomRadius) * h / DrumHeight;

        private static Point Project(double phi, double h) => Project(RadiusAt(h), phi, h);

        private static Point Project(double r, double phi, double h) =>
            new Point(CenterX + r * Math.Sin(phi),
                      BaseY + r * Math.Cos(phi) * SinE - h * CosE);

        private static Point ProjectProfile(double q, double phi)
        {
            Profile(q, out double r, out double h);
            return Project(r, phi, h);
        }

        // Punkt auf der Schräge im Abstand "offset" hinter deren Beginn.
        private static double SlopeRadius(double offset) => T2r + offset * Dir2r;
        private static double SlopeHeight(double offset) => T2h + offset * Dir2h;

        // ---------------------------------------------------------------
        // Alles Bewegliche neu positionieren
        // ---------------------------------------------------------------
        private void UpdateMarks()
        {
            // Striche am unteren Rand des vorderen Bands (nur die zugewandte Hälfte)
            foreach (var t in _frontTicks)
            {
                double phi = Phi(t.Angle);
                double cos = Math.Cos(phi);
                if (cos < 0.05) { t.Line.Visibility = Visibility.Collapsed; continue; }

                var p0 = Project(phi, 0);
                var p1 = Project(phi, t.Length);
                t.Line.X1 = p0.X; t.Line.Y1 = p0.Y;
                t.Line.X2 = p1.X; t.Line.Y2 = p1.Y;
                t.Line.Opacity = Math.Max(0.55, Math.Pow(cos, 0.6));
                t.Line.Visibility = Visibility.Visible;
            }

            // Große Zahlen: aufrecht auf der Trommelfläche, schmale Schrift (horizontal gestaucht)
            foreach (var l in _frontLabels)
            {
                double phi = Phi(l.Angle);
                double cos = Math.Cos(phi);
                if (cos < 0.05) { l.Text.Visibility = Visibility.Collapsed; continue; }

                double sin = Math.Sin(phi);
                var pos = Project(phi, FrontLabelHeightH);
                SetLabelMatrix(l, cos * TextCondense, -sin * SinE * TextCondense, 0, CosE, pos);
                l.Text.Opacity = Math.Max(0.8, Math.Pow(cos, 0.5));
                l.Text.Visibility = Visibility.Visible;
            }

            // Skalenstriche der Oberseite: laufen über den höchsten Punkt der Schulter hinweg.
            // Gezeichnet wird nur, was dem Betrachter zugewandt ist (Flächennormale · Blickrichtung > 0).
            // Die Sichtbarkeitsgrenze wandert beim Drehen stufenlos über den Strich: vorn bleibt der Teil
            // auf der Schulter, hinten der Teil auf der Schräge, an den Seiten ist der ganze Strich sichtbar.
            foreach (var t in _topTicks)
            {
                double phi = Phi(t.Angle);
                double cos = Math.Cos(phi);
                double qLo = CrownQ - TopTickNearReach;
                double qHi = CrownQ + t.Length;

                int n = (int)Math.Ceiling((qHi - qLo) / 1.5) + 1;
                var pts = new PointCollection();
                for (int i = 0; i < n; i++)
                {
                    double q = qLo + (qHi - qLo) * i / (n - 1);
                    ProfileTangent(q, out double tr, out double th);
                    if (th * cos * CosE - tr * SinE > 0)
                        pts.Add(ProjectProfile(q, phi));
                }
                t.Line.Points = pts.Count >= 2 ? pts : new PointCollection();
            }

            // Zahlen/Buchstaben auf der Schräge: Textoberkante zeigt nach außen (zur Oberkante),
            // dadurch steht die Schrift auf der fernen Seite aufrecht und der Buchstabe (weiter
            // innen) unter der Zahl. "rechts" läuft dort gegen die Azimutrichtung. Nur die ferne
            // Hälfte ist sichtbar.
            foreach (var l in _topLabels)
            {
                double phi = Phi(l.Angle);
                double cos = Math.Cos(phi);
                if (cos > -0.1) { l.Text.Visibility = Visibility.Collapsed; continue; }

                double sin = Math.Sin(phi);
                var pos = Project(SlopeRadius(l.S), phi, SlopeHeight(l.S));
                double m21 = -sin * SlopeCos;
                double m22 = -cos * SlopeCos * SinE + SlopeSin * CosE;
                SetLabelMatrix(l, -cos * TextCondense, sin * SinE * TextCondense, m21, m22, pos);
                l.Text.Opacity = Math.Min(1, -cos * 1.6);
                l.Text.Visibility = Visibility.Visible;
            }
        }

        // Setzt die Matrix so, dass der Textmittelpunkt auf pos liegt: (m11,m12) = Bild von "rechts",
        // (m21,m22) = Bild von "unten".
        private static void SetLabelMatrix(Label l, double m11, double m12, double m21, double m22, Point pos)
        {
            double offX = pos.X - (l.Width / 2) * m11 - (l.Height / 2) * m21;
            double offY = pos.Y - (l.Width / 2) * m12 - (l.Height / 2) * m22;
            l.Transform.Matrix = new Matrix(m11, m12, m21, m22, offX, offY);
        }

        // ---------------------------------------------------------------
        // Zifferblatt (einmalig)
        // ---------------------------------------------------------------
        private void DrawFace()
        {
            // Gehäuse: gleiche Schichtung wie bei den Rundinstrumenten, nur als breites Rechteck.
            GaugeCanvas.Children.Add(new Rectangle
            {
                Width = 600, Height = 300, RadiusX = 60, RadiusY = 60,
                Fill = new LinearGradientBrush(Color.FromRgb(0x6a, 0x6a, 0x6e), Color.FromRgb(0x1a, 0x1a, 0x1c), 90)
            });

            var inner = new Rectangle { Width = 582, Height = 282, RadiusX = 52, RadiusY = 52, Fill = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2c)) };
            Canvas.SetLeft(inner, 9);
            Canvas.SetTop(inner, 9);
            GaugeCanvas.Children.Add(inner);

            var face = new Rectangle { Width = 564, Height = 264, RadiusX = 44, RadiusY = 44, Fill = Brushes.Black };
            Canvas.SetLeft(face, 18);
            Canvas.SetTop(face, 18);
            GaugeCanvas.Children.Add(face);

            // Alles Gezeichnete der Rose liegt in diesem Canvas und wird am Glasfenster abgeschnitten.
            var glass = new Canvas
            {
                Width = 600, Height = 300,
                Clip = new RectangleGeometry(new Rect(GlassLeft, GlassTop, GlassRight - GlassLeft, GlassBottom - GlassTop), GlassCorner, GlassCorner)
            };

            // Oberseite (Kegel): dunkle Ellipse auf Höhe der Schulter-Oberkante, zur Mitte hin schwärzer.
            double crownCenterY = BaseY - CrownH * CosE;
            var opening = new Ellipse
            {
                Width = CrownR * 2, Height = CrownR * 2 * SinE,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x03, 0x03, 0x04), 0.0),
                        new GradientStop(Color.FromRgb(0x0a, 0x0a, 0x0b), 0.7),
                        new GradientStop(Color.FromRgb(0x1c, 0x1c, 0x1e), 1.0)
                    })
            };
            Canvas.SetLeft(opening, CenterX - CrownR);
            Canvas.SetTop(opening, crownCenterY - CrownR * SinE);
            glass.Children.Add(opening);

            // Zahlen alle 30° auf der Schräge, bei 0/90/180/270 mit Buchstabe darunter. Beschriftet um 180°
            // versetzt: die Marke auf der fernen Seite (Kartenwinkel a) trägt die Peilung a+180, sodass
            // hinten dieselbe Zahl steht wie vorn unter dem Steuerstrich.
            for (int a = 0; a < 360; a += 30)
            {
                int bearing = (a + 180) % 360;
                var number = CreateLabel(glass, a, bearing.ToString(), TopNumberFont, TopNumberW, TopNumberH);
                number.S = TopNumberOffset;
                _topLabels.Add(number);

                string letter = CardinalLetter(bearing);
                if (letter != null)
                {
                    var l = CreateLabel(glass, a, letter, TopLetterFont, TopLetterW, TopLetterH);
                    l.S = TopLetterOffset;
                    _topLabels.Add(l);
                }
            }

            // Vorderes Band: Umriss = Oberkante der Schulter (vorn), an den Seiten dem Profil folgend
            // (dadurch laufen die oberen Ecken rund), unten die aufgeweitete Unterkante.
            var bandPoints = new PointCollection();
            for (int d = -90; d <= 90; d += 3)
                bandPoints.Add(Project(CrownR, d * Math.PI / 180.0, CrownH));
            for (double q = CrownQ; q > -FrontLength; q -= 4)
                bandPoints.Add(ProjectProfile(q, Math.PI / 2));
            for (int d = 90; d >= -90; d -= 3)
                bandPoints.Add(Project(d * Math.PI / 180.0, 0));
            for (double q = -FrontLength; q < CrownQ; q += 4)
                bandPoints.Add(ProjectProfile(q, -Math.PI / 2));
            glass.Children.Add(new Polygon
            {
                Points = bandPoints,
                Fill = new LinearGradientBrush(Color.FromRgb(0x12, 0x12, 0x14), Color.FromRgb(0x03, 0x03, 0x04), 90)
            });

            // Heller Lichtsaum an der gerundeten Oberkante und an der Unterkante.
            var crownEdge = new PointCollection();
            for (int d = -90; d <= 90; d += 3)
                crownEdge.Add(Project(CrownR, d * Math.PI / 180.0, CrownH));
            glass.Children.Add(new Polyline { Points = crownEdge, Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x48)), StrokeThickness = 1.2 });
            var bottomEdge = new PointCollection();
            for (int d = -90; d <= 90; d += 3)
                bottomEdge.Add(Project(d * Math.PI / 180.0, 0));
            glass.Children.Add(new Polyline { Points = bottomEdge, Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x64)), StrokeThickness = 1.5 });

            // Striche am unteren Rand: alle 5° kurz, alle 10° länger.
            for (int a = 0; a < 360; a += 5)
            {
                bool tenth = a % 10 == 0;
                var line = new Line { Stroke = Brushes.White, StrokeThickness = tenth ? 2.6 : 1.8 };
                glass.Children.Add(line);
                _frontTicks.Add(new FrontTick { Angle = a, Length = tenth ? 28 : 17, Line = line });
            }

            // Große Zahlen (volle Grad: 0, 30, 60, ... 330)
            for (int a = 0; a < 360; a += 30)
                _frontLabels.Add(CreateLabel(glass, a, a.ToString(), FrontLabelFont, FrontLabelW, FrontLabelH));

            // Skalenstriche der Oberseite (gleiche Teilung wie vorn: alle 5° kurz, alle 10° lang). Sie
            // liegen über dem Band, damit ihr vorderer Teil auf der gerundeten Schulter sichtbar ist.
            for (int a = 0; a < 360; a += 5)
            {
                bool tenth = a % 10 == 0;
                var line = new Polyline
                {
                    Stroke = Brushes.White,
                    StrokeThickness = tenth ? 1.5 : 1.0,
                    StrokeLineJoin = PenLineJoin.Round
                };
                glass.Children.Add(line);
                _topTicks.Add(new TopTick { Angle = a, Length = tenth ? TopTickLong : TopTickShort, Line = line });
            }

            GaugeCanvas.Children.Add(glass);

            // Abdunklung zu den Seiten (Kuppelglas), über der Rose.
            var shade = new Rectangle
            {
                Width = GlassRight - GlassLeft, Height = GlassBottom - GlassTop,
                RadiusX = GlassCorner, RadiusY = GlassCorner,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0x80, 0, 0, 0), 0.0),
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.14),
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.86),
                        new GradientStop(Color.FromArgb(0x80, 0, 0, 0), 1.0)
                    }, 0)
            };
            Canvas.SetLeft(shade, GlassLeft);
            Canvas.SetTop(shade, GlassTop);
            GaugeCanvas.Children.Add(shade);

            // Glasreflex im oberen Bereich (Kuppelglas).
            var glare = new Rectangle
            {
                Width = GlassRight - GlassLeft - 40, Height = 40,
                RadiusX = 30, RadiusY = 30,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0x1C, 255, 255, 255), Color.FromArgb(0x00, 255, 255, 255), 90)
            };
            Canvas.SetLeft(glare, GlassLeft + 20);
            Canvas.SetTop(glare, GlassTop + 4);
            GaugeCanvas.Children.Add(glare);

            // Glasrand
            var frame = new Rectangle
            {
                Width = GlassRight - GlassLeft, Height = GlassBottom - GlassTop,
                RadiusX = GlassCorner, RadiusY = GlassCorner,
                Stroke = new SolidColorBrush(Color.FromRgb(0x8a, 0x8a, 0x8e)),
                StrokeThickness = 3,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(frame, GlassLeft);
            Canvas.SetTop(frame, GlassTop);
            GaugeCanvas.Children.Add(frame);

            // Steuerstrich: EIN gelber Strich fest in der Mitte, wie beim Vorbild von der fernen
            // Oberkante der Rose bis zum unteren Fensterrand.
            double lubberTop = crownCenterY - CrownR * SinE;
            GaugeCanvas.Children.Add(new Line
            {
                X1 = CenterX, X2 = CenterX, Y1 = lubberTop, Y2 = GlassBottom - 2,
                Stroke = new SolidColorBrush(Color.FromRgb(0xEC, 0xE4, 0x1C)),
                StrokeThickness = 2.5,
                IsHitTestVisible = false
            });
        }

        private static string CardinalLetter(int bearing)
        {
            switch (bearing)
            {
                case 0: return "N";
                case 90: return "E";
                case 180: return "S";
                case 270: return "W";
                default: return null;
            }
        }

        private static Label CreateLabel(Canvas canvas, double angle, string text, double fontSize, double width, double height)
        {
            var transform = new MatrixTransform();
            var tb = new TextBlock
            {
                Text = text,
                Width = width,
                Height = height,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = fontSize,
                Foreground = Brushes.White,
                RenderTransform = transform,
                Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(tb);
            return new Label { Angle = angle, Text = tb, Transform = transform, Width = width, Height = height };
        }
    }
}
