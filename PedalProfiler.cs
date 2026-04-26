// Pedal Profiler – per-song audio CPU load monitor
//
// How it works
// ─────────────────────────────────────────────────────────────────────────────
// This is a CONTROL machine (void Work() signature).  ReBuzz calls control
// machines FIRST in every audio buffer, before all generators and effects.
//
// That ordering gives us a free timing bracket:
//
//   ┌──────────────────────────────────────────────────────────────────┐
//   │  [Our Work() START] ──── trivial housekeeping ──[Our Work() END] │
//   │  [Gen1 Work()] [FX1 Work()] [Gen2 Work()] … [FX_N Work()]        │
//   │  [Our Work() START again] ←── next audio buffer                  │
//   └──────────────────────────────────────────────────────────────────┘
//
//   otherMs  = thisStart − lastEnd   ← time ALL other machines took
//   periodMs = thisStart − lastStart ← full audio-buffer period
//   cpuLoad% = otherMs / periodMs × 100
//
// The "budget" line in the GUI sits at periodMs — crossing it means dropouts.
//
// Per-machine breakdown is not directly measurable without hooking into
// ReBuzz internals; instead the machine list (enumerated safely on the UI
// thread) lets you mute individual machines and watch the load drop in real
// time — the classic "binary-search mute" profiling workflow.
//
// Thread-safety contract
// ─────────────────────────────────────────────────────────────────────────────
//  • Audio thread  : writes timing fields + does an atomic reference swap of
//                    the immutable ProfileSnapshot.  Never touches Song.Machines.
//  • UI thread     : reads the snapshot via DispatcherTimer + enumerates
//                    Song.Machines (safe because no audio thread access).
//  • Volatile keyword on the snapshot field ensures the UI thread always sees
//    the latest value (no stale cache line).
//
// Build
// ─────────────────────────────────────────────────────────────────────────────
//   dotnet build PedalProfiler.csproj -c Release
//
// Output DLL goes straight into <ReBuzz>\Gear\Generators\ (control machines
// appear in the Generators browser tab).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalProfiler
{
    // =========================================================================
    //  Spike record — one captured dropout event
    // =========================================================================
    public struct SpikeRecord
    {
        public double SpikeMs;      // how long other machines took
        public double BudgetMs;     // estimated hardware deadline at the time
        public double ElapsedSec;   // seconds since profiler was loaded
        public int    Bpm;

        public double Ratio => BudgetMs > 0 ? SpikeMs / BudgetMs : 0;
    }

    // =========================================================================
    //  Immutable snapshot — swapped atomically (volatile reference write/read)
    // =========================================================================
    public sealed class ProfileSnapshot
    {
        public double CpuPct      { get; init; }
        public double PeakCpuPct  { get; init; }
        public double AvgOtherMs  { get; init; }
        public double PeakOtherMs { get; init; }
        public double PeriodMs    { get; init; }
        public double BudgetMs    { get; init; }
        public int    SampleRate  { get; init; }
        public bool   IsValid     { get; init; }
        // Dropout stats
        public int           DropoutsInWindow { get; init; }  // near-limit buffers this window
        public long          TotalDropouts    { get; init; }  // all-time count
        public long          TotalBuffers     { get; init; }  // all-time buffer count
        // Spike log
        public SpikeRecord[] RecentSpikes     { get; init; }  // last N spikes, newest first
        // Song context
        public int Bpm { get; init; }
    }

    // =========================================================================
    //  Machine declaration
    // =========================================================================
    [MachineDecl(Name = "Pedal Profiler", ShortName = "Profiler", Author = "WDE",
                 MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public class ProfilerMachine : IBuzzMachine
    {
        IBuzzMachineHost _host;
        IBuzz Buzz => _host?.Machine?.Graph?.Buzz;

        // ── Timing fields (written ONLY on the audio thread) ──────────────────
        long _lastWorkStart;    // Stopwatch ticks at entry to Work()
        long _lastWorkEnd;      // Stopwatch ticks at exit  of Work()

        // ── Parameter: averaging window size ─────────────────────────────────
        // At least one parameter is required by the ReBuzz managed machine loader.
        // This one is genuinely useful — it controls how many audio buffers are
        // averaged before the snapshot is published to the GUI.
        // 16 = snappy (~0.1 s), 128 = smooth (~0.75 s).
        [ParameterDecl(Name = "Window", Description = "Averaging window in audio buffers (16–128)",
                       MinValue = 16, MaxValue = 128, DefValue = 64)]
        public int Window { get; set; } = 64;

        // Accumulator for the rolling measurement window
        long _sumOtherTicks;
        long _sumPeriodTicks;
        long _maxOtherTicks;
        long _maxOtherPeriodTicks;
        int  _windowCount;
        int  _windowDropouts;       // near-limit buffers in this window

        // All-time counters
        long _totalDropouts;
        long _totalBuffers;

        // Baseline period estimator — low-pass filter of periodTicks.
        // Represents the hardware audio callback interval (the true deadline).
        // Comparing otherTicks against THIS (not the current buffer's periodTicks)
        // correctly detects spikes even when the OS delivers the callback late.
        long _baselinePeriodTicks;

        // Elapsed time since profiler was loaded (for spike timestamps)
        readonly long _loadTimestamp = Stopwatch.GetTimestamp();

        // Spike ring buffer
        const int SPIKE_SLOTS = 8;
        readonly SpikeRecord[] _spikeRing  = new SpikeRecord[SPIKE_SLOTS];
        int                    _spikeWrite = 0;
        int                    _spikeCount = 0;

        // Cooldown: suppress further spike captures for this many ticks after one fires
        const long SPIKE_COOLDOWN_MS = 500;
        long _lastSpikeTicks = 0;

        // Snapshot (immutable; reference swap is atomic in .NET)
        volatile ProfileSnapshot _snapshot = new ProfileSnapshot();

        // Cached sample rate — written by audio thread (int write is atomic)
        volatile int _cachedSampleRate = 44100;

        // ── Constructor ───────────────────────────────────────────────────────
        public ProfilerMachine(IBuzzMachineHost host) => _host = host;

        public IBuzzMachineHost Host
        {
            get => _host;
            set => _host = value;
        }

        // ── Public snapshot accessor (read by UI thread) ───────────────────────
        public ProfileSnapshot Snapshot => _snapshot;
        public int CachedSampleRate     => _cachedSampleRate;

        // ── Control machine Work() — called FIRST each audio buffer ───────────
        //    void + no parameters = control machine (see §2 in the dev notes)
        public void Work()
        {
            long nowTicks = Stopwatch.GetTimestamp();

            // MasterInfo is valid in Work()
            var mi = _host?.MasterInfo;
            int sr  = mi?.SamplesPerSec ?? 44100;
            int bpm = mi?.BeatsPerMin   ?? 120;
            _cachedSampleRate = sr;

            if (_lastWorkEnd != 0 && _lastWorkStart != 0)
            {
                long otherTicks  = nowTicks - _lastWorkEnd;
                long periodTicks = nowTicks - _lastWorkStart;

                // Discard wildly implausible values (system sleep/wake, clock jump > 10× period)
                if (periodTicks <= 0 || otherTicks < 0 || otherTicks > periodTicks * 10)
                {
                    _lastWorkStart = nowTicks;
                    _lastWorkEnd   = Stopwatch.GetTimestamp();
                    return;
                }

                // Update low-pass baseline estimate of the hardware callback period.
                // Time constant ≈ 128 buffers.  This gives us a stable reference
                // that isn't distorted by individual late/early callbacks.
                if (_baselinePeriodTicks == 0)
                    _baselinePeriodTicks = periodTicks;
                else
                    _baselinePeriodTicks = (_baselinePeriodTicks * 127 + periodTicks) / 128;

                _totalBuffers++;

                // Near-dropout: other machines used ≥90% of the baseline deadline
                bool isNearDropout = _baselinePeriodTicks > 0 &&
                                     otherTicks >= _baselinePeriodTicks * 90 / 100;
                if (isNearDropout) { _windowDropouts++; _totalDropouts++; }

                // Spike: >150% of baseline, with 500ms cooldown between captures
                long cooldownTicks = (long)(SPIKE_COOLDOWN_MS * Stopwatch.Frequency / 1000);
                bool cooledDown    = (nowTicks - _lastSpikeTicks) >= cooldownTicks;

                if (_baselinePeriodTicks > 0 && otherTicks > _baselinePeriodTicks * 3 / 2 && cooledDown)
                {
                    double freq     = Stopwatch.Frequency;
                    double spikeMs  = otherTicks          / freq * 1000.0;
                    double budgetMs = _baselinePeriodTicks / freq * 1000.0;
                    double elapsed  = (nowTicks - _loadTimestamp) / freq;
                    _spikeRing[_spikeWrite] = new SpikeRecord
                    {
                        SpikeMs    = spikeMs,
                        BudgetMs   = budgetMs,
                        ElapsedSec = elapsed,
                        Bpm        = bpm
                    };
                    _spikeWrite = (_spikeWrite + 1) % SPIKE_SLOTS;
                    if (_spikeCount < SPIKE_SLOTS) _spikeCount++;
                    _lastSpikeTicks = nowTicks;
                }

                // Clamp to baseline for avg so xrun buffers don't inflate above 100%
                long clampedOther = _baselinePeriodTicks > 0
                    ? Math.Min(otherTicks, _baselinePeriodTicks)
                    : Math.Min(otherTicks, periodTicks);
                _sumOtherTicks  += clampedOther;
                _sumPeriodTicks += _baselinePeriodTicks > 0 ? _baselinePeriodTicks : periodTicks;

                if (otherTicks > _maxOtherTicks)
                {
                    _maxOtherTicks       = otherTicks;
                    _maxOtherPeriodTicks = periodTicks;
                }
                _windowCount++;

                if (_windowCount >= Window)
                {
                    double freq         = Stopwatch.Frequency;
                    double avgOtherMs   = _sumOtherTicks  / (double)_windowCount / freq * 1000.0;
                    double avgPeriodMs  = _sumPeriodTicks / (double)_windowCount / freq * 1000.0;
                    double peakOtherMs  = _maxOtherTicks       / freq * 1000.0;
                    double peakPeriodMs = _maxOtherPeriodTicks / freq * 1000.0;
                    double cpuPct       = avgPeriodMs  > 0 ? avgOtherMs  / avgPeriodMs  * 100.0 : 0.0;
                    double peakCpuPct   = peakPeriodMs > 0 ? peakOtherMs / peakPeriodMs * 100.0 : 0.0;

                    var spikes = new SpikeRecord[_spikeCount];
                    for (int i = 0; i < _spikeCount; i++)
                    {
                        int slot = (_spikeWrite - 1 - i + SPIKE_SLOTS) % SPIKE_SLOTS;
                        spikes[i] = _spikeRing[slot];
                    }

                    _snapshot = new ProfileSnapshot
                    {
                        CpuPct           = cpuPct,
                        PeakCpuPct       = peakCpuPct,
                        AvgOtherMs       = avgOtherMs,
                        PeakOtherMs      = peakOtherMs,
                        PeriodMs         = avgPeriodMs,
                        BudgetMs         = avgPeriodMs,
                        SampleRate       = sr,
                        IsValid          = true,
                        DropoutsInWindow = _windowDropouts,
                        TotalDropouts    = _totalDropouts,
                        TotalBuffers     = _totalBuffers,
                        RecentSpikes     = spikes,
                        Bpm              = bpm
                    };

                    _sumOtherTicks       = 0;
                    _sumPeriodTicks      = 0;
                    _maxOtherTicks       = 0;
                    _maxOtherPeriodTicks = 0;
                    _windowCount         = 0;
                    _windowDropouts      = 0;
                }
            }

            _lastWorkStart = nowTicks;
            _lastWorkEnd   = Stopwatch.GetTimestamp();
        }
    }

    // =========================================================================
    //  GUI factory (ReBuzz discovers this via reflection)
    // =========================================================================
    public class ProfilerGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new ProfilerGUI();
    }

    // =========================================================================
    //  GUI
    // =========================================================================
    public class ProfilerGUI : UserControl, IMachineGUI
    {
        // ── Machine references ────────────────────────────────────────────────
        IMachine        _iMachine;
        ProfilerMachine _machine;

        // ── Update timer ──────────────────────────────────────────────────────
        DispatcherTimer _timer;
        int             _ticksSinceMachineListRefresh;
        const int       MACHINE_LIST_REFRESH_INTERVAL = 25;  // ~2.5 s at 100 ms ticks

        // ── Sparkline history (CPU % samples) ────────────────────────────────
        const int HISTORY_LEN = 120;
        readonly double[] _history    = new double[HISTORY_LEN];
        int               _historyIdx = 0;
        bool              _historyFull = false;

        // ── Peak hold (decays slowly on UI thread) ────────────────────────────
        double _peakHold = 0.0;
        const double PEAK_DECAY = 0.985;

        // ── Mute-delta tracking (polling-based — PropertyChanged is unreliable) ──
        // OnTick compares each machine's current IsMuted against _machineWasMuted.
        // A change triggers a 1.5 s stabilisation delay, then the CPU delta is
        // recorded and displayed as a mini bar next to the machine name.
        readonly Dictionary<string, double> _machineDelta    = new Dictionary<string, double>();
        readonly Dictionary<string, bool>   _machineWasMuted = new Dictionary<string, bool>();
        readonly HashSet<string>            _pendingDelta    = new HashSet<string>();
        List<IMachine>                      _trackedMachines = new List<IMachine>();

        // ── UI element references ─────────────────────────────────────────────
        Rectangle  _cpuFill, _peakMark, _budgetMark;
        TextBlock  _cpuText, _avgText, _peakText, _budgetText, _srText;
        Canvas     _spark;
        StackPanel _machineListPanel;
        StackPanel _topologyPanel;
        StackPanel _spikePanel;
        TextBlock  _measureLabel;
        TextBlock  _dropoutText;

        // Song event subscriptions (UI thread)
        IBuzz _subscribedBuzz;

        const double BAR_W      = 320.0;
        const double BAR_H      = 20.0;
        const double SPARK_W    = 320.0;
        const double SPARK_H    = 50.0;
        const double DELTA_BAR_W = 80.0;

        // ── Brushes ───────────────────────────────────────────────────────────
        static readonly Brush BrushBg        = Freeze(new SolidColorBrush(Color.FromRgb( 24,  26,  30)));
        static readonly Brush BrushTrack     = Freeze(new SolidColorBrush(Color.FromRgb( 40,  43,  48)));
        static readonly Brush BrushGreen     = Freeze(new SolidColorBrush(Color.FromRgb( 56, 182,  89)));
        static readonly Brush BrushOrange    = Freeze(new SolidColorBrush(Color.FromRgb(230, 140,  30)));
        static readonly Brush BrushRed       = Freeze(new SolidColorBrush(Color.FromRgb(210,  55,  55)));
        static readonly Brush BrushPeak      = Freeze(new SolidColorBrush(Color.FromRgb(255, 210,  60)));
        static readonly Brush BrushBudget    = Freeze(new SolidColorBrush(Color.FromRgb(200,  50,  50)));
        static readonly Brush BrushText      = Freeze(new SolidColorBrush(Color.FromRgb(200, 205, 215)));
        static readonly Brush BrushSubText   = Freeze(new SolidColorBrush(Color.FromRgb(130, 138, 150)));
        static readonly Brush BrushTitle     = Freeze(new SolidColorBrush(Color.FromRgb(170, 185, 215)));
        static readonly Brush BrushRowA      = Freeze(new SolidColorBrush(Color.FromRgb( 38,  41,  46)));
        static readonly Brush BrushRowMuted  = Freeze(new SolidColorBrush(Color.FromRgb( 30,  32,  36)));
        static readonly Brush BrushMutedFg   = Freeze(new SolidColorBrush(Color.FromRgb( 85,  90, 100)));
        static readonly Brush BrushSparkLine = Freeze(new SolidColorBrush(Color.FromRgb( 70, 160, 230)));
        static readonly Brush BrushSparkBg   = Freeze(new SolidColorBrush(Color.FromRgb( 30,  33,  38)));
        static readonly Brush BrushDeltaBar  = Freeze(new SolidColorBrush(Color.FromRgb( 56, 182,  89)));
        static readonly Brush BrushDeltaTrack= Freeze(new SolidColorBrush(Color.FromRgb( 40,  43,  48)));
        static readonly FontFamily Mono = new FontFamily("Consolas");

        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        // =====================================================================
        //  IMachineGUI
        // =====================================================================
        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                // Unsubscribe from previous song events
                UnsubscribeSongEvents();

                _iMachine        = value;
                _machine         = value?.ManagedMachine as ProfilerMachine;
                _trackedMachines = new List<IMachine>();
                _machineWasMuted.Clear();

                // Subscribe to song machine add/remove so the list stays live
                _subscribedBuzz = value?.Graph?.Buzz;
                if (_subscribedBuzz?.Song != null)
                {
                    _subscribedBuzz.Song.MachineAdded   += OnSongMachineAdded;
                    _subscribedBuzz.Song.MachineRemoved += OnSongMachineRemoved;
                }

                RefreshMachineList();
            }
        }

        void UnsubscribeSongEvents()
        {
            if (_subscribedBuzz?.Song != null)
            {
                _subscribedBuzz.Song.MachineAdded   -= OnSongMachineAdded;
                _subscribedBuzz.Song.MachineRemoved -= OnSongMachineRemoved;
            }
            _subscribedBuzz = null;
        }

        void OnSongMachineAdded(IMachine m)   { RefreshMachineList(); RefreshTopology(); }
        void OnSongMachineRemoved(IMachine m) { RefreshMachineList(); RefreshTopology(); }

        // =====================================================================
        //  Constructor
        // =====================================================================
        public ProfilerGUI()
        {
            BuildUI();

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += OnTick;
            _timer.Start();
            Unloaded += (_, __) => { _timer.Stop(); UnsubscribeSongEvents(); };
        }

        // =====================================================================
        //  Timer callback — runs on UI thread
        // =====================================================================
        void OnTick(object sender, EventArgs e)
        {
            if (_machine == null) return;


            var snap = _machine.Snapshot;   // atomic reference read
            if (!snap.IsValid) return;

            // ── Update history ────────────────────────────────────────────────
            _history[_historyIdx] = snap.CpuPct;
            _historyIdx = (_historyIdx + 1) % HISTORY_LEN;
            if (_historyIdx == 0) _historyFull = true;

            // ── Peak hold ─────────────────────────────────────────────────────
            _peakHold = Math.Max(snap.PeakCpuPct, _peakHold * PEAK_DECAY);

            // ── CPU bar ───────────────────────────────────────────────────────
            double frac     = Math.Min(snap.CpuPct / 100.0, 1.0);
            double peakFrac = Math.Min(_peakHold    / 100.0, 1.0);

            _cpuFill.Width = frac * BAR_W;
            _cpuFill.Fill  = frac > 0.85 ? BrushRed
                           : frac > 0.60 ? BrushOrange
                           :               BrushGreen;

            Canvas.SetLeft(_peakMark, Math.Min(peakFrac * BAR_W, BAR_W - 2));

            // ── Text stats ────────────────────────────────────────────────────
            _cpuText.Text    = $"Audio CPU: {snap.CpuPct:F1}%   peak {_peakHold:F1}%";
            _avgText.Text    = $"Avg render :  {snap.AvgOtherMs:F3} ms";
            _peakText.Text   = $"Peak render:  {snap.PeakOtherMs:F3} ms";
            _budgetText.Text = $"Budget     :  {snap.BudgetMs:F3} ms  (audio deadline)";
            _srText.Text     = $"Sample rate:  {snap.SampleRate} Hz   " +
                               $"buffer ≈ {snap.PeriodMs * snap.SampleRate / 1000.0:F0} smp";

            // ── Sparkline ─────────────────────────────────────────────────────
            DrawSparkline();

            // ── Dropout stats ──────────────────────────────────────────────────
            if (_dropoutText != null)
            {
                double dropoutRate = snap.TotalBuffers > 0
                    ? snap.TotalDropouts * 100.0 / snap.TotalBuffers : 0;
                // "Near-dropout" = ≥90% of budget. Even when stopped, machines
                // can consume this much — that is genuine idle load, not a false alarm.
                string playState = snap.Bpm > 0 ? $"{snap.Bpm} BPM" : "";
                _dropoutText.Text =
                    $"Near-dropouts (≥90% budget): {snap.DropoutsInWindow} this window   " +
                    $"{snap.TotalDropouts} total ({dropoutRate:F1}%)   {playState}\n" +
                    $"Xruns (>150% budget): see spike log below";
            }

            // ── Spike log ─────────────────────────────────────────────────────
            UpdateSpikeLog(snap);

            // ── Poll mute states — detect changes without relying on events ────
            CheckMuteChanges(snap.CpuPct);

            // ── Periodic machine list + topology refresh ───────────────────────
            _ticksSinceMachineListRefresh++;
            if (_ticksSinceMachineListRefresh >= MACHINE_LIST_REFRESH_INTERVAL)
            {
                _ticksSinceMachineListRefresh = 0;
                RefreshMachineList();
                RefreshTopology();
            }
        }

        // =====================================================================
        //  Mute-change polling (called every OnTick — UI thread)
        // =====================================================================
        int    _debugCounter   = 0;
        string _lastMeasureMsg = "";   // shown in the GUI regardless of delta size

        void CheckMuteChanges(double cpuNow)
        {
            _debugCounter++;
            bool doLog = (_debugCounter % 50 == 1);
            IBuzz buzz = doLog ? _iMachine?.Graph?.Buzz : null;

            if (doLog)
                buzz?.DCWriteLine($"[Profiler] tracked={_trackedMachines.Count}  pending={_pendingDelta.Count}  deltas={_machineDelta.Count}");

            foreach (var m in _trackedMachines)
            {
                string name;
                bool   nowMuted;
                try
                {
                    name     = m.Name;
                    nowMuted = m.IsMuted;
                }
                catch (Exception ex)
                {
                    buzz?.DCWriteLine($"[Profiler] IsMuted threw: {ex.Message}");
                    continue;
                }

                if (doLog)
                    buzz?.DCWriteLine($"[Profiler]   {name}: IsMuted={nowMuted}  prev={(_machineWasMuted.TryGetValue(name, out bool p) ? p.ToString() : "?")}  pending={_pendingDelta.Contains(name)}");

                if (!_machineWasMuted.TryGetValue(name, out bool wasMuted))
                {
                    _machineWasMuted[name] = nowMuted;
                    continue;
                }

                if (nowMuted == wasMuted || _pendingDelta.Contains(name)) continue;

                buzz?.DCWriteLine($"[Profiler] MUTE CHANGE: {name} → IsMuted={nowMuted}  cpuBefore={cpuNow:F1}%");

                _machineWasMuted[name] = nowMuted;
                _pendingDelta.Add(name);
                double cpuBefore = cpuNow;
                double msBefore  = _machine?.Snapshot?.AvgOtherMs ?? 0;
                _lastMeasureMsg  = $"Measuring {name}…  render before: {msBefore:F3} ms";
                UpdateMeasureLabel();

                bool   mutedNow     = nowMuted;
                string capturedName = name;

                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                t.Tick += (_, __) =>
                {
                    t.Stop();
                    _pendingDelta.Remove(capturedName);
                    double cpuAfter = _machine?.Snapshot?.CpuPct      ?? 0;
                    double msAfter  = _machine?.Snapshot?.AvgOtherMs  ?? 0;

                    // Delta in ms — works even when % is clamped at 100 %
                    double deltaMs  = mutedNow ? (msBefore - msAfter)
                                               : (msAfter  - msBefore);
                    // Also compute % for display
                    double budget   = _machine?.Snapshot?.BudgetMs ?? 1;
                    double deltaPct = budget > 0 ? deltaMs / budget * 100.0 : 0;

                    _iMachine?.Graph?.Buzz?.DCWriteLine(
                        $"[Profiler] STABILISED: {capturedName}  " +
                        $"before={msBefore:F3}ms  after={msAfter:F3}ms  Δ={deltaMs:F3}ms ({deltaPct:F1}%)");

                    // Record ms-based delta (always, even if small)
                    _machineDelta[capturedName] = Math.Max(0, deltaMs);

                    string deltaStr = Math.Abs(deltaMs) < 0.05
                        ? "Δ≈0 ms — remaining machines still max the budget"
                        : $"Δ={deltaMs:F2} ms  (~{deltaPct:F0}% of budget)";
                    _lastMeasureMsg = $"{capturedName}: {msBefore:F2}→{msAfter:F2} ms   {deltaStr}";
                    UpdateMeasureLabel();
                    RefreshMachineList();
                };
                t.Start();
            }
        }

        void UpdateMeasureLabel()
        {
            if (_measureLabel != null)
                _measureLabel.Text = _lastMeasureMsg;
        }

        // =====================================================================
        //  Spike log update (UI thread — reads snapshot published by audio thread)
        // =====================================================================
        void UpdateSpikeLog(ProfileSnapshot snap)
        {
            if (_spikePanel == null) return;
            _spikePanel.Children.Clear();

            var spikes = snap.RecentSpikes;
            if (spikes == null || spikes.Length == 0)
            {
                _spikePanel.Children.Add(new TextBlock
                {
                    Text = "  No spikes recorded yet  (threshold: >95% of budget)",
                    FontFamily = Mono, FontSize = 10,
                    Foreground = BrushSubText
                });
                return;
            }

            foreach (var s in spikes)
            {
                // Format elapsed time as mm:ss since profiler was loaded
                int totalSec = (int)s.ElapsedSec;
                string elapsed = $"{totalSec / 60}:{totalSec % 60:D2}";

                string ratioStr = $"{s.Ratio:F1}× budget";
                Brush  colour   = s.Ratio > 4 ? BrushRed
                                : s.Ratio > 2 ? BrushOrange
                                :               BrushPeak;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,1,0,0) };
                row.Children.Add(new TextBlock
                {
                    Text = $"  {s.SpikeMs,6:F1} ms  ",
                    FontFamily = Mono, FontSize = 10.5,
                    Foreground = colour
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"({ratioStr})  at +{elapsed}  [{s.Bpm} BPM]",
                    FontFamily = Mono, FontSize = 10.5,
                    Foreground = BrushSubText
                });
                _spikePanel.Children.Add(row);
            }
        }

        // =====================================================================
        //  Signal chain topology (UI thread — walks m.Inputs graph)
        // =====================================================================
        void RefreshTopology()
        {
            if (_topologyPanel == null) return;
            _topologyPanel.Children.Clear();

            IBuzz buzz = _iMachine?.Graph?.Buzz;
            if (buzz?.Song?.Machines == null) return;

            List<IMachine> all;
            try { all = buzz.Song.Machines.Where(m => m != _iMachine).ToList(); }
            catch { return; }

            // Find sink machines — those whose Outputs are empty or null
            // (Master is typically the only sink)
            var sinks = all.Where(m =>
            {
                try { var o = m.Outputs; return o == null || !o.Any(); }
                catch { return false; }
            }).ToList();

            // If no sinks found fall back to the machine named "Master"
            if (!sinks.Any())
                sinks = all.Where(m => { try { return m.Name == "Master"; } catch { return false; } }).ToList();

            // Fallback: just show all machines flat if graph walk fails
            if (!sinks.Any())
            {
                foreach (var m in all)
                    AddTopologyRow(m, 0, false);
                return;
            }

            var visited = new HashSet<IMachine>();
            foreach (var sink in sinks)
                BuildTopologyNode(sink, 0, visited);
        }

        void BuildTopologyNode(IMachine m, int depth, HashSet<IMachine> visited)
        {
            if (visited.Contains(m)) return;
            visited.Add(m);

            AddTopologyRow(m, depth, depth > 0);

            // Recurse into upstream machines
            try
            {
                var inputs = m.Inputs?.ToList();
                if (inputs == null) return;
                foreach (var conn in inputs)
                {
                    IMachine src;
                    try { src = conn.Source; } catch { continue; }
                    if (src != null && src != _iMachine)
                        BuildTopologyNode(src, depth + 1, visited);
                }
            }
            catch { }
        }

        void AddTopologyRow(IMachine m, int depth, bool isChild)
        {
            string name;
            bool   isMuted;
            try { name = m.Name; isMuted = m.IsMuted; }
            catch { return; }

            string typeTag = m.IsControlMachine ? "CTRL" :
                             (m.Inputs != null && m.Inputs.Any()) ? " FX " : " GEN";
            string indent  = new string(' ', depth * 3);
            string prefix  = isChild ? "└─ " : "";

            // Delta from mute measurements
            string deltaStr = "";
            if (_machineDelta.TryGetValue(name, out double delta) && delta >= 0.02)
                deltaStr = $"  {delta:F2}ms";

            var row = new Border
            {
                Background   = isMuted ? BrushRowMuted : BrushRowA,
                CornerRadius = new CornerRadius(2),
                Margin       = new Thickness(0, 1, 0, 0),
                Padding      = new Thickness(4, 2, 4, 2)
            };
            row.Child = new TextBlock
            {
                Text       = $"{indent}{prefix}[{typeTag}]  {name}{(isMuted ? "  — muted" : "")}{deltaStr}",
                FontFamily = Mono,
                FontSize   = 11,
                Foreground = isMuted ? BrushMutedFg : BrushText
            };
            _topologyPanel.Children.Add(row);
        }


        void DrawSparkline()
        {
            _spark.Children.Clear();

            // Faint grid lines at 25%, 50%, 75%, 100%
            foreach (double pct in new[] { 0.25, 0.50, 0.75, 1.00 })
            {
                double y = SPARK_H - pct * SPARK_H;
                var gridLine = new Line
                {
                    X1 = 0, X2 = SPARK_W, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(
                        pct >= 1.0 ? Color.FromArgb(100, 200, 50, 50)
                                   : Color.FromArgb(45, 100, 108, 120)),
                    StrokeThickness = pct >= 1.0 ? 1.0 : 0.7
                };
                _spark.Children.Add(gridLine);
            }

            // Data points
            int count = _historyFull ? HISTORY_LEN : _historyIdx;
            if (count < 2) return;

            // Oldest-first ordered slice
            var pts = new List<Point>(count);
            int startIdx = _historyFull ? _historyIdx : 0;
            for (int i = 0; i < count; i++)
            {
                int idx   = (startIdx + i) % HISTORY_LEN;
                double x  = i / (double)(count - 1) * SPARK_W;
                double y  = SPARK_H - Math.Min(_history[idx] / 100.0, 1.0) * SPARK_H;
                pts.Add(new Point(x, y));
            }

            // Filled area
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, SPARK_H), isFilled: true, isClosed: true);
                ctx.LineTo(pts[0], isStroked: false, isSmoothJoin: false);
                for (int i = 1; i < pts.Count; i++)
                    ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new Point(pts[pts.Count - 1].X, SPARK_H), isStroked: false, isSmoothJoin: false);
            }
            geo.Freeze();
            _spark.Children.Add(new System.Windows.Shapes.Path
            {
                Data   = geo,
                Fill   = new SolidColorBrush(Color.FromArgb(40, 70, 160, 230)),
                Stroke = BrushSparkLine,
                StrokeThickness = 1.5
            });

            // 100% warning tint if peak > 85%
            if (_peakHold > 85)
            {
                _spark.Children.Add(new Rectangle
                {
                    Width  = SPARK_W,
                    Height = SPARK_H * Math.Min((_peakHold - 85) / 15.0, 1.0),
                    Fill   = new SolidColorBrush(Color.FromArgb(25, 200, 50, 50))
                });
            }
        }

        // =====================================================================
        //  Machine list (UI thread only — Song.Machines is not audio-thread safe)
        // =====================================================================
        void RefreshMachineList()
        {
            _machineListPanel?.Children.Clear();
            if (_machineListPanel == null) return;

            IBuzz buzz = _iMachine?.Graph?.Buzz;
            if (buzz?.Song?.Machines == null)
            {
                AddMachineRow("(no song loaded)", null, 1.0, BrushSubText, BrushRowA, false);
                return;
            }

            IMachine self = _iMachine;

            List<IMachine> all;
            try { all = buzz.Song.Machines.Where(m => m != self).ToList(); }
            catch { return; }

            // Update tracked machines list for polling (no event subscription needed)
            _trackedMachines = all;
            foreach (var m in all)
            {
                string name; try { name = m.Name; } catch { continue; }
                if (!_machineWasMuted.ContainsKey(name))
                    try { _machineWasMuted[name] = m.IsMuted; } catch { }
            }

            // Classification — use live audio routing connections, not ParameterGroups.
            // All Buzz machines have an Input parameter group in slot 0 (even pure
            // generators), so checking ParameterGroups[0].Type is unreliable.
            // m.Inputs is the list of incoming AUDIO connections — generators have none.
            string TypeTag(IMachine m)
            {
                if (m.IsControlMachine) return "CTRL";
                try
                {
                    var inputs = m.Inputs;
                    if (inputs != null && inputs.Any()) return " FX ";
                }
                catch { }
                return " GEN";
            }

            bool Muted(IMachine m) { try { return m.IsMuted; } catch { return false; } }

            // Sort: active → muted, within each group GEN → FX → CTRL,
            //       then by measured delta descending (highest cost first)
            double Delta(IMachine m)
            {
                string name; try { name = m.Name; } catch { return 0; }
                return _machineDelta.TryGetValue(name, out double d) ? d : -1;
            }

            var sorted = all
                .OrderBy(m => Muted(m) ? 1 : 0)
                .ThenBy(m => TypeTag(m) == "CTRL" ? 2 : TypeTag(m) == " FX " ? 1 : 0)
                .ThenByDescending(m => Delta(m))
                .ToList();

            // Find highest delta (in ms) for bar scaling
            double maxDelta = sorted.Select(m => Delta(m)).Where(d => d > 0).DefaultIfEmpty(0.1).Max();

            int active = 0, muted = 0;

            foreach (var m in sorted)
            {
                bool   isMuted = Muted(m);
                string tag     = TypeTag(m);
                string name;
                try { name = m.Name; } catch { name = "(unknown)"; }

                if (isMuted) muted++; else active++;

                double delta = Delta(m);
                string muteLabel = isMuted ? "  — muted" : "";

                Brush fg = isMuted ? BrushMutedFg : BrushText;
                Brush bg = isMuted ? BrushRowMuted : BrushRowA;

                AddMachineRow($"[{tag}]  {name}{muteLabel}", delta > 0 ? delta : (double?)null,
                              maxDelta, fg, bg, isMuted);
            }

            // Summary
            _machineListPanel.Children.Add(new TextBlock
            {
                Text       = $"  {active} active   {muted} muted   {sorted.Count} total",
                FontFamily = Mono, FontSize = 10.5, Foreground = BrushSubText,
                Margin     = new Thickness(0, 5, 0, 0)
            });

            bool anyMeasured = _machineDelta.Count > 0;
            string hint = anyMeasured
                ? "  Mute/unmute machines to update cost bars."
                : "  Mute a machine — its CPU cost will appear here.";

            _measureLabel = new TextBlock
            {
                Text         = string.IsNullOrEmpty(_lastMeasureMsg) ? hint : _lastMeasureMsg,
                FontFamily   = Mono, FontSize = 9.5,
                Foreground   = new SolidColorBrush(Color.FromRgb(80, 88, 100)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0)
            };
            _machineListPanel.Children.Add(_measureLabel);
        }


        // ── Machine row with optional delta bar ───────────────────────────────
        void AddMachineRow(string label, double? delta, double maxDelta,
                           Brush fg, Brush bg, bool isMuted)
        {
            var outer = new Border
            {
                Background   = bg,
                CornerRadius = new CornerRadius(2),
                Margin       = new Thickness(0, 1, 0, 0),
                Padding      = new Thickness(6, 3, 6, 3)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DELTA_BAR_W + 46) });

            // Label
            var tb = new TextBlock
            {
                Text = label, FontFamily = Mono, FontSize = 11, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, 0);
            row.Children.Add(tb);

            // Delta bar + text
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center
            };

            if (delta.HasValue && delta.Value >= 0.02)   // 0.02 ms minimum — catches small savings
            {
                double frac = Math.Min(delta.Value / Math.Max(maxDelta, 0.1), 1.0);

                // Mini bar
                var barBg = new Border
                {
                    Width = DELTA_BAR_W, Height = 8,
                    Background   = BrushDeltaTrack,
                    CornerRadius = new CornerRadius(2),
                    Margin       = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Colour by ms cost: >1ms = red, >0.5ms = orange, else green
                var barFill = new Border
                {
                    Width  = frac * DELTA_BAR_W, Height = 8,
                    Background   = delta.Value > 1.0 ? BrushRed
                                 : delta.Value > 0.5 ? BrushOrange
                                 :                     BrushDeltaBar,
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                barBg.Child = barFill;
                rightPanel.Children.Add(barBg);

                // Label in ms
                rightPanel.Children.Add(new TextBlock
                {
                    Text              = $"{delta.Value:F2}ms",
                    FontFamily        = Mono,
                    FontSize          = 10,
                    Foreground        = isMuted ? BrushMutedFg : BrushText,
                    Width             = 38,
                    TextAlignment     = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            else
            {
                // No measurement yet
                rightPanel.Children.Add(new TextBlock
                {
                    Text              = "—",
                    FontFamily        = Mono,
                    FontSize          = 10,
                    Foreground        = new SolidColorBrush(Color.FromRgb(60, 65, 75)),
                    Width             = DELTA_BAR_W + 38,
                    TextAlignment     = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            Grid.SetColumn(rightPanel, 1);
            row.Children.Add(rightPanel);

            outer.Child = row;
            _machineListPanel.Children.Add(outer);
        }

        // =====================================================================
        //  UI construction
        // =====================================================================
        void BuildUI()
        {
            Background = BrushBg;
            MinWidth   = 360;

            var root = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };

            // ── Title ─────────────────────────────────────────────────────────
            root.Children.Add(new TextBlock
            {
                Text       = "Pedal Profiler",
                FontSize   = 14,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTitle,
                Margin     = new Thickness(0, 0, 0, 10)
            });

            // ── CPU bar ───────────────────────────────────────────────────────
            var barCanvas = new Canvas { Width = BAR_W, Height = BAR_H };

            // Track background
            barCanvas.Children.Add(new Rectangle
            {
                Width = BAR_W, Height = BAR_H,
                Fill  = BrushTrack,
                RadiusX = 3, RadiusY = 3
            });

            // Fill
            _cpuFill = new Rectangle { Height = BAR_H, Width = 0, Fill = BrushGreen, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(_cpuFill, 0);
            barCanvas.Children.Add(_cpuFill);

            // Subtle tick marks at 25%, 50%, 75%
            foreach (double pct in new[] { 0.25, 0.50, 0.75 })
            {
                var tick = new Rectangle
                {
                    Width = 1, Height = BAR_H * 0.45,
                    Fill  = new SolidColorBrush(Color.FromArgb(70, 200, 210, 230))
                };
                Canvas.SetLeft(tick, pct * BAR_W);
                Canvas.SetTop(tick, BAR_H * 0.275);
                barCanvas.Children.Add(tick);
            }

            // Budget deadline marker (red, at 100%)
            _budgetMark = new Rectangle
            {
                Width = 2, Height = BAR_H,
                Fill  = BrushBudget
            };
            Canvas.SetLeft(_budgetMark, BAR_W - 2);
            barCanvas.Children.Add(_budgetMark);

            // Peak hold marker (yellow, moves with peak)
            _peakMark = new Rectangle
            {
                Width = 2, Height = BAR_H,
                Fill  = BrushPeak
            };
            Canvas.SetLeft(_peakMark, 0);
            barCanvas.Children.Add(_peakMark);

            root.Children.Add(barCanvas);

            // ── Bar legend ────────────────────────────────────────────────────
            var legend = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 2, 0, 8)
            };
            legend.Children.Add(LegendSwatch(BrushGreen));
            legend.Children.Add(LegendLabel("low  "));
            legend.Children.Add(LegendSwatch(BrushOrange));
            legend.Children.Add(LegendLabel("medium  "));
            legend.Children.Add(LegendSwatch(BrushRed));
            legend.Children.Add(LegendLabel("high  "));
            legend.Children.Add(LegendSwatch(BrushPeak));
            legend.Children.Add(LegendLabel("peak hold  "));
            legend.Children.Add(LegendSwatch(BrushBudget));
            legend.Children.Add(LegendLabel("deadline"));
            root.Children.Add(legend);

            // ── Text stats ────────────────────────────────────────────────────
            var statsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            _cpuText    = AddStatLine(statsPanel, "");
            _avgText    = AddStatLine(statsPanel, "");
            _peakText   = AddStatLine(statsPanel, "");
            _budgetText = AddStatLine(statsPanel, "");
            _srText     = AddStatLine(statsPanel, "");
            root.Children.Add(statsPanel);

            // ── Sparkline (CPU history) ───────────────────────────────────────
            root.Children.Add(new TextBlock
            {
                Text       = "CPU history (last ~2 min)",
                FontSize   = 10,
                Foreground = BrushSubText,
                Margin     = new Thickness(0, 0, 0, 3)
            });

            _spark = new Canvas
            {
                Width      = SPARK_W,
                Height     = SPARK_H,
                Background = BrushSparkBg,
                Margin     = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(_spark);

            // ── Machine list ──────────────────────────────────────────────────
            root.Children.Add(new TextBlock
            {
                Text       = "Machines in song",
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushSubText,
                Margin     = new Thickness(0, 0, 0, 4)
            });

            _machineListPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                Content                     = _machineListPanel,
                VerticalScrollBarVisibility  = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 280
            };
            root.Children.Add(scroll);

            // ── Dropout & spike section ───────────────────────────────────────
            root.Children.Add(SectionHeader("Dropouts & spikes"));

            _dropoutText = new TextBlock
            {
                FontFamily   = Mono, FontSize = 10.5,
                Foreground   = BrushSubText,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4),
                Text         = "Waiting for data…"
            };
            root.Children.Add(_dropoutText);

            _spikePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(_spikePanel);

            // ── Signal chain topology ─────────────────────────────────────────
            root.Children.Add(SectionHeader("Signal chain"));

            _topologyPanel = new StackPanel();
            var topoScroll = new ScrollViewer
            {
                Content                      = _topologyPanel,
                VerticalScrollBarVisibility  = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 200,
                Margin    = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(topoScroll);

            // ── Footer explanation ─────────────────────────────────────────────
            root.Children.Add(new TextBlock
            {
                Text         = "\nCPU % = time in generators+effects ÷ audio buffer period.\n" +
                               "Measured by bracketing all other Work() calls between this\n" +
                               "control machine's own Work() invocations.",
                FontSize     = 9,
                Foreground   = new SolidColorBrush(Color.FromRgb(75, 82, 95)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 6, 0, 0)
            });

            Content = new ScrollViewer
            {
                Content                     = root,
                VerticalScrollBarVisibility  = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        // ── Small helpers ─────────────────────────────────────────────────────
        static TextBlock SectionHeader(string title) => new TextBlock
        {
            Text       = title,
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushSubText,
            Margin     = new Thickness(0, 8, 0, 4)
        };

        static TextBlock AddStatLine(StackPanel parent, string text)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontFamily = Mono,
                FontSize   = 11,
                Foreground = BrushText,
                Margin     = new Thickness(0, 1, 0, 0)
            };
            parent.Children.Add(tb);
            return tb;
        }

        static UIElement LegendSwatch(Brush b) => new Rectangle
        {
            Width = 10, Height = 10,
            Fill  = b,
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        static TextBlock LegendLabel(string t) => new TextBlock
        {
            Text       = t,
            FontSize   = 9,
            Foreground = BrushSubText,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
