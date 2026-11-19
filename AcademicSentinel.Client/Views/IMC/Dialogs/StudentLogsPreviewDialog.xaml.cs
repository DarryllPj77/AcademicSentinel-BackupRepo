using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AcademicSentinel.Client.Models;

namespace AcademicSentinel.Client.Views.IMC.Dialogs
{
    /// <summary>
    /// Converts a server-side UTC <see cref="DateTime"/> into the user's
    /// local time for display in the Timestamp column. Server stores
    /// timestamps via <c>DateTime.UtcNow</c>; without this converter the
    /// grid renders raw UTC, which is 8 hours behind Philippines local time.
    /// </summary>
    public sealed class UtcToLocalTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not DateTime dt) return string.Empty;
            // Treat Unspecified (the default for System.Text.Json deserialization)
            // as UTC, since that's what the server actually wrote.
            var utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            var local = utc.ToLocalTime();
            string format = parameter as string ?? "MMM dd, yyyy hh:mm:ss tt";
            return local.ToString(format, culture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class StudentLogsPreviewDialog : Window
    {
        // Master filter labels — always pinned to the top of the ComboBox.
        private const string AllEventsOption = "All Events";
        private const string AllViolationsOption = "All Violations";

        // Backing ICollectionView for the DataGrid. Filtering goes through
        // this view rather than mutating the underlying log list, so the
        // export step (and any future analytics) still see every entry.
        private ICollectionView _logsView;

        // Currently selected event-type filter. Updated by the ComboBox's
        // SelectionChanged handler and read by the filter predicate.
        private string _eventTypeFilter = AllEventsOption;

        // Master log list — kept as a field so Analytic Mode can compute
        // the event-type frequency breakdown against the full set, not
        // whatever subset is currently visible through the filter view.
        private List<SessionLogDto> _allLogs = new();

        // Analytic Mode toggle state. Default = false (chronological log
        // grid). Flipped by BtnAnalyticMode_Click.
        private bool _isAnalyticMode = false;

        public bool ExportRequested { get; private set; }

        public StudentLogsPreviewDialog(SessionStudentDto student)
        {
            InitializeComponent();

            if (student == null)
            {
                Close();
                return;
            }

            TxtStudentHeader.Text = $"{student.Name} — {student.Email}";

            var logs = student.Logs ?? new List<SessionLogDto>();
            // Newest first — DataGrid header click can still resort.
            var sortedLogs = logs.OrderByDescending(l => l.Timestamp).ToList();
            // Cache the master list — Analytic Mode groups against this
            // regardless of the current ComboBox filter, so the chart
            // always reflects the student's full session.
            _allLogs = sortedLogs;

            // --- DataGrid via ICollectionView ---
            // GetDefaultView returns a CollectionView wrapping the list;
            // setting it as ItemsSource keeps filtering AND header-click
            // sorting in sync against the same view.
            _logsView = CollectionViewSource.GetDefaultView(sortedLogs);
            _logsView.Filter = FilterByEventType;
            LogsDataGrid.ItemsSource = _logsView;

            // --- Summary chips (unchanged metrics) ---
            TxtTotalEvents.Text = sortedLogs.Count.ToString();
            TxtTotalSeverity.Text = sortedLogs.Sum(l => l.SeverityScore).ToString();

            // Legacy DB rows may still hold the raw "Cheating" / "CHEATING"
            // string from older builds. The RiskLevelDisplay.Normalize helper
            // re-maps those onto the panel-approved "Possible Dishonesty"
            // wording while preserving the original case style. New rows
            // already arrive in the panel-approved form and pass through.
            var displayRiskLevel = AcademicSentinel.Client.Services.SAC.Models.RiskLevelDisplay
                .Normalize(student.RiskLevel);

            TxtRiskLevel.Text = string.IsNullOrWhiteSpace(displayRiskLevel) ? "—" : displayRiskLevel;

            TxtRiskLevel.Foreground = displayRiskLevel.ToLowerInvariant() switch
            {
                // Both "cheating" (legacy) and "possible dishonesty" (new)
                // route to the same critical-risk red — the visual urgency
                // is preserved; only the wording has softened.
                "cheating"            => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)),
                "possible dishonesty" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)),
                "suspicious"          => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0)),
                _                     => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32))
            };

            PopulateEventTypeFilter(sortedLogs);
            // Initial render of the breakdown — uses the current filter
            // ("All Events" by default, so this is the full master list).
            // Subsequent renders are triggered by the ComboBox handler.
            PopulateViolationSummary();
        }

        // -----------------------------------------------------------------
        // ComboBox population. Order:
        //   1) "All Events"      — pinned master option, no filter.
        //   2) "All Violations"  — pinned master option, severity > 0 only.
        //   3) Specific event types that actually occurred in this student's
        //      log, sorted alphabetically. Both system entries (e.g.
        //      SYSTEM, CANVAS_RETURNED) AND violation entries appear here,
        //      so the teacher can drill down to any single offense type.
        // -----------------------------------------------------------------
        private void PopulateEventTypeFilter(IEnumerable<SessionLogDto> logs)
        {
            var distinctTypes = logs
                .Select(l => l.EventType)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = new List<string> { AllEventsOption, AllViolationsOption };
            items.AddRange(distinctTypes);

            EventTypeFilter.ItemsSource = items;
            EventTypeFilter.SelectedIndex = 0;
        }

        // -----------------------------------------------------------------
        // Violation breakdown — LINQ GroupBy against the FILTERED view.
        //
        // Enumerating _logsView (an ICollectionView) yields only the rows
        // that pass the current filter predicate, so the summary chips
        // automatically reflect whatever the ComboBox is set to:
        //   "All Events"     → every event type that occurred
        //   "All Violations" → only rows with SeverityScore > 0
        //   "{specific}"     → just that one event type
        // No predicate duplication: the same FilterByEventType used by
        // the DataGrid IS the one used here.
        // -----------------------------------------------------------------
        private void PopulateViolationSummary()
        {
            var filteredLogs = _logsView == null
                ? Enumerable.Empty<SessionLogDto>()
                : _logsView.Cast<SessionLogDto>();

            var summary = filteredLogs
                .Where(l => !string.IsNullOrWhiteSpace(l.EventType))
                .GroupBy(l => l.EventType, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ViolationSummaryRow
                {
                    EventType = g.Key.ToUpperInvariant(),
                    Count = g.Count()
                })
                .Where(row => row.Count > 0)             // strict constraint
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.EventType, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ViolationSummaryItems.ItemsSource = summary;
            ViolationSummaryEmpty.Visibility = summary.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // -----------------------------------------------------------------
        // ICollectionView filter predicate. True keeps the row visible.
        // Three resolution paths:
        //   * "All Events"     — let everything through (system entries,
        //                        informational returns, violations).
        //   * "All Violations" — only rows with SeverityScore > 0. This is
        //                        the structural definition of a violation
        //                        in our scoring engine: zero-point events
        //                        (SYSTEM, CANVAS_RETURNED, lifecycle
        //                        markers) are filtered out.
        //   * Specific type    — exact case-insensitive EventType match.
        // -----------------------------------------------------------------
        private bool FilterByEventType(object item)
        {
            if (item is not SessionLogDto log) return false;

            if (string.Equals(_eventTypeFilter, AllEventsOption, StringComparison.Ordinal))
                return true;

            if (string.Equals(_eventTypeFilter, AllViolationsOption, StringComparison.Ordinal))
                return log.SeverityScore > 0;

            return string.Equals(log.EventType, _eventTypeFilter, StringComparison.OrdinalIgnoreCase);
        }

        private void EventTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _eventTypeFilter = EventTypeFilter.SelectedItem as string ?? AllEventsOption;

            // Refresh the DataGrid view first (this is what applies the new
            // filter predicate), then recompute the breakdown chips. The
            // order matters: PopulateViolationSummary enumerates _logsView,
            // so the view must have already re-evaluated the predicate.
            _logsView?.Refresh();
            PopulateViolationSummary();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            ExportRequested = false;
            DialogResult = false;
            Close();
        }

        private void BtnExportFromPreview_Click(object sender, RoutedEventArgs e)
        {
            ExportRequested = true;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Row shape bound to the violation-breakdown ItemsControl. Kept
        /// nested to avoid leaking a thin DTO into the wider namespace.
        /// </summary>
        private sealed class ViolationSummaryRow
        {
            public string EventType { get; set; }
            public int Count { get; set; }
        }

        // -----------------------------------------------------------------
        // ANALYTIC MODE TOGGLE
        //
        // Swaps the chronological log grid for a horizontal bar chart of
        // event-type frequency. The chart uses the FULL log set (not the
        // current filter view) so the picture is always the student's
        // complete session — analytics that changed shape every time the
        // teacher tweaked the filter would be confusing.
        //
        // Visibility flips:
        //   OFF (default) → LogsDataGridContainer visible, AnalyticsPanel collapsed
        //   ON            → LogsDataGridContainer collapsed, AnalyticsPanel visible
        // The Event Type ComboBox stays visible either way — it still
        // drives the chronological view, just becomes a no-op while
        // Analytic Mode is on.
        // -----------------------------------------------------------------
        private void BtnAnalyticMode_Click(object sender, RoutedEventArgs e)
        {
            _isAnalyticMode = !_isAnalyticMode;

            if (_isAnalyticMode)
            {
                LogsDataGridContainer.Visibility = Visibility.Collapsed;
                AnalyticsPanel.Visibility = Visibility.Visible;
                BtnAnalyticMode.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(56, 142, 60));   // #388E3C — active green
                if (TxtAnalyticMode != null) TxtAnalyticMode.Text = "Standard View";

                LoadAnalytics();
            }
            else
            {
                LogsDataGridContainer.Visibility = Visibility.Visible;
                AnalyticsPanel.Visibility = Visibility.Collapsed;
                BtnAnalyticMode.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(25, 118, 210));  // #1976D2 — default blue
                if (TxtAnalyticMode != null) TxtAnalyticMode.Text = "Analytic Mode";
            }
        }

        /// <summary>
        /// Group every cached log by EventType, count occurrences, and
        /// bind to the AnalyticsItemsControl. Each row carries both Count
        /// and MaxCount so the ProgressBar can render proportional widths
        /// without a value converter. Severity is denormalized into the
        /// row so the bar can colour-code violations (>0 score) red and
        /// informational entries (0 score) green.
        /// </summary>
        private void LoadAnalytics()
        {
            // Defensive guard — the button can technically be clicked
            // before the constructor has finished caching, even though
            // the current call-graph doesn't trigger that.
            if (_allLogs == null || _allLogs.Count == 0)
            {
                AnalyticsItemsControl.ItemsSource = null;
                if (AnalyticsEmpty != null) AnalyticsEmpty.Visibility = Visibility.Visible;
                return;
            }

            var grouped = _allLogs
                .Where(l => !string.IsNullOrWhiteSpace(l.EventType))
                .GroupBy(l => l.EventType, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    EventType  = g.Key.ToUpperInvariant(),
                    Count      = g.Count(),
                    MaxSeverity = g.Max(x => x.SeverityScore)
                })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.EventType, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (grouped.Count == 0)
            {
                AnalyticsItemsControl.ItemsSource = null;
                if (AnalyticsEmpty != null) AnalyticsEmpty.Visibility = Visibility.Visible;
                return;
            }

            int maxCount = grouped[0].Count;   // already sorted desc; safe.

            // Materialize as EventAnalyticItem so the WPF binding has
            // public properties to bind to (anonymous types work in
            // collections but not great with XAML reflection).
            var rows = grouped.Select(g => new EventAnalyticItem
            {
                EventType = g.EventType,
                Count     = g.Count,
                MaxCount  = maxCount,
                // Red for actual violations (severity > 0), green for
                // informational / system events (severity == 0). The bar
                // bar visually communicates whether a high count is a
                // problem or just chatter (CANVAS_RETURNED etc.).
                BarBrush  = g.MaxSeverity > 0
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80))
            }).ToList();

            AnalyticsItemsControl.ItemsSource = rows;
            if (AnalyticsEmpty != null) AnalyticsEmpty.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Row shape bound to the AnalyticsItemsControl. Count drives the
        /// progress-bar Value, MaxCount drives Maximum so widths are
        /// proportional to the most-frequent event in the set.
        /// </summary>
        private sealed class EventAnalyticItem
        {
            public string EventType { get; set; }
            public int Count { get; set; }
            public int MaxCount { get; set; }
            public System.Windows.Media.Brush BarBrush { get; set; }
        }
    }
}
