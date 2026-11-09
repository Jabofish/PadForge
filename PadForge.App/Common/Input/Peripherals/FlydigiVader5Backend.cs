using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Lights a Flydigi Vader 5 Pro's LED strip with the color a game writes
    /// to a virtual DualSense's lightbar (#494), through the controller's own
    /// vendor HID interface, so it works on the cable and over the 2.4G
    /// dongle with no Flydigi software running.
    ///
    /// <para>It is an <see cref="IDirectUnitBackend"/> like the SteelSeries
    /// and Razer ones, so scanning, rows, claims
    /// (<see cref="PeripheralOutputs.TryResolveColor(OutputPath, out int)"/>),
    /// crash hand-back and the unit row all come from the host. The unit
    /// appears as a unit row; assigned to a virtual controller it takes that
    /// controller's lightbar. This backend only opens the vendor interface
    /// (0xFFA0). It never enables the controller's test mode and never reads
    /// input, so it adds no input and cannot double one.</para>
    ///
    /// <para>Claim: read the active profile and its LED blob (kept as the
    /// original), put the strip in the off state, then follow with live colors
    /// (0xF5, one frame, no reply, rate limited for the dongle). Hand-back:
    /// write the original blob. Nothing is saved to the controller.</para>
    ///
    /// <para>Mode "blob" (environment PADFORGE_VADER5_LED_MODE=blob) sends each
    /// color as the captured Space Station static blob instead, acknowledged
    /// frame by frame and much slower. It is the fallback if a firmware ignores
    /// 0xF5.</para>
    /// </summary>
    internal sealed class FlydigiVader5Backend : IDirectUnitBackend
    {
        internal const int ActiveTickMs = 20;
        internal const int IdleTickMs = 500;
        /// <summary>No live color sooner than this after the one before: about 8 a second. At 20 a
        /// second a game's continuous lightbar stream backed the 2.4G dongle up by about two seconds
        /// (hardware log 2026-11-10); tux-invader warns that a flood swamps the dongle.</summary>
        internal const int LiveGapMs = 120;
        /// <summary>A jump of at least <see cref="JumpDelta"/> in any channel (a character switch)
        /// goes out after this much instead.</summary>
        internal const int LiveJumpGapMs = 50;
        internal const int JumpDelta = 48;
        /// <summary>The blob mode sends a color at most this often.</summary>
        internal const int BlobGapMs = 400;
        /// <summary>An unchanged color goes out again this often, so a lost packet heals.</summary>
        internal const int ReassertMs = 2000;
        /// <summary>The controller is asked if it is still there this often while claimed.</summary>
        internal const int KeepaliveMs = 3000;
        internal const int RetryMs = 1000;
        internal const int ReplyMs = 400;
        internal const int BlobReplyMs = 300;
        internal const int KeepaliveReplyMs = 150;
        internal const int OneShotMs = 400;
        internal const int PanicJoinMs = 250;
        internal const int StopJoinMs = 2500;
        internal const int ContainerRetryMs = 3000;
        internal const int NoColor = -1;

        private sealed class Unit
        {
            public string Path;
            public ushort ProductId;
            public Guid Container;
            public long ContainerRetryAt;
            public bool Seen;
            public bool Rejected;       // answered, but is not a Vader 5 Pro
            public bool Verified;       // answered as a Vader 5 Pro
            public IHidReportChannel Io;
            public bool Asleep;
            public long RetryAt;
            public bool Claimed;
            public int Profile;
            public byte[] Original;
            public byte[][] Restore;    // frames that write Original back
            public byte[][] Owed;       // hand-back that did not go out
            public bool Recorded;
            public byte[][] RecordedFrames;
            public int SentRgb = NoColor;
            public int SeenRgb = NoColor;
            public long LastChangeAt;
            public long SentAt;
            public long AliveAt;
            public int Misses;
            public bool WasSilent;
            public readonly byte[] Input = new byte[65];
        }

        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly Func<string, byte[], int, bool> _writeOnce;
        private readonly bool _blobMode;
        private readonly AutoResetEvent _wake = new(false);

        private (string Path, byte[][] Frames)[] _recorded = Array.Empty<(string, byte[][])>();
        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;
        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private static Thread s_lastWorker;

        public FlydigiVader5Backend() : this(null, null, null, null, blobMode: null) { }

        /// <summary>Test seam: channel opener, container lookup, clock, one-shot writer.</summary>
        internal FlydigiVader5Backend(Func<string, IHidReportChannel> open, Func<string, Guid> containerOf,
            Func<long> ticks, Func<string, byte[], int, bool> writeOnce, bool? blobMode)
        {
            _open = open ?? HidReportChannel.Open;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _writeOnce = writeOnce ?? HidReportChannel.WriteOutputOnce;
            _blobMode = blobMode
                ?? string.Equals(Environment.GetEnvironmentVariable("PADFORGE_VADER5_LED_MODE"), "blob", StringComparison.OrdinalIgnoreCase);
        }

        public OutputFamily Family => OutputFamily.FlydigiVader5Unit;
        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public event Action SnapshotChanged;
        public event Action RescanRequested;
        internal int PassCount => Volatile.Read(ref _passes);
        internal (string Path, byte[][] Frames)[] Recorded => Volatile.Read(ref _recorded);

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            Volatile.Write(ref _present, null);
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralFlydigiVader5" };
            _thread.Start();
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.LinksChanged += Wake;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.LinksChanged -= Wake;
            if (_thread == null) return;
            _run.Stop = true;
            Wake();
            try { _thread.Join(StopJoinMs); } catch { }
            _thread = null;
            _run = null;
        }

        public void Rescan(IReadOnlyList<HidCollectionInfo> present)
        {
            if (present == null) return;
            Volatile.Write(ref _present, present);
            Wake();
        }

        public void SignalStop()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
        }

        public void ReleaseClaimedNow(long deadline)
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            long left = deadline - _ticks();
            if (left > 0)
            {
                try { _thread?.Join((int)Math.Min(left, PanicJoinMs)); } catch { }
            }
            foreach (var (path, frames) in Interlocked.Exchange(ref _recorded, Array.Empty<(string, byte[][])>()))
            {
                foreach (byte[] frame in frames)
                {
                    left = deadline - _ticks();
                    if (left <= 0) return;
                    try { _writeOnce(path, frame, (int)Math.Min(left, OneShotMs)); } catch { }
                }
            }
        }

        internal void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        // ─── worker ───

        private void Worker(WorkerRun run)
        {
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive) previous.Join(StopJoinMs);
            var units = new List<Unit>();
            try
            {
                IReadOnlyList<HidCollectionInfo> scanned = null;
                while (!run.Stop)
                {
                    bool changed = false, failed = false;
                    long due = long.MaxValue;

                    var present = Volatile.Read(ref _present);
                    if (present != null && !ReferenceEquals(present, scanned))
                    {
                        changed = Scan(units, present, _ticks()) || scanned == null;
                        scanned = present;
                    }

                    var links = PeripheralOutputs.Links;
                    foreach (var unit in units)
                    {
                        if (run.Stop) break;
                        Serve(run, unit, links, _ticks(), ref changed, ref failed, ref due);
                    }

                    if (changed) Publish(units);
                    if (failed)
                    {
                        try { RescanRequested?.Invoke(); } catch { }
                    }

                    int wait = due == long.MaxValue ? IdleTickMs : (int)Math.Clamp(due - _ticks(), 1, IdleTickMs);
                    Interlocked.Increment(ref _passes);
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL FlydigiVader5 worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // The strip goes back to the controller's own lights before the handle closes.
                foreach (var unit in units)
                {
                    if (unit.Io != null && (unit.Claimed || unit.Owed != null)) HandBack(unit, blocking: true);
                    Close(unit);
                }
                var current = _run;
                if (current == null || ReferenceEquals(current, run))
                {
                    Volatile.Write(ref _recorded, Array.Empty<(string, byte[][])>());
                    Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
                }
            }
        }

        private bool Scan(List<Unit> units, IReadOnlyList<HidCollectionInfo> present, long now)
        {
            bool changed = false;
            foreach (var c in present)
            {
                if (!FlydigiVader5Protocol.IsConfigCollection(c)) continue;
                Unit unit = null;
                foreach (var known in units)
                    if (string.Equals(known.Path, c.Path, StringComparison.OrdinalIgnoreCase)) { unit = known; break; }
                if (unit == null)
                {
                    unit = new Unit { Path = c.Path, ProductId = c.ProductId, Verified = false };
                    LookUpContainer(unit, now);
                    units.Add(unit);
                    changed = true;
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL FlydigiVader5 found {c.VendorId:X4}:{c.ProductId:X4}");
                }
                unit.Seen = true;
            }
            for (int i = units.Count - 1; i >= 0; i--)
            {
                var unit = units[i];
                if (unit.Seen) { unit.Seen = false; continue; }
                // The collection is gone: nothing to hand back to.
                unit.Owed = null;
                Close(unit);
                units.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        private bool LookUpContainer(Unit unit, long now)
        {
            Guid container;
            try { container = _containerOf(unit.Path); }
            catch { container = Guid.Empty; }
            unit.Container = container;
            unit.ContainerRetryAt = now + ContainerRetryMs;
            return container != Guid.Empty;
        }

        private void Publish(List<Unit> units)
        {
            var list = new List<DirectUnit>(units.Count);
            foreach (var unit in units)
            {
                if (unit.Rejected) continue;
                list.Add(new DirectUnit(OutputFamily.FlydigiVader5Unit, unit.Path, unit.Container,
                    FlydigiVader5Protocol.VendorId, unit.ProductId)
                { Name = "Flydigi Vader 5 Pro", Asleep = unit.Asleep });
            }
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(list));
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        private static void Soonest(ref long due, long at)
        {
            if (at < due) due = at;
        }

        private void Serve(WorkerRun run, Unit unit, LinkTable links, long now, ref bool changed, ref bool failed, ref long due)
        {
            if (unit.Rejected) return;
            if (unit.Container == Guid.Empty)
            {
                if (now >= unit.ContainerRetryAt && LookUpContainer(unit, now)) changed = true;
                if (unit.Container == Guid.Empty) Soonest(ref due, unit.ContainerRetryAt);
            }

            var path = new OutputPath(OutputFamily.FlydigiVader5Unit, unit.Path);
            int rgb = 0;
            bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb);
            if (wanted && (rgb & 0xFFFFFF) != unit.SeenRgb)
            {
                unit.SeenRgb = rgb & 0xFFFFFF;
                unit.LastChangeAt = now;
            }

            if (unit.Asleep)
            {
                if (now < unit.RetryAt) { Soonest(ref due, unit.RetryAt); return; }
                // An asleep unit is tried again whether or not anything wants it now: the linker
                // withdraws the path of an asleep unit, so waiting for a claim would never end.
                var answer = Probe(unit);
                if (answer == Answer.NotVader) { Reject(unit, ref changed); return; }
                // Silence is not sleep: with the SDL Flydigi driver reading the same interface a reply can
                // be taken before this worker sees it. Only a failed open or write keeps the unit asleep.
                if (answer == Answer.Failed) { unit.RetryAt = now + RetryMs; Soonest(ref due, unit.RetryAt); Close(unit); return; }
                unit.Asleep = false;
                changed = true;
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL FlydigiVader5 answers again");
                if (unit.Owed != null && !HandBack(unit, blocking: false)) { Sleep(unit, now, "owed hand-back failed", ref changed, ref failed, ref due); return; }
            }

            if (!wanted)
            {
                if (unit.Claimed && unit.Io != null)
                {
                    if (!HandBack(unit, blocking: false))
                    {
                        Sleep(unit, now, "hand-back failed", ref changed, ref failed, ref due);
                        return;
                    }
                    Close(unit);
                }
                else if (unit.Io != null && !unit.Claimed) Close(unit);
                return;
            }

            if (run.Stop) return;

            if (!unit.Claimed)
            {
                var result = Claim(unit, now);
                if (result == Answer.NotVader) { Reject(unit, ref changed); return; }
                if (result != Answer.Yes) { Sleep(unit, now, "claim failed", ref changed, ref failed, ref due); return; }
                if (!unit.Verified) { unit.Verified = true; changed = true; }
            }

            if (!Paint(unit, rgb, now, ref due))
            {
                Sleep(unit, now, "write failed", ref changed, ref failed, ref due);
                return;
            }
            // A probe blocks the worker for a moment, so it waits for a quiet one. A silent probe never
            // stops the colors: only a failed write does (above). When the controller answers again after
            // being silent it may have restarted and lost its lights, so the claim is made again.
            if (now - unit.AliveAt >= KeepaliveMs && now - unit.LastChangeAt >= 1500)
            {
                var alive = Probe(unit, KeepaliveReplyMs);
                unit.AliveAt = now;
                if (alive == Answer.Failed)
                {
                    PowerLost(unit);
                    Sleep(unit, now, "write failed", ref changed, ref failed, ref due);
                    return;
                }
                if (alive == Answer.Yes)
                {
                    if (unit.WasSilent)
                    {
                        unit.WasSilent = false;
                        unit.Claimed = false;
                    }
                }
                else unit.WasSilent = true;
            }
            Soonest(ref due, unit.AliveAt + KeepaliveMs);
        }

        private enum Answer { Yes, Silent, Failed, NotVader }

        private void Reject(Unit unit, ref bool changed)
        {
            unit.Rejected = true;
            Close(unit);
            changed = true;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL FlydigiVader5 {unit.ProductId:X4} is not a Vader 5 Pro, left alone");
        }

        private void Sleep(Unit unit, long now, string why, ref bool changed, ref bool failed, ref long due)
        {
            if (unit.Restore != null && (unit.Claimed || unit.Recorded)) unit.Owed = unit.Restore;
            unit.Claimed = false;
            Close(unit);
            unit.Asleep = true;
            unit.RetryAt = now + RetryMs;
            changed = true;
            failed = true;
            Soonest(ref due, unit.RetryAt);
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL FlydigiVader5 {why}, listed asleep");
        }

        private static void PowerLost(Unit unit)
        {
            unit.Owed = null;
            unit.Original = null;
            unit.Restore = null;
            unit.Claimed = false;
        }

        // ─── talking to the controller ───

        private bool EnsureOpen(Unit unit)
        {
            if (unit.Io != null) return true;
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            if (io == null || io.FeatureOnly || io.OutputReportLength == 0)
            {
                try { io?.Dispose(); } catch { }
                return false;
            }
            unit.Io = io;
            unit.SentRgb = NoColor;
            Drain(unit);
            return true;
        }

        private static void Drain(Unit unit)
        {
            for (int i = 0; i < 64; i++)
                if (unit.Io.ReadInput(unit.Input, 0) <= 0) break;
        }

        private static bool Write(Unit unit, byte[] packet)
            => unit.Io != null && unit.Io.WriteOutput(FlydigiVader5Protocol.AsReport(packet), 1000);

        /// <summary>Sends a frame and waits for the reply to the same command.
        /// Input reports read meanwhile are dropped.</summary>
        private byte[] Exchange(Unit unit, byte[] packet, byte cmd, int timeoutMs, int attempts = 2)
        {
            for (int a = 0; a < attempts; a++)
            {
                if (!Write(unit, packet)) return null;
                long end = Environment.TickCount64 + timeoutMs;
                while (true)
                {
                    long left = end - Environment.TickCount64;
                    if (left <= 0) break;
                    int n = unit.Io.ReadInput(unit.Input, (int)Math.Min(left, 50));
                    if (n < 0) return null;
                    if (n == 0) continue;
                    var reply = FlydigiVader5Protocol.Extract(unit.Input, n);
                    if (reply != null && reply[2] == cmd) return reply;
                }
            }
            return null;
        }

        /// <summary>Opens if needed and asks who it is. Yes when it answers as a Vader 5 Pro.</summary>
        private Answer Probe(Unit unit, int replyMs = ReplyMs)
        {
            return ProbeCore(unit, replyMs, out _);
        }

        private Answer ProbeCore(Unit unit, int replyMs, out string why)
        {
            why = "ok";
            if (!EnsureOpen(unit)) { why = "open"; return Answer.Failed; }
            if (!Write(unit, FlydigiVader5Protocol.InfoRequest())) { why = "info write"; return Answer.Failed; }
            long end = Environment.TickCount64 + replyMs;
            while (true)
            {
                long left = end - Environment.TickCount64;
                if (left <= 0) { why = "no info reply"; return Answer.Silent; }
                int n = unit.Io.ReadInput(unit.Input, (int)Math.Min(left, 50));
                if (n < 0) { why = "info read"; return Answer.Failed; }
                if (n == 0) continue;
                var reply = FlydigiVader5Protocol.Extract(unit.Input, n);
                if (reply == null || reply[2] != FlydigiVader5Protocol.CmdInfo) continue;
                why = $"model byte {reply[5]:X2}";
                return reply[5] == FlydigiVader5Protocol.ModelVader5Pro ? Answer.Yes : Answer.NotVader;
            }
        }

        private Answer Claim(Unit unit, long now)
        {
            var who = Probe(unit);
            if (who == Answer.NotVader || who == Answer.Failed) return who;
            // Silent goes on: the colors are sent whether or not the controller is heard answering.
            if (who == Answer.Yes && unit.Original == null)
            {
                var profileReply = Exchange(unit, FlydigiVader5Protocol.ProfileVersionsRequest(), FlydigiVader5Protocol.CmdProfileVersions, ReplyMs);
                if (profileReply != null)
                {
                    unit.Profile = FlydigiVader5Protocol.ActiveProfile(profileReply);
                    var blob = ReadBlob(unit, unit.Profile);
                    if (blob != null)
                    {
                        unit.Original = blob;
                        unit.Restore = FlydigiVader5Protocol.BlobFrames(unit.Profile, blob);
                    }
                }
            }
            // On record before the first frame that changes the strip goes out.
            Record(unit);

            var off = FlydigiVader5Protocol.StaticFrames(unit.Profile, 0, 100, FlydigiVader5Protocol.ModeOff);
            if (!SendBlob(unit, off)) return Answer.Failed;
            unit.Claimed = true;
            unit.SentRgb = NoColor;
            unit.AliveAt = now;
            unit.Misses = 0;
            return Answer.Yes;
        }

        private byte[] ReadBlob(Unit unit, int profile)
        {
            const int chunk = FlydigiVader5Protocol.BlobChunk;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!Write(unit, FlydigiVader5Protocol.LedReadRequest(profile))) return null;
                var packets = new Dictionary<int, byte[]>();
                long end = Environment.TickCount64 + 3000;
                while (Environment.TickCount64 < end)
                {
                    int n = unit.Io.ReadInput(unit.Input, 50);
                    if (n < 0) return null;
                    if (n == 0) continue;
                    var r = FlydigiVader5Protocol.Extract(unit.Input, n);
                    if (r == null || r[2] != FlydigiVader5Protocol.CmdLedRead || r.Length < 6 + chunk) continue;
                    int total = r[3];
                    var data = new byte[chunk];
                    Array.Copy(r, 6, data, 0, chunk);
                    packets[r[4]] = data;
                    if (total == 0) continue;
                    bool all = true;
                    for (int i = 0; i < total; i++) if (!packets.ContainsKey(i)) { all = false; break; }
                    if (!all) continue;
                    var blob = new byte[total * chunk];
                    for (int i = 0; i < total; i++) Array.Copy(packets[i], 0, blob, i * chunk, chunk);
                    // A sane blob: header byte 7 is the zone count.
                    return blob.Length > 20 && blob[7] != 0 ? blob : null;
                }
            }
            return null;
        }

        /// <summary>Sends every frame. A reply is waited for (twice) so a controller that does answer is
        /// not outrun, but one that does not, because the SDL Flydigi driver read it first, never stops
        /// the colors. Only a failed write returns false.</summary>
        private bool SendBlob(Unit unit, byte[][] frames)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                byte cmd = frames[i][2];
                bool acked = false;
                for (int attempt = 0; attempt < 2 && !acked; attempt++)
                {
                    if (!Write(unit, frames[i])) return false;
                    long end = Environment.TickCount64 + BlobReplyMs;
                    while (!acked)
                    {
                        long left = end - Environment.TickCount64;
                        if (left <= 0) break;
                        int n = unit.Io.ReadInput(unit.Input, (int)Math.Min(left, 50));
                        if (n < 0) return false;
                        if (n == 0) continue;
                        var reply = FlydigiVader5Protocol.Extract(unit.Input, n);
                        if (reply != null && reply[2] == cmd) acked = true;
                    }
                }
            }
            return true;
        }

        private bool Paint(Unit unit, int rgb, long now, ref long due)
        {
            rgb &= 0xFFFFFF;
            int gap = _blobMode ? BlobGapMs : LiveGapMs;
            if (!_blobMode && unit.SentRgb != NoColor && MaxChannelDelta(rgb, unit.SentRgb) >= JumpDelta) gap = LiveJumpGapMs;
            bool changed = rgb != unit.SentRgb;
            bool stale = now - unit.SentAt >= ReassertMs;
            if (!changed && !stale) { Soonest(ref due, unit.SentAt + ReassertMs); return true; }
            if (unit.SentRgb != NoColor && now - unit.SentAt < gap)
            {
                Soonest(ref due, unit.SentAt + gap);
                return true;
            }
            bool ok;
            if (_blobMode)
            {
                byte mode = rgb == 0 ? FlydigiVader5Protocol.ModeOff : FlydigiVader5Protocol.ModeSolid;
                ok = SendBlob(unit, FlydigiVader5Protocol.StaticFrames(unit.Profile, rgb, 100, mode));
            }
            else
            {
                ok = Write(unit, FlydigiVader5Protocol.LiveColor(rgb));
                if (ok) Drain(unit);
            }
            if (!ok) return false;
            unit.SentRgb = rgb;
            unit.SentAt = _ticks();
            Soonest(ref due, unit.SentAt + ReassertMs);
            return true;
        }

        private static int MaxChannelDelta(int a, int b)
        {
            int dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
            int dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
            int db = Math.Abs((a & 0xFF) - (b & 0xFF));
            return Math.Max(dr, Math.Max(dg, db));
        }

        /// <summary>Writes the original lights back. True when they went out (or nothing was owed).</summary>
        private bool HandBack(Unit unit, bool blocking)
        {
            var frames = unit.Owed ?? (unit.Claimed ? unit.Restore : null);
            if (frames == null) { Unrecord(unit); unit.Claimed = false; return true; }
            if (unit.Io == null && !EnsureOpen(unit)) return false;
            bool ok = SendBlob(unit, frames);
            if (!ok && blocking)
            {
                // A last attempt through a handle of its own, as the crash path does.
                ok = true;
                foreach (var f in frames)
                    if (!_writeOnce(unit.Path, FlydigiVader5Protocol.AsReport(f), OneShotMs)) { ok = false; break; }
            }
            if (ok)
            {
                unit.Owed = null;
                unit.Claimed = false;
                unit.SentRgb = NoColor;
                Unrecord(unit);
            }
            return ok;
        }

        private void Close(Unit unit)
        {
            if (unit.Owed == null && !unit.Claimed) Unrecord(unit);
            var io = unit.Io;
            unit.Io = null;
            unit.SentRgb = NoColor;
            try { io?.Dispose(); } catch { }
        }

        // ─── the crash path's record ───

        private void Record(Unit unit)
        {
            if (unit.Recorded || unit.Restore == null) return;
            var frames = new byte[unit.Restore.Length][];
            for (int i = 0; i < frames.Length; i++) frames[i] = FlydigiVader5Protocol.AsReport(unit.Restore[i]);
            var current = Volatile.Read(ref _recorded);
            var next = new (string, byte[][])[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = (unit.Path, frames);
            Volatile.Write(ref _recorded, next);
            unit.Recorded = true;
            unit.RecordedFrames = frames;
        }

        private void Unrecord(Unit unit)
        {
            if (!unit.Recorded) return;
            unit.Recorded = false;
            var frames = unit.RecordedFrames;
            unit.RecordedFrames = null;
            var current = Volatile.Read(ref _recorded);
            var next = new List<(string, byte[][])>(current.Length);
            foreach (var entry in current)
                if (!ReferenceEquals(entry.Frames, frames)) next.Add(entry);
            Volatile.Write(ref _recorded, next.ToArray());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
