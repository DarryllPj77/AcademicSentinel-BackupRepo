using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace AcademicSentinel.Client.Services.SAC.Utilities
{
    /// <summary>
    /// Reads the URL currently shown in the address bar of a Chromium /
    /// Gecko / Trident browser using UI Automation. Window titles only
    /// surface the page title — they cannot distinguish
    /// <c>docs.google.com/forms/...</c> from <c>docs.google.com/document/...</c>
    /// or <c>google.com/search</c>. The address bar exposes its value via
    /// <see cref="ValuePattern"/>, which works for every modern browser
    /// without a per-vendor extension or hook.
    ///
    /// All calls are best-effort: if UIA throws (browser still painting,
    /// elevation mismatch, etc.) we return <c>null</c> so the caller can
    /// fall back to title-based heuristics rather than crashing the poll.
    /// </summary>
    internal static class BrowserUrlReader
    {
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Common automation-id / name strings used by the address-bar edit
        // control across browsers. Matched case-insensitively. We try the
        // ID first because it's localization-stable; Name varies by locale
        // ("Address and search bar" vs "Barra de direcciones", etc.).
        private static readonly string[] _addressBarAutomationIds =
        {
            // Chromium-based (Chrome, Edge, Brave, Opera, Vivaldi, Arc, …)
            "urlbar", "url_bar", "OmniboxView",
            // Firefox / Gecko forks
            "urlbar-input",
        };

        private static readonly string[] _addressBarNameContains =
        {
            "address and search",  // English (Chromium)
            "address bar",         // generic
            "search or enter",     // older Chrome
            "search with google",  // newer Chrome
        };

        /// <summary>
        /// Attempts to read the address-bar URL for the foreground browser
        /// window. Returns <c>null</c> when the HWND isn't a browser, when
        /// no edit control is found, or when any UIA call throws.
        /// </summary>
        public static string TryGetForegroundBrowserUrl(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;

            try
            {
                var root = AutomationElement.FromHandle(hWnd);
                if (root == null) return null;

                // Two-pass walk:
                //   1) match by AutomationId (stable, locale-independent)
                //   2) fall back to Name substring
                var byId = FindFirstEdit(root, byAutomationId: true);
                string value = ReadValue(byId);
                if (!string.IsNullOrWhiteSpace(value))
                    return NormalizeBrowserUrl(value);

                var byName = FindFirstEdit(root, byAutomationId: false);
                value = ReadValue(byName);
                if (!string.IsNullOrWhiteSpace(value))
                    return NormalizeBrowserUrl(value);

                return null;
            }
            catch
            {
                // UIA is brittle — protected-process browsers, cross-bitness
                // installs, and concurrent navigations all throw. Treat
                // every failure as "URL unknown" and let the title-based
                // path provide a fallback heuristic.
                return null;
            }
        }

        // ----- internals --------------------------------------------------

        private static AutomationElement FindFirstEdit(AutomationElement root, bool byAutomationId)
        {
            // First filter to all Edit controls — much faster than walking
            // every descendant Element-by-Element.
            var editCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Edit);
            var edits = root.FindAll(TreeScope.Descendants, editCondition);
            if (edits == null || edits.Count == 0) return null;

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    string aid = (edit.Current.AutomationId ?? string.Empty).Trim();
                    string name = (edit.Current.Name ?? string.Empty).Trim();

                    if (byAutomationId)
                    {
                        foreach (var token in _addressBarAutomationIds)
                        {
                            if (aid.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                return edit;
                        }
                    }
                    else
                    {
                        foreach (var token in _addressBarNameContains)
                        {
                            if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                return edit;
                        }
                    }
                }
                catch
                {
                    // skip this element on any property-access failure
                }
            }
            return null;
        }

        private static string ReadValue(AutomationElement element)
        {
            if (element == null) return null;
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && pattern is ValuePattern valuePattern)
                {
                    return valuePattern.Current.Value;
                }
            }
            catch
            {
                // pattern unavailable — fall through
            }
            return null;
        }

        /// <summary>
        /// Browsers usually omit the protocol in the address bar
        /// ("docs.google.com/forms/..." instead of "https://..."). Restore
        /// the implicit https:// so <see cref="Uri.TryCreate"/> can parse.
        /// Strip any leading omnibox decorations (lock icons aren't in the
        /// value, but search suggestions sometimes prefix text).
        /// </summary>
        private static string NormalizeBrowserUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();

            // Reject if it doesn't look like a URL at all (user typed a
            // search query rather than a URL — the omnibox doubles as a
            // search field).
            if (s.IndexOf(' ') >= 0 && !s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!s.Contains("."))
                return null;

            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return s;

            return "https://" + s;
        }
    }
}
