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
                // 1. Find the last colon-space combination.
                int lastColonIndex = s.LastIndexOf(": ", StringComparison.Ordinal);
                if (lastColonIndex < 0) return s;

                int payloadStartIndex = lastColonIndex + 2;
                string payload = s.Substring(payloadStartIndex).Trim();

                // 2. Notification Badge Stripping (e.g., "(4) YouTube" -> "YouTube")
                if (payload.StartsWith("("))
                {
                    int closeParenIndex = payload.IndexOf(") ");
                    if (closeParenIndex > 0 && closeParenIndex < 8)
                    {
                        payload = payload.Substring(closeParenIndex + 2).Trim();
                    }
                }

                // 3. URL Check (Tabs) - Return just the Host
                if (!payload.Contains(" ") && payload.Contains("."))
                {
                    string urlString = payload.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? payload : "https://" + payload;
                    if (Uri.TryCreate(urlString, UriKind.Absolute, out Uri uri))
                    {
                        return uri.Host; // e.g., "feu.instructure.com"
                    }
                }

                // 4. Browser Suffix Stripping (Windows)
                string[] suffixes = { " - Google Chrome", " - Microsoft Edge", " - Mozilla Firefox", " - Brave", " - Opera", " - Personal - Microsoft Edge" };
                foreach (var suffix in suffixes)
                {
                    if (payload.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        payload = payload.Substring(0, payload.Length - suffix.Length).Trim();
                    }
                }

                // 5. Aggressive Title Separator Splitting
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

                return payload; // Return just the cleaned app name, NO prefix.
            }
            catch
            {
                return s;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
