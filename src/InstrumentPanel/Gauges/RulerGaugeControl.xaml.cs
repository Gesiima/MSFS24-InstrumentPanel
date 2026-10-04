// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows;

namespace InstrumentPanel
{
    // TEMPORÄRES Mess-Werkzeug: zwei unabhängige, einfache Lineale (jeweils
    // eigene Linie, kein gemeinsames Koordinatensystem) - eins senkrecht, eins
    // waagerecht, jedes nur in eine Richtung, mit wachsendem Strichabstand (1,
    // dann 1+2, dann 1+2+3, ... bis Lücke 10). Über das Layout neben eine andere
    // Anzeige legbar (nutzt dieselbe 300x300-Canvas-Größe wie die anderen
    // Anzeigen (OuterRadius=132-Standard), damit "1 Einheit" hier == "1
    // Einheit" dort ist - war zwischenzeitlich auf 200 stehen geblieben, als
    // Fuel & Co. von 200 auf 300 hochskaliert wurden, dadurch stimmten die
    // Einheiten nicht mehr überein).
    public partial class RulerGaugeControl : UserControl, IGauge
    {
        private const double Size = 300;
        private const double CenterX = Size / 2;
        private const double CenterY = Size / 2;
        private const int MaxGap = 10;

        // Senkrechtes Lineal: 20 Einheiten links von der senkrechten Mitte.
        private const double VerticalRulerX = CenterX - 20;
        // Ursprung so verschoben, dass der Strich für Lücke 7 (kumuliert 28) genau
        // auf der waagerechten Mitte (CenterY) liegt.
        private const double VerticalRulerOriginY = CenterY - 28;

        // Waagerechtes Lineal: eigene Y-Position, 5 Einheiten verschoben.
        private const double HorizontalRulerY = CenterY + 5;

        public RulerGaugeControl()
        {
            InitializeComponent();
            DrawRulers();
        }

        public void Initialize(SimConnectService service)
        {
            // Kein SimConnect-Bezug - reines Mess-Werkzeug.
        }

        private void DrawRulers()
        {
            GaugeCanvas.Children.Add(new Rectangle
            {
                Width = Size,
                Height = Size,
                Fill = Brushes.White
            });

            // Senkrechtes Lineal: eigene Linie, nur nach unten. = "Y-Achse"
            GaugeCanvas.Children.Add(new Line
            {
                X1 = VerticalRulerX, Y1 = 0, X2 = VerticalRulerX, Y2 = Size,
                Stroke = Brushes.Black, StrokeThickness = 1
            });
            GaugeDrawing.AddCenteredText(GaugeCanvas, "Y-Achse", VerticalRulerX, 10, 8, Brushes.Black, true);

            // Waagerechtes Lineal: eigene Linie, nur nach rechts. = "X-Achse"
            GaugeCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = HorizontalRulerY, X2 = Size, Y2 = HorizontalRulerY,
                Stroke = Brushes.Black, StrokeThickness = 1
            });
            GaugeDrawing.AddCenteredText(GaugeCanvas, "X-Achse", Size - 20, HorizontalRulerY - 8, 8, Brushes.Black, true);

            double cumulativeV = 0;
            double cumulativeH = 0;

            // "0"-Strich am jeweiligen Ursprung, damit auch der Abstand zur "1"
            // erkennbar ist.
            DrawHorizontalTick(VerticalRulerX, VerticalRulerOriginY, 0, labelOnRight: true);
            DrawVerticalTick(CenterX, HorizontalRulerY, 0, labelBelow: true);

            for (int gap = 1; gap <= MaxGap; gap++)
            {
                cumulativeV += gap;
                cumulativeH += gap;
                bool altSide = gap % 2 == 0;

                // Senkrechtes Lineal: Striche nach unten (ab eigenem Ursprung),
                // Beschriftung abwechselnd links/rechts vom Strich.
                DrawHorizontalTick(VerticalRulerX, VerticalRulerOriginY + cumulativeV, gap, labelOnRight: altSide);

                // Waagerechtes Lineal: Striche nach rechts, Beschriftung abwechselnd
                // oben/unten vom Strich.
                DrawVerticalTick(CenterX + cumulativeH, HorizontalRulerY, gap, labelBelow: altSide);
            }
        }

        // Strich steht waagerecht (für das SENKRECHTE Lineal).
        private void DrawHorizontalTick(double x, double y, int gapLabel, bool labelOnRight)
        {
            const double tickHalfLength = 5;
            GaugeCanvas.Children.Add(new Line
            {
                X1 = x - tickHalfLength, Y1 = y, X2 = x + tickHalfLength, Y2 = y,
                Stroke = Brushes.Black, StrokeThickness = 0.25
            });
            double labelX = labelOnRight ? x + tickHalfLength + 7 : x - tickHalfLength - 7;
            GaugeDrawing.AddCenteredText(GaugeCanvas, gapLabel.ToString(), labelX, y, 5.25, Brushes.Black, false);
        }

        // Strich steht senkrecht (für das WAAGERECHTE Lineal).
        private void DrawVerticalTick(double x, double y, int gapLabel, bool labelBelow)
        {
            const double tickHalfLength = 5;
            GaugeCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = y - tickHalfLength, X2 = x, Y2 = y + tickHalfLength,
                Stroke = Brushes.Black, StrokeThickness = 0.25
            });
            double labelY = labelBelow ? y + tickHalfLength + 7 : y - tickHalfLength - 7;
            GaugeDrawing.AddCenteredText(GaugeCanvas, gapLabel.ToString(), x, labelY, 5.25, Brushes.Black, false);
        }
    }
}
