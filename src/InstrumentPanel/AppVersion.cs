// MSFS24 InstrumentPanel - GNU AGPL v3 (siehe LICENSE)
// Entstanden in Zusammenarbeit: Coding durch Claude (Anthropic), Anforderungen und Tests durch Gesiima.
using System.Linq;
using System.Reflection;

namespace InstrumentPanel
{
    /// <summary>
    /// Liefert die Programmversion. Einzige Quelle der Wahrheit ist &lt;Version&gt; in
    /// InstrumentPanel.csproj (SemVer) - der Build schreibt sie in die Assembly.
    /// </summary>
    public static class AppVersion
    {
        /// <summary>Versionsnummer, z.B. "1.0.0" (ohne Build-Metadaten wie "+commit").</summary>
        public static string Current { get; } = Read();

        private static string Read()
        {
            var assembly = typeof(AppVersion).Assembly;
            var info = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            string version = info != null ? info.InformationalVersion : assembly.GetName().Version.ToString(3);
            int plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }
    }
}
