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

        // ============================================================
        // ADDRESS-BAR CACHE (Phase 2)
        // ============================================================
        // Walking every descendant of a browser HWND on every focus poll
        // is the single most expensive UIA call we make.  Cache the
        // AutomationElement we found per HWND so subsequent reads can
        // call ValuePattern.Current.Value directly without re-walking.
        //
        // Invalidation: the address-bar element CAN go stale when a tab
        // is closed, the browser rebuilds its UI tree, or the page enters
        // a protected-content state.  Stale elements throw on access
        // (ElementNotAvailableException being the canonical case), so we
        // drop the cached entry from the catch block and fall back to the
        // full FindFirstEdit walk.  No periodic janitor is needed —
        // failing reads self-purge.
        //
        // Thread safety: the dictionary itself is plain Dictionary<,>
        // (per spec), guarded by _cacheLock.  All reads and writes go
        // through that lock so concurrent callers from the polling
        // thread and the WinEvent hook thread can't corrupt the bucket
        // chain.  The lock window is microseconds — a single Get/Set —
        // and is always released before any UIA call.
        private static readonly Dictionary<IntPtr, AutomationElement> _addressBarCache = new();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Drops every cached <see cref="AutomationElement"/> so the UIA COM
        /// proxies they wrap can be reclaimed by the GC.  Intended to be
        /// called from <see cref="DetectionService.BehavioralMonitoringService.StopMonitoring"/>
        /// (and therefore <see cref="DetectionService.BehavioralMonitoringService.Dispose"/>)
        /// so each fresh monitoring cycle starts with an empty cache and
        /// no stale references to closed browser tabs / windows.
        ///
        /// Lock-guarded for the same reason the rest of the cache is:
        /// the WinEvent hook can be calling
        /// <see cref="TryGetForegroundBrowserUrl"/> from another thread
        /// at the same moment a monitoring cycle ends.
        /// </summary>
        public static void Clear()
        {
            lock (_cacheLock)
            {
                _addressBarCache.Clear();
            }
        }

        /// <summary>
        /// Attempts to read the address-bar URL for the foreground browser
        /// window. Returns <c>null</c> when the HWND isn't a browser, when
        /// no edit control is found, or when any UIA call throws.
        ///
        /// Performance: a per-HWND cache of the resolved AutomationElement
        /// short-circuits the expensive descendant walk on hot paths.
        /// Cache misses and stale-element exceptions transparently fall
        /// back to the full <see cref="FindFirstEdit"/> traversal.
        /// </summary>
        public static string TryGetForegroundBrowserUrl(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;

            // ============================================================
            // CACHE FAST PATH
            // ============================================================
            // Snapshot the cached element under the lock, then release the
            // lock BEFORE making any UIA call.  UIA reads can take
            // milliseconds and we never want to hold the lock across one.
            AutomationElement cached;
            lock (_cacheLock)
            {
                _addressBarCache.TryGetValue(hWnd, out cached);
            }

            if (cached != null)
            {
                try
                {
                    // ValuePattern.Current.Value on an already-resolved
                    // element is the cheapest UIA read available — no
                    // tree walk, just a single property access.
                    if (cached.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                        && pattern is ValuePattern valuePattern)
                    {
                        string cachedValue = valuePattern.Current.Value;
                        if (!string.IsNullOrWhiteSpace(cachedValue))
                            return NormalizeBrowserUrl(cachedValue);
                    }
                    // Element is still alive but the URL field is empty
                    // (page mid-navigation, blank new tab, etc.).  Keep
                    // the cached element — it's still valid; just no URL
                    // to return right now.
                    return null;
                }
                catch
                {
                    // ElementNotAvailableException, COM RPC failure, or
                    // any other UIA error means our cached reference is
                    // stale (tab closed, window reconstructed, browser
                    // entered protected-content mode, etc.).  Drop the
                    // entry and fall through to a fresh walk.  We use a
                    // ReferenceEquals guard so a concurrent caller that
                    // already replaced the entry with a newer element
                    // doesn't lose theirs to our purge.
                    lock (_cacheLock)
                    {
                        if (_addressBarCache.TryGetValue(hWnd, out var current)
                            && ReferenceEquals(current, cached))
                        {
                            _addressBarCache.Remove(hWnd);
                        }
                    }
                    // Fall through to the slow path below.
                }
            }

            // ============================================================
            // SLOW PATH — full descendant walk, then repopulate the cache
            // ============================================================
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
                {
                    // Cache only on a successful end-to-end read so the
                    // cache never contains an element whose Value pattern
                    // we couldn't actually use.
                    lock (_cacheLock)
                    {
                        _addressBarCache[hWnd] = byId;
                    }
                    return NormalizeBrowserUrl(value);
                }

                var byName = FindFirstEdit(root, byAutomationId: false);
                value = ReadValue(byName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    lock (_cacheLock)
                    {
                        _addressBarCache[hWnd] = byName;
                    }
                    return NormalizeBrowserUrl(value);
                }

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
