using System;
using System.Globalization;
using System.Windows.Data;

namespace AcademicSentinel.Client.Views.IMC.Converters
{
    /// <summary>
    /// Presentation-layer trimmer for IMC log messages.
    ///
    /// Real log entries that the SAC ships have a fixed prefix shape:
    ///
    ///   "[BehavioralMonitoring] Switched to tab: https://www.google.com/search?q=answers"
    ///   "[BehavioralMonitoring] Switched to window: Untitled1 - Notepad"
    ///
    /// The previous regex / whole-string heuristics could not find a
    /// match through that prefix and fell back to either a length-based
    /// truncation or a blunt blanket pass-through.  This rewrite parses
    /// the prefix EXPLICITLY:
    ///
    ///   * "Switched to tab: "    → take everything after the colon-space.
    ///                              If parseable as a URL, return the
    ///                              host only.  If not (e.g. "New Tab -
    ///                              Google Chrome"), fall back to the
    ///                              window-title reducer.
    ///
    ///   * "Switched to window: " → take everything after the colon-space
    ///                              and return the trailing segment of the
    ///                              last " - " / " — " / " – " split
    ///                              (the application's product name).
    ///
    /// Any string that contains NEITHER marker is returned UNTOUCHED —
    /// no ellipsis, no Substring length cap, nothing.  The DB still
    /// holds the full unedited message; this converter only ever
    /// affects rendering.
    /// </summary>
    public sealed class LogDescriptionTrimmerConverter : IValueConverter
    {
        // Exact prefixes the SAC emits.  The colon + space is part of
        // the marker so a stray word like "tab:" inside a message body
        // can't accidentally trip detection.
        private const string TabMarker    = "Switched to tab: ";
        private const string WindowMarker = "Switched to window: ";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is null) return string.Empty;
            string s = value.ToString();
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            try
            {
                // ---- TAB path -----------------------------------------------
                // Find the marker — IndexOf rather than StartsWith so a
                // bracketed module prefix like "[BehavioralMonitoring] "
                // is preserved verbatim in the rendered output.
                int tabAt = s.IndexOf(TabMarker, StringComparison.Ordinal);
                if (tabAt >= 0)
                {
                    int targetStart = tabAt + TabMarker.Length;
                    string prefix   = s.Substring(0, targetStart);
                    string target   = s.Substring(targetStart).Trim();

                    string cleaned = TryExtractHost(target, out var host)
                        ? host
                        : ReduceWindowTitle(target);

                    return prefix + cleaned;
                }

                // ---- WINDOW path --------------------------------------------
                int winAt = s.IndexOf(WindowMarker, StringComparison.Ordinal);
                if (winAt >= 0)
                {
                    int targetStart = winAt + WindowMarker.Length;
                    string prefix   = s.Substring(0, targetStart);
                    string target   = s.Substring(targetStart).Trim();

                    return prefix + ReduceWindowTitle(target);
                }

                // ---- FALLBACK -----------------------------------------------
                // Neither marker present.  Return the string exactly as
                // received — no truncation, no ellipsis, no length cap.
                return s;
            }
            catch
            {
                // Belt-and-suspenders: any parse / format exception leaves
                // the user staring at the full raw payload rather than a
                // blanked cell.  No ellipsis is ever appended.
                return s;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        // ----- internals --------------------------------------------------

        /// <summary>
        /// Attempts to parse <paramref name="candidate"/> as an absolute
        /// URL and return its <see cref="Uri.Host"/>.  Adds an implicit
        /// <c>https://</c> if the scheme is missing so a bare
        /// <c>"docs.google.com/forms/abc"</c> is still recognised.
        ///
        /// Two guards keep this from misfiring:
        ///   • The candidate must not contain whitespace.  A URL token
        ///     never has spaces, and the guard prevents the implicit-scheme
        ///     prepend from accidentally turning a window title like
        ///     "New Tab - Google Chrome" into a "valid" parse.
        ///   • The resolved host must contain at least one dot.  Without
        ///     this, the prepend would turn a single word ("Notepad")
        ///     into a syntactically valid but semantically wrong URI.
        /// </summary>
        private static bool TryExtractHost(string candidate, out string host)
        {
            host = null;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (candidate.Contains(' ')) return false;

            string parsable = candidate;
            if (!parsable.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)
             && !parsable.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                parsable = "https://" + parsable;
            }

            if (!Uri.TryCreate(parsable, UriKind.Absolute, out var uri))
                return false;
            if (string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.'))
                return false;

            host = uri.Host;
            return true;
        }

        /// <summary>
        /// Splits a Windows-style title on the LAST occurrence of " - " /
        /// " — " / " – " and returns the trailing segment (the application
        /// product name).  When the input has no separator we return the
        /// input itself, trimmed — never an ellipsis or a truncation.
        /// </summary>
        private static string ReduceWindowTitle(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input ?? string.Empty;

            string[] separators = { " - ", " — ", " – " };
            int bestIdx = -1;
            int bestSepLen = 0;
            foreach (var sep in separators)
            {
                int idx = input.LastIndexOf(sep, StringComparison.Ordinal);
                if (idx > bestIdx)
                {
                    bestIdx    = idx;
                    bestSepLen = sep.Length;
                }
            }

            if (bestIdx < 0)
                return input.Trim();

            string tail = input.Substring(bestIdx + bestSepLen).Trim();
            return string.IsNullOrWhiteSpace(tail) ? input.Trim() : tail;
        }
    }
}
