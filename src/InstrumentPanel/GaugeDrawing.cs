using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace InstrumentPanel
{
    /// <summary>
    /// Reine Zeichen-Hilfsfunktionen (Mathematik/Text), die in mehreren Anzeigen
    /// identisch gebraucht werden. Bewusst NUR Werkzeuge, keine Vorgabe für Bezel/
    /// Zifferblatt-Design - jede Anzeige bleibt in ihrer optischen Gestaltung
    /// unabhängig und kann diese Methoden nutzen oder eigene Wege gehen.
    /// </summary>
    public static class GaugeDrawing
    {
        /// <summary>
        /// Punkt auf einem Kreis um (centerX, centerY). 0° = 12 Uhr/oben,
        /// im Uhrzeigersinn positiv (Standard-Konvention für Zeiger/Striche).
        /// </summary>
        public static Point PointOnCircle(double centerX, double centerY, double angleDeg, double radius)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(
                centerX + radius * Math.Sin(rad),
                centerY - radius * Math.Cos(rad));
        }

        /// <summary>
        /// Punkt auf einem Kreis, 0° = waagrecht rechts (3-Uhr-Position), im
        /// Uhrzeigersinn positiv - andere Konvention für Elemente, deren
        /// Ruhelage waagrecht ist (z.B. Turn-Coordinator-Tragfläche).
        /// </summary>
        public static Point PointOnHorizontalCircle(double centerX, double centerY, double angleDeg, double radius)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(
                centerX + radius * Math.Cos(rad),
                centerY + radius * Math.Sin(rad));
        }

        public static void AddCenteredText(Canvas canvas, string text, double centerX, double centerY,
            double fontSize, Brush brush, bool bold, double rotationDeg = 0)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = brush,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            };
            if (rotationDeg != 0)
                tb.RenderTransform = new RotateTransform(rotationDeg);
            double approxWidth = text.Length * fontSize * 0.6;
            double approxHeight = fontSize * 1.2;
            Canvas.SetLeft(tb, centerX - approxWidth / 2);
            Canvas.SetTop(tb, centerY - approxHeight / 2);
            tb.RenderTransformOrigin = new Point(0.5, 0.5);
            canvas.Children.Add(tb);
        }

        /// <summary>
        /// Zeichnet einen Farbbogen zwischen zwei WINKELN (nicht Domänenwerten wie
        /// Speed/RPM - die Umrechnung Wert→Winkel bleibt Sache der jeweiligen
        /// Anzeige, da jede ihre eigene Kalibrierung hat).
        /// </summary>
        public static void DrawArcBand(Canvas canvas, double centerX, double centerY,
            double fromAngle, double toAngle, Brush brush, double thickness, double radius)
        {
            // Segment-Anzahl proportional zur Winkel-Spanne (statt fest 40) - sonst
            // bekommen kurze Bögen unnötig viele, winzige Linienstücke ("übersampled"),
            // während lange Bögen ggf. zu grob wären. ~1°/Segment als Auflösung.
            const double degreesPerSegment = 1.0;
            int segments = Math.Max(2, (int)Math.Ceiling(Math.Abs(toAngle - fromAngle) / degreesPerSegment));
            for (int i = 0; i < segments; i++)
            {
                double t0 = fromAngle + (toAngle - fromAngle) * i / segments;
                double t1 = fromAngle + (toAngle - fromAngle) * (i + 1) / segments;

                var p0 = PointOnCircle(centerX, centerY, t0, radius);
                var p1 = PointOnCircle(centerX, centerY, t1, radius);
                canvas.Children.Add(new Line
                {
                    X1 = p0.X,
                    Y1 = p0.Y,
                    X2 = p1.X,
                    Y2 = p1.Y,
                    Stroke = brush,
                    StrokeThickness = thickness
                });
            }
        }

        public static void DrawRadialLine(Canvas canvas, double centerX, double centerY,
            double angle, Brush brush, double thickness, double innerRadius, double outerRadius)
        {
            var p0 = PointOnCircle(centerX, centerY, angle, innerRadius);
            var p1 = PointOnCircle(centerX, centerY, angle, outerRadius);
            canvas.Children.Add(new Line
            {
                X1 = p0.X,
                Y1 = p0.Y,
                X2 = p1.X,
                Y2 = p1.Y,
                Stroke = brush,
                StrokeThickness = thickness
            });
        }
    }
}
