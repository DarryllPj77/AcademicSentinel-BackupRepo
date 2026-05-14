using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AcademicSentinel.Client.Models;

namespace AcademicSentinel.Client.Views.IMC.Dialogs
{
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
            TxtRiskLevel.Text = string.IsNullOrWhiteSpace(student.RiskLevel)
                ? "—"
                : student.RiskLevel;

            TxtRiskLevel.Foreground = student.RiskLevel?.ToLowerInvariant() switch
            {
                "cheating"   => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)),
                "suspicious" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0)),
                _            => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32))
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
    }
}
