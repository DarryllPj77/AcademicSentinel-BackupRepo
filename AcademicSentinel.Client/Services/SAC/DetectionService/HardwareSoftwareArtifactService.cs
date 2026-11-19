using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    /// <summary>
    /// HAS — Hardware/Software Artifact Scan.
    /// Per spec v3/v4/v5: "System configuration inspection" + detection of
    /// abnormal/suspicious setups. Implemented signals:
    ///
    ///   1. Debugger attached to the SAC process (IsDebuggerPresent +
    ///      CheckRemoteDebuggerPresent). Catches reverse-engineering / live
    ///      memory inspection of the running client.
    ///
    ///   2. Clock drift vs server (system-time tampering). The student's
    ///      local UtcNow is compared against /api/server/time once on start
    ///      and again every minute during the session; drifts &gt; 90 seconds
    ///      flag HAS_TIME_TAMPER (gross), 30-90s flag HAS_CLOCK_DRIFT.
    /// </summary>
    internal sealed class HardwareSoftwareArtifactService : IDisposable
    {
        public event Action<string, string> ArtifactDetected; // (eventType, description)

        private const int DriftWarningSeconds = 30;
        private const int DriftTamperSeconds = 90;

        private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(1);
        private CancellationTokenSource _cts;
        private Task _runLoop;
        private bool _isDisposed;

        public void Start()
        {
            if (_isDisposed) return;
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            _runLoop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _runLoop = null;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            ArtifactDetected = null;
        }

        private async Task RunAsync(CancellationToken token)
        {
            // Initial scan as soon as monitoring starts.
            await RunOneScanAsync(token);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_pollInterval, token);
                }
                catch (TaskCanceledException) { return; }

                await RunOneScanAsync(token);
            }
        }

        private async Task RunOneScanAsync(CancellationToken token)
        {
            try
            {
                CheckDebuggerAttached();
            }
            catch { /* never let HAS crash the SAC */ }

            try
            {
                await CheckClockDriftAsync(token);
            }
            catch { /* network blips are not violations */ }
        }

        // (1) Debugger attached to the running SAC process.
        private void CheckDebuggerAttached()
        {
            bool managed = System.Diagnostics.Debugger.IsAttached;
            bool nativeLocal = IsDebuggerPresent();

            bool nativeRemote = false;
            try
            {
                var current = System.Diagnostics.Process.GetCurrentProcess();
                CheckRemoteDebuggerPresent(current.Handle, out nativeRemote);
            }
            catch
            {
                nativeRemote = false;
            }

            if (managed || nativeLocal || nativeRemote)
            {
                string detail = $"managed={managed}, localNative={nativeLocal}, remoteNative={nativeRemote}";
                ArtifactDetected?.Invoke(
                    "HAS_DEBUGGER",
                    $"Debugger attached to SAC process ({detail}).");
            }
        }

        // (2) Clock drift vs server. Uses the JWT in SessionManager.
        private async Task CheckClockDriftAsync(CancellationToken token)
        {
            string token_jwt = SessionManager.JwtToken;
            if (string.IsNullOrWhiteSpace(token_jwt))
                return;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token_jwt);

            ServerTimeDto serverTime;
            DateTime localBefore = DateTime.UtcNow;
            try
            {
                serverTime = await client.GetFromJsonAsync<ServerTimeDto>(
                    $"{ApiEndpoints.BaseUrl}/api/server/time", token);
            }
            catch
            {
                return; // cannot reach server — bail silently
            }

            DateTime localAfter = DateTime.UtcNow;
            if (serverTime == null) return;

            // Estimate one-way latency and subtract from drift to avoid false
            // positives on slow networks.
            var roundTrip = (localAfter - localBefore).TotalSeconds;
            var oneWay = roundTrip / 2.0;

            // Compare server clock to the midpoint of the local request window.
            var midLocal = localBefore.AddSeconds(oneWay);
            var driftSeconds = Math.Abs((midLocal - serverTime.UtcNow).TotalSeconds);

            if (driftSeconds >= DriftTamperSeconds)
            {
                ArtifactDetected?.Invoke(
                    "HAS_TIME_TAMPER",
                    $"Local clock differs from server by {(int)driftSeconds}s (threshold {DriftTamperSeconds}s). Possible system-time tampering.");
            }
            else if (driftSeconds >= DriftWarningSeconds)
            {
                ArtifactDetected?.Invoke(
                    "HAS_CLOCK_DRIFT",
                    $"Local clock differs from server by {(int)driftSeconds}s (warning threshold {DriftWarningSeconds}s).");
            }
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);

        private sealed class ServerTimeDto
        {
            public DateTime UtcNow { get; set; }
            public string Iso { get; set; }
        }
    }
}
