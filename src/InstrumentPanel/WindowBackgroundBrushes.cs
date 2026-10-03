using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;

namespace InstrumentPanel
{
    /// <summary>
    /// Erzeugt den Brush für den Fenster-Hintergrund je nach gewähltem Modus
    /// (siehe AppSettings.WindowBackgroundMode). "none" liefert einen
    /// transparenten Brush - das entspricht dem bisherigen, unveränderten
    /// Verhalten (Anzeigen schweben frei über Desktop/Sim).
    /// </summary>
    public static class WindowBackgroundBrushes
    {
        /// <summary>
        /// Alle verfügbaren Modus-Namen mit Anzeige-Text, in der Reihenfolge,
        /// in der sie im Einstellungen-Dropdown erscheinen sollen.
        /// </summary>
        public static readonly (string Mode, string Label)[] AvailableModes =
        {
            ("none", "Kein Hintergrund (transparent)"),
            ("brushed-metal", "Gebürstetes Aluminium"),
            ("carbon-fiber", "Carbon-Faser"),
            ("dark-panel", "Dunkles Panel (Vignette)"),
            ("custom", "Eigenes Bild...")
        };

        public static Brush Create(string mode, string customImagePath)
        {
            switch (mode)
            {
                case "brushed-metal":
                    return BrushedMetal();
                case "carbon-fiber":
                    return CarbonFiber();
                case "dark-panel":
                    return DarkPanel();
                case "custom":
                    return CustomImage(customImagePath);
                case "none":
                default:
                    return Brushes.Transparent;
            }
        }

        /// <summary>
        /// Feine, sich wiederholende waagerechte Streifen in Grautönen -
        /// wirkt wie gebürstetes Aluminium/Metall.
        /// </summary>
        private static Brush BrushedMetal()
        {
            var baseColor = Color.FromRgb(0x6a, 0x6c, 0x70);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(baseColor), null,
                new RectangleGeometry(new Rect(0, 0, 4, 4))));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null,
                new RectangleGeometry(new Rect(0, 0, 4, 1))));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null,
                new RectangleGeometry(new Rect(0, 2, 4, 1))));

            return new DrawingBrush
            {
                Drawing = drawing,
                Viewport = new Rect(0, 0, 4, 4),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile
            };
        }

        /// <summary>
        /// Kleines, gekacheltes Karo-Muster in zwei dunklen Grautönen -
        /// wirkt aus normaler Entfernung wie eine Carbon-Faser-Oberfläche.
        /// </summary>
        private static Brush CarbonFiber()
        {
            var dark = Color.FromRgb(0x1c, 0x1c, 0x1e);
            var darker = Color.FromRgb(0x10, 0x10, 0x12);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(dark), null,
                new RectangleGeometry(new Rect(0, 0, 8, 8))));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(darker), null,
                new RectangleGeometry(new Rect(0, 0, 4, 4))));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(darker), null,
                new RectangleGeometry(new Rect(4, 4, 4, 4))));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), null,
                new RectangleGeometry(new Rect(0, 0, 4, 1))));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), null,
                new RectangleGeometry(new Rect(4, 4, 4, 1))));

            return new DrawingBrush
            {
                Drawing = drawing,
                Viewport = new Rect(0, 0, 8, 8),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile
            };
        }

        /// <summary>
        /// Dunkles, mattes Armaturenbrett-Grau mit leichter Vignette (Mitte
        /// etwas heller als der Rand) - schlicht, ohne Textur.
        /// </summary>
        private static Brush DarkPanel()
        {
            return new RadialGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x3a, 0x3a, 0x3c), 0.0),
                    new GradientStop(Color.FromRgb(0x18, 0x18, 0x1a), 1.0)
                })
            { RadiusX = 0.9, RadiusY = 0.9 };
        }

        /// <summary>
        /// Eigenes Bild als Hintergrund. Bei fehlendem/ungültigem Pfad wird
        /// (statt eines Absturzes) einfach transparent zurückgegeben - die App
        /// bleibt so in jedem Fall startfähig.
        /// </summary>
        private static Brush CustomImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return Brushes.Transparent;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }
}
