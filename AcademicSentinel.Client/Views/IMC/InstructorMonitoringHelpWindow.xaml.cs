using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Interactive tutorial for the Instructor Monitoring Console.
    /// Renders a visual replica of the live console with clickable
    /// hit regions. Clicking a region updates the right-side info
    /// card with that region's purpose, typical use, and a concrete
    /// example scenario.
    ///
    /// Intentionally safe: this window holds no SignalR connection,
    /// no HttpClient, no SessionManager references, no monitoring
    /// state — every Region_Click only mutates local TextBlocks and
    /// region border colours. Closing the parent console
    /// auto-closes this window because the caller sets Owner=this
    /// before Show().
    /// </summary>
    public partial class InstructorMonitoringHelpWindow : Window
    {
        // Region key → (title, purpose, typical use, example). Keys
        // match the Tag="..." values on each clickable Border in
        // InstructorMonitoringHelpWindow.xaml.
        private static readonly Dictionary<string, RegionInfo> Regions = new()
        {
            ["header"] = new RegionInfo(
                Title:"Header & End Session",
                Purpose:"The green bar at the top of the console identifies the room and gives you the only safe way to end the session.",
                TypicalUse:"Glance at it to confirm you are watching the correct room. Click End Session (red) only when you are ready to finish — it cleanly closes monitoring, broadcasts the SessionEnded signal to every student, and archives the session for review.",
                Example:"After the last student submits their exam, click End Session. The console flips to 'Ended', students get the exit signal in their SAC, and the session appears under Past Sessions in the room view."),

            ["start"] = new RegionInfo(
                Title:"Start Session Monitoring",
                Purpose:"Begins live monitoring for the room — students who joined the waiting area become Active and detection events start flowing.",
                TypicalUse:"Click once at the agreed start time. The button label toggles to 'Pause' while monitoring runs, so you can pause for a class-wide announcement and resume without ending the session.",
                Example:"Three students are in the waiting area. You click Start Session Monitoring at 10:30. All three flip from 'Awaiting' to 'Connected', the timer starts, and the Global Log Feed begins recording events."),

            ["participants"] = new RegionInfo(
                Title:"Participants Panel",
                Purpose:"The roster of every student in this room with their live connection state and a per-student status dot (green = connected, yellow = idle, red = disconnected, grey = awaiting).",
                TypicalUse:"Click any student card to load them into the Selected Student panel and see their risk score in detail. The counter at the top (e.g. '2 / 3') tells you joined vs. enrolled.",
                Example:"Mid-exam you notice Student B's dot turn yellow. You click their card — the Risk Dashboard updates and the Global Log Feed already shows 'focus lost' at 10:33."),

            ["risk"] = new RegionInfo(
                Title:"Selected Student — Risk Dashboard",
                Purpose:"Deep-dive view for the student currently selected in the Participants panel: cumulative risk level (SAFE / WARN / HIGH), violation count, and severity score.",
                TypicalUse:"Use it to decide whether a particular student needs a personal check-in or a removal. The level recomputes automatically as new violations arrive.",
                Example:"Student B's panel flips from SAFE to WARN after 3 focus-lost events. You pause monitoring, message them, then resume — the panel keeps the running totals."),

            ["log"] = new RegionInfo(
                Title:"Global Log Feed",
                Purpose:"Chronological stream of every monitoring event across all students in the room — joins, focus losses, idle states, hardware status, manual actions.",
                TypicalUse:"Scan it to spot patterns the per-student dashboards hide (e.g., multiple students losing focus at the same instant suggests a network blip, not cheating).",
                Example:"At 10:33 you see three back-to-back 'focus lost' lines for different students — likely the projector flickered, not coordinated misconduct. You log it and continue."),

            ["env"] = new RegionInfo(
                Title:"Environment Profile",
                Purpose:"Read-only summary of the detection rules this room is enforcing right now — focus, idle, process, clipboard, virtualization, strict mode.",
                TypicalUse:"Glance at it before starting to confirm the rules match the exam policy. Settings are configured in Room Setup and lock once the session leaves Pending.",
                Example:"You expected strict mode to be ON for a final exam but the panel reads OFF. End the session, fix it in Room Setup, then start a fresh session — settings are locked once monitoring begins."),
        };

        // Last clicked region's border so we can clear its
        // gold-thick "selected" outline when a new region is picked.
        private Border? _highlighted;

        public InstructorMonitoringHelpWindow()
        {
            InitializeComponent();
        }

        // Single handler for every clickable region in the replica.
        // The Border's Tag identifies which entry of Regions to show
        // and the Border itself gets a gold outline so the
        // explanation card is unambiguous about which region the
        // text refers to.
        private void Region_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border) return;
            if (border.Tag is not string key) return;
            if (!Regions.TryGetValue(key, out var info)) return;

            HighlightSelection(border);
            ApplyInfo(info);
        }

        private void HighlightSelection(Border border)
        {
            if (_highlighted != null)
            {
                _highlighted.BorderBrush = Brushes.Transparent;
            }
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
            _highlighted = border;
        }

        private void ApplyInfo(RegionInfo info)
        {
            TxtRegionTitle.Text = info.Title;

            // The intro paragraph only shows on first load; replace
            // it with empty so the per-region content sits flush
            // against the title.
            TxtIntro.Text = string.Empty;

            LblPurpose.Visibility   = Visibility.Visible;
            LblTypical.Visibility   = Visibility.Visible;
            LblExample.Visibility   = Visibility.Visible;
            TxtPurpose.Visibility   = Visibility.Visible;
            TxtTypicalUse.Visibility = Visibility.Visible;
            TxtExample.Visibility   = Visibility.Visible;

            TxtPurpose.Text   = info.Purpose;
            TxtTypicalUse.Text = info.TypicalUse;
            TxtExample.Text   = info.Example;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Plain record for the region content table. Lives inside
        // the partial class so it stays scoped to this window.
        private sealed record RegionInfo(string Title, string Purpose, string TypicalUse, string Example);
    }
}
