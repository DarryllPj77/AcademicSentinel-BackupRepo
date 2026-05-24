using System;
using System.Globalization;
using System.Windows.Data;

namespace AcademicSentinel.Client.Views.IMC.Converters
{
    public sealed class LogDescriptionTrimmerConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;
            string s = value.ToString();

            try
            {
                // 1. STRICT SAFETY GUARD: Only process specific window/tab switch events.
                string[] targetPrefixes = {
                    "Active Window changed to: ",
                    "Active Tab changed to: ",
                    "Switched to window: ",
                    "Switched to tab: "
                };

                string matchedPrefix = null;
                foreach (var prefix in targetPrefixes)
                {
                    if (s.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedPrefix = prefix;
                        break;
                    }
                }

                // If it's a regular log (like a cheat detection or error), RETURN IT UNTOUCHED.
                if (matchedPrefix == null) return s;

                // 2. Extract payload after the specific prefix
                int payloadStartIndex = s.IndexOf(matchedPrefix, StringComparison.OrdinalIgnoreCase) + matchedPrefix.Length;
                string payload = s.Substring(payloadStartIndex).Trim();

                // 3. Notification Badge Stripping (e.g., "(4) YouTube" -> "YouTube")
                if (payload.StartsWith("("))
                {
                    int closeParenIndex = payload.IndexOf(") ");
                    if (closeParenIndex > 0 && closeParenIndex < 8)
                    {
                        payload = payload.Substring(closeParenIndex + 2).Trim();
                    }
                }

                // 4. URL Check (Tabs) - Return just the Host
                if (!payload.Contains(" ") && payload.Contains("."))
                {
                    string urlString = payload.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? payload : "https://" + payload;
                    if (Uri.TryCreate(urlString, UriKind.Absolute, out Uri uri))
                    {
                        return uri.Host; // e.g., "feu.instructure.com"
                    }
                }

                // 5. Browser Suffix Stripping (Windows)
                string[] suffixes = { " - Google Chrome", " - Microsoft Edge", " - Mozilla Firefox", " - Brave", " - Opera", " - Personal - Microsoft Edge" };
                foreach (var suffix in suffixes)
                {
                    if (payload.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        payload = payload.Substring(0, payload.Length - suffix.Length).Trim();
                    }
                }

                // 6. Aggressive Title Separator Splitting
                string[] separators = { " - ", " | ", " — ", " – ", " : ", " > ", " • " };
                int bestIdx = -1;
                int bestSepLen = 0;

                foreach (var sep in separators)
                {
                    int idx = payload.LastIndexOf(sep, StringComparison.Ordinal);
                    if (idx > bestIdx)
                    {
                        bestIdx = idx;
                        bestSepLen = sep.Length;
                    }
                }

                if (bestIdx >= 0)
                {
                    return payload.Substring(bestIdx + bestSepLen).Trim(); // e.g., "YouTube"
                }

                return payload;
            }
            catch
            {
                // Fallback: If any string manipulation fails, return the raw log safely.
                return s;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
