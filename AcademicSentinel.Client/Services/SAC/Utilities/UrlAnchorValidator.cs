using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcademicSentinel.Client.Services.SAC.Utilities
{
    /// <summary>
    /// Deep-Path URL anchoring for the LMS focus rules. Closes the
    /// "same-domain cheating" gap where a student could open a second tab to
    /// any page under <c>feu.instructure.com</c> and pass the legacy host-only
    /// check.
    ///
    /// Usage:
    /// <code>
    ///   var anchor = UrlAnchorValidator.BuildAnchor(teacherProvidedExamUrl);
    ///   if (anchor == null) { /* teacher URL malformed; reject session */ }
    ///   var result = UrlAnchorValidator.Validate(anchor, studentActiveUrl);
    ///   if (result.Outcome != ValidationOutcome.Authorized) {
    ///       AddEvent(..., $"Blocked: {result.Reason}");
    ///   }
    /// </code>
    ///
    /// Tab-multiplicity ("No Multiple Tabs") is not a URL concern — two tabs
    /// can carry the exact same URL string. The integration layer should
    /// stamp an <c>AnchoredTabSignature</c> (e.g. browser HWND + capture
    /// timestamp) on first authorized hit and reject any other foreground
    /// window/tab that resolves to the same URL but a different signature.
    /// See <see cref="MakeTabSignature"/> for the recommended shape.
    /// </summary>
    public static class UrlAnchorValidator
    {
        // -----------------------------------------------------------------
        // Public surface
        // -----------------------------------------------------------------

        public enum UrlAnchorPlatform
        {
            Canvas,
            GoogleForms,
            Generic
        }

        public enum ValidationOutcome
        {
            Authorized,
            Malformed,
            WrongHost,
            WrongCorePath,
            RestrictedPathSegment,
            DisallowedSuffix
        }

        public sealed record ValidationResult(ValidationOutcome Outcome, string Reason)
        {
            public bool IsAuthorized => Outcome == ValidationOutcome.Authorized;
        }

        /// <summary>
        /// Immutable shape describing what the teacher said is "the exam".
        /// Construct once at session start via <see cref="BuildAnchor"/>;
        /// pass to <see cref="Validate"/> on every focus check.
        /// </summary>
        public sealed record AnchorSpec(
            string Host,
            string CorePath,
            UrlAnchorPlatform Platform,
            IReadOnlyList<string> AllowedSuffixes,
            IReadOnlyList<string> RestrictedPathSegments);

        // -----------------------------------------------------------------
        // Builders
        // -----------------------------------------------------------------

        // Canvas: "/courses/{id}/quizzes/{id}". The {id} segments must be
        // strictly numeric so a path like "/courses/foo/quizzes/bar" — which
        // wouldn't actually exist on Canvas — is treated as malformed.
        private static readonly Regex _canvasCoreRegex =
            new(@"^/courses/(?<courseId>\d+)/quizzes/(?<quizId>\d+)(?:/|$)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Google Forms: "/forms/d/e/{formId}/". The form id token is the
        // long opaque string Google generates per form; tolerate any
        // unreserved URL characters.
        private static readonly Regex _googleFormsCoreRegex =
            new(@"^/forms/d/e/(?<formId>[A-Za-z0-9_\-]+)(?:/|$)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Path segments that, if present anywhere in a Canvas active URL,
        // unambiguously identify a non-exam Canvas surface even when the
        // course-id portion of the path still matches.
        private static readonly string[] _canvasRestrictedSegments =
            { "/pages/", "/modules/", "/files/", "/assignments/", "/discussion_topics/", "/announcements/" };

        /// <summary>
        /// Parses the teacher-provided exam URL and produces an anchor spec
        /// suitable for repeated validation. Returns <c>null</c> if the URL
        /// is malformed, missing a host, or doesn't match a recognized
        /// platform path shape.
        /// </summary>
        public static AnchorSpec? BuildAnchor(string teacherUrl)
        {
            if (!TryParseAbsolute(teacherUrl, out var uri))
                return null;

            string host = uri.Host.ToLowerInvariant();
            string path = uri.AbsolutePath; // already URL-decoded for ASCII

            // ---- Canvas ----
            var canvasMatch = _canvasCoreRegex.Match(path);
            if (canvasMatch.Success && host.EndsWith(".instructure.com", StringComparison.OrdinalIgnoreCase))
            {
                string corePath = $"/courses/{canvasMatch.Groups["courseId"].Value}/quizzes/{canvasMatch.Groups["quizId"].Value}";
                return new AnchorSpec(
                    Host: host,
                    CorePath: corePath,
                    Platform: UrlAnchorPlatform.Canvas,
                    // /take is the in-progress endpoint, /take/questions/N
                    // surfaces during quizzes. "" tolerates the bare core
                    // path (review screen immediately after submit).
                    AllowedSuffixes: new[] { "", "/take", "/take/questions" },
                    RestrictedPathSegments: _canvasRestrictedSegments);
            }

            // ---- Google Forms ----
            var formsMatch = _googleFormsCoreRegex.Match(path);
            if (formsMatch.Success
                && string.Equals(host, "docs.google.com", StringComparison.OrdinalIgnoreCase))
            {
                string corePath = $"/forms/d/e/{formsMatch.Groups["formId"].Value}";
                return new AnchorSpec(
                    Host: host,
                    CorePath: corePath,
                    Platform: UrlAnchorPlatform.GoogleForms,
                    AllowedSuffixes: new[] { "/viewform", "/formResponse" },
                    RestrictedPathSegments: Array.Empty<string>());
            }

            // ---- Generic fallback ----
            // Trim the teacher URL down to its directory portion (drop the
            // final segment if it looks like a file/leaf). Active URL must
            // share this prefix exactly. No suffix tolerance.
            string trimmed = TrimToDirectory(path);
            return new AnchorSpec(
                Host: host,
                CorePath: trimmed,
                Platform: UrlAnchorPlatform.Generic,
                AllowedSuffixes: Array.Empty<string>(),
                RestrictedPathSegments: Array.Empty<string>());
        }

        // -----------------------------------------------------------------
        // Validator
        // -----------------------------------------------------------------

        /// <summary>
        /// Compares an active (student-foreground) URL to the teacher's anchor.
        /// </summary>
        public static ValidationResult Validate(AnchorSpec anchor, string activeUrl)
        {
            if (anchor == null)
                return new ValidationResult(ValidationOutcome.Malformed, "Anchor was not built.");

            if (!TryParseAbsolute(activeUrl, out var uri))
                return new ValidationResult(ValidationOutcome.Malformed, "Active URL could not be parsed.");

            // Rule 1 — strict host match. docs.google.com != www.google.com.
            string activeHost = uri.Host.ToLowerInvariant();
            if (!string.Equals(activeHost, anchor.Host, StringComparison.Ordinal))
                return new ValidationResult(ValidationOutcome.WrongHost,
                    $"Host '{activeHost}' does not match anchored host '{anchor.Host}'.");

            string activePath = uri.AbsolutePath;

            // Rule 3a — explicit blacklist of foreign Canvas surfaces.
            // Checked BEFORE core-path match so that a student visiting
            // /courses/110334/pages/... is rejected with the precise reason
            // even though the course id technically prefixes the path.
            foreach (var segment in anchor.RestrictedPathSegments)
            {
                if (activePath.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return new ValidationResult(ValidationOutcome.RestrictedPathSegment,
                        $"Active URL contains restricted segment '{segment}'.");
            }

            // Rule 2 — deep path anchoring. The active path must start with
            // the core path, with the next character either '/' or end-of-string.
            if (!StartsWithPathSegment(activePath, anchor.CorePath))
                return new ValidationResult(ValidationOutcome.WrongCorePath,
                    $"Active path '{activePath}' is not under anchored core path '{anchor.CorePath}'.");

            // Rule 3b — allowed-suffix gate (per-platform). Empty list means
            // any suffix beyond the core path is acceptable (generic mode).
            if (anchor.AllowedSuffixes.Count > 0)
            {
                string suffix = activePath.Length == anchor.CorePath.Length
                    ? string.Empty
                    : activePath.Substring(anchor.CorePath.Length);

                bool suffixOk = anchor.AllowedSuffixes.Any(allowed =>
                    string.Equals(suffix, allowed, StringComparison.OrdinalIgnoreCase)
                    || (allowed.Length > 0
                        && suffix.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
                        && (suffix.Length == allowed.Length || suffix[allowed.Length] == '/')));

                if (!suffixOk)
                    return new ValidationResult(ValidationOutcome.DisallowedSuffix,
                        $"Suffix '{suffix}' is not on the allowed list for {anchor.Platform}.");
            }

            return new ValidationResult(ValidationOutcome.Authorized, "OK");
        }

        // -----------------------------------------------------------------
        // Tab-identity hook (recommendation for the integration layer)
        // -----------------------------------------------------------------

        /// <summary>
        /// Builds an opaque signature uniquely identifying the originally
        /// anchored tab. The integration layer should:
        ///   1. Capture this signature on the first URL validation that
        ///      returns <see cref="ValidationOutcome.Authorized"/>.
        ///   2. On every subsequent authorized hit, re-build the signature
        ///      from the *current* foreground and compare. If different,
        ///      fire <c>WINDOW_SWITCH</c> regardless of URL match — the
        ///      student opened a second tab to the exam.
        ///
        /// The browser HWND alone is insufficient because Chromium uses one
        /// HWND per browser window and many tabs per HWND. The wall-clock
        /// timestamp guarantees the signature changes if monitoring restarts.
        /// </summary>
        public static string MakeTabSignature(IntPtr browserHwnd, long anchoredAtTicks)
            => $"{browserHwnd.ToInt64():X}:{anchoredAtTicks:X}";

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private static bool TryParseAbsolute(string url, out Uri uri)
        {
            uri = null!;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(parsed.Host)) return false;
            uri = parsed;
            return true;
        }

        /// <summary>
        /// True when <paramref name="path"/> starts with <paramref name="prefix"/>
        /// AND the character immediately after the prefix is either the end
        /// of the string or a forward slash. Prevents "/courses/1100"
        /// from matching anchored prefix "/courses/110".
        /// </summary>
        private static bool StartsWithPathSegment(string path, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return true;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return path.Length == prefix.Length || path[prefix.Length] == '/';
        }

        /// <summary>
        /// Drops the trailing path segment when it doesn't look like a
        /// directory. e.g. "/a/b/c.html" → "/a/b/"; "/a/b/" → "/a/b/".
        /// Used only by the generic fallback anchor path.
        /// </summary>
        private static string TrimToDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return "/";
            if (path.EndsWith("/", StringComparison.Ordinal)) return path;
            int lastSlash = path.LastIndexOf('/');
            return lastSlash <= 0 ? "/" : path.Substring(0, lastSlash);
        }
    }
}
