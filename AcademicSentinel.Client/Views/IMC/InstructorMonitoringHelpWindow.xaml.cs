using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Interactive tutorial for the Instructor Monitoring Console.
    /// Visually mirrors the live LiveSessionMonitoringWindow layout
    /// (green header, sidebar with countdown/participants/start,
    /// log-feed pane on the right) but every "real" control is a
    /// plain Border — none of them can trigger session actions.
    /// Each labelled region in the replica fires Region_Click which
    /// opens a small MonitoringHelpInfoWindow popup that shows the
    /// region's Description, Purpose, and Scenario.
    ///
    /// Safe by construction: no SignalR, no HttpClient, no
    /// SessionManager reference — only local UI handlers.
    /// </summary>
    public partial class InstructorMonitoringHelpWindow : Window
    {
        // Region key → full content shown in the popup. Keys must
        // match the Tag="..." values on the clickable Borders in
        // InstructorMonitoringHelpWindow.xaml.
        private static readonly Dictionary<string, RegionInfo> Regions = new()
        {
            ["header"] = new RegionInfo(
                Title:       "Header & End Session",
                IconKind:    PackIconKind.SchoolOutline,
                Description: "The green bar at the top of the console identifies the room you're monitoring and exposes the only safe way to finish a session.",
                Purpose:     "Lets you confirm the correct room at a glance and gives you the End Session control. End Session cleanly closes monitoring, broadcasts the SessionEnded signal to every student, and archives the session for review.",
                Scenario:    "After the last student submits their exam, click End Session. The console flips to 'Ended', students get the exit signal in their SAC, and the session appears under Past Sessions in the room view."),

            ["countdown"] = new RegionInfo(
                Title:       "Session Countdown",
                IconKind:    PackIconKind.TimerOutline,
                Description: "Status indicator showing whether the room is currently running a countdown before a session starts, or sitting idle (NOT ACTIVE).",
                Purpose:     "Gives you a clear visual that the room is waiting / counting down / actively monitoring without you having to read the status bar.",
                Scenario:    "You scheduled the exam to start at 10:30. At 10:29 the countdown chip flips from 'NOT ACTIVE' to the live count so you know the session is about to flip Active."),

            ["viewParticipants"] = new RegionInfo(
                Title:       "View Participants List",
                IconKind:    PackIconKind.AccountMultipleOutline,
                Description: "Opens the detailed roster window for the room — full list of enrolled students with their join, leave, and risk history.",
                Purpose:     "Shortcut to the deeper roster view when the compact Participants panel below isn't enough (e.g. you need contact info or history across sessions).",
                Scenario:    "A student says they joined but you don't see them in the panel. Open the participants list to confirm enrollment status and pull up their last join timestamp."),

            ["start"] = new RegionInfo(
                Title:       "Start Session Monitoring",
                IconKind:    PackIconKind.Play,
                Description: "The green call-to-action button that flips the room from 'waiting' to live monitoring.",
                Purpose:     "Begins detection-event capture for every student in the waiting area. The button toggles to Pause once monitoring is live so you can pause for an announcement without ending the session.",
                Scenario:    "Three students are in the waiting area. You click Start Session Monitoring at 10:30. All three flip from 'Awaiting' to 'Connected', the timer starts, and the Global Log Feed begins recording events."),

            ["participants"] = new RegionInfo(
                Title:       "Participants Panel",
                IconKind:    PackIconKind.AccountMultiple,
                Description: "Live roster of every student in the room with their connection state, plus filter tabs (Taking / Done / Finished) and a search box.",
                Purpose:     "Real-time view of who's in, who's idle, and who's disconnected. Click any student card to load them into the Selected Student dashboard for detail.",
                Scenario:    "Mid-exam you notice Student B's status dot turn yellow. You click their card — the Risk Dashboard updates and the Global Log Feed shows 'focus lost' at 10:33, which gives you the context you need to decide whether to message them."),

            ["log"] = new RegionInfo(
                Title:       "Global Log Feed",
                IconKind:    PackIconKind.FormatListBulleted,
                Description: "Chronological stream of every monitoring event across all students in the room — joins, focus losses, idle states, hardware status, manual actions. Filterable via the dropdown on the right.",
                Purpose:     "Scan it to spot patterns the per-student dashboards hide. For example, multiple simultaneous 'focus lost' lines across students usually means a network blip, not coordinated misconduct.",
                Scenario:    "At 10:33 you see three back-to-back 'focus lost' lines for different students. You check the timestamps — same second — and conclude the projector flickered. You note it in your records and continue without flagging anyone."),
        };

        public InstructorMonitoringHelpWindow()
        {
            InitializeComponent();
        }

        // Single handler for every clickable region in the replica.
        // The Border's Tag identifies which entry of Regions to
        // surface, and the popup opens as an owned modal so it
        // stays above the replica until the user dismisses it.
        private void Region_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border) return;
            if (border.Tag is not string key) return;
            if (!Regions.TryGetValue(key, out var info)) return;

            var popup = new MonitoringHelpInfoWindow(
                info.Title,
                info.IconKind,
                info.Description,
                info.Purpose,
                info.Scenario)
            {
                Owner = this
            };
            popup.ShowDialog();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Strongly-typed record for the region content table.
        // Internal to this window so it stays scoped here.
        private sealed record RegionInfo(
            string       Title,
            PackIconKind IconKind,
            string       Description,
            string       Purpose,
            string       Scenario);
    }
}
