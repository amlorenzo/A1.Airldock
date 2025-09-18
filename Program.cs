// Airlock Controller (PB / MDK, C#6-safe)
// Commands: (none)=summary | list | rescan | test <ID> | enter <ID> | exit <ID>
//
// Tags per airlock ID (e.g., A1, B1):
//   Doors: [A1:Inner] / [A1:Outer]
//   Vent(s): [A1:Vent]
//   Process tank(s): [A1:ProcessTank]   (the same O2 tank may be tagged for multiple IDs: [A1:ProcessTank][B1:ProcessTank])
// Globals:
//   Base O2 tanks (optional explicit): [BaseTank]
//   O2/H2 generators: [O2H2]
// Optional lights per ID: [A1:Light]

using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Text;
using VRage.Game;   // DoorStatus, UpdateType
using VRageMath;   // Color

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        // ---------------- Config ----------------
        // Use readonly (not const) for booleans so the compiler won't fold branches and warn about unreachable code.
        readonly bool  LOCK_MANUAL   = false; // set true to disable doors (F key) when idle
        const  float PRESS_OK       = 0.90f;  // 90% room O2 is considered "good"
        const  float VAC_OK         = 0.02f;  // ~2% O2 considered vacuum
        const  float MIN_O2_DELTA   = 0.01f;  // require ≥1% O2 rise before accepting pressurize
        const  float MIN_PROC_DELTA = 0.0005f; // minimal tank fill change considered "capturing"
        const  int   WAIT_SHORT     = 10;     // ~1.6s
        const  int   WAIT_PASS      = 30;     // ~5s to step through a door
        const  int   TIMEOUT_TICKS  = 300;    // ~50s safety cap
        const  int   MIN_DEPRESS_TICKS = 30;  // ≥5s before opening OUTER
        const  int   MIN_PRESS_TICKS   = 30;  // ≥5s before opening INNER
        const  int   SETTLE_TICKS   = 3;      // settle after flipping supply/sink
        const  int   STABLE_TICKS   = 3;      // O2 must be stable/rising this many ticks

        //---------------- Non Airlock AutoClose Config ----------------
        readonly bool AUTO_CLOSE_ENABLED          = true;
        const  int   AUTO_CLOSE_DEFAULT_SECONDS   = 10;
        const  int   TICKS_PER_SECOND             = 6;

        enum Mode { None, Exit, Enter }
        enum Step
        {
            Idle,
            // Exit prelude (inner opens first while pressurized so you can step in)
            ExitOpenInner, ExitWaitIn, ExitCloseInner,
            // Shared sequence
            Seal, Isolate, DepressInit, Depressurize, OpenOuter, WaitOuter, CloseOuter,
            PressInit, Pressurize, OpenInner, WaitInner, CloseInner,
            Restore, Done
        }

        class LightState
        {
            public Color Color;
            public float BlinkInterval;
            public float BlinkLength;
            public float BlinkOffset;
            public bool Enabled;
        }

        class AutoDoor
        {
            public IMyDoor Door;
            public int LimitTicks;
            public int Counter;

            public AutoDoor(IMyDoor d, int limit)
            {
                Door = d;
                LimitTicks = limit;
                Counter = 0;
            }
        }
        readonly List<AutoDoor> _autoDoors = new  List<AutoDoor>();

        class RunCtx
        {
            public string Id;
            public Mode Mode;
            public Step Step;
            public int Wait;
            public int Timeout;
            public int Stable;
            public AirlockRec Al;
            public IMyGasTank Proc;
            public float StartO2;
            public float LastO2;
            public float ProcStart;
            public float ProcLast;
            public Dictionary<IMyLightingBlock, LightState> LightBackup =
                new Dictionary<IMyLightingBlock, LightState>();
        }

        RunCtx _run = null;

        // ---------------- Model ----------------
        class AirlockRec
        {
            public string Id { get; }
            public List<IMyDoor> Inner  = new List<IMyDoor>();      // base-side
            public List<IMyDoor> Outer  = new List<IMyDoor>();      // space-side
            public List<IMyAirVent> Vents = new List<IMyAirVent>();
            public List<IMyButtonPanel> ButtonsInner = new List<IMyButtonPanel>();
            public List<IMyButtonPanel> ButtonsOuter = new List<IMyButtonPanel>();
            public List<IMySensorBlock>  SensorInner  = new List<IMySensorBlock>();
            public List<IMySensorBlock>  SensorOuter  = new List<IMySensorBlock>();
            public List<IMyLightingBlock> Lights = new List<IMyLightingBlock>();
            public List<IMyGasTank> _processTanks = new List<IMyGasTank>();
            public AirlockRec(string id) { Id = id; }
        }

        readonly Dictionary<string, AirlockRec> _locks =
            new Dictionary<string, AirlockRec>(StringComparer.OrdinalIgnoreCase);
        readonly List<IMyGasTank> _baseTanks = new List<IMyGasTank>();
        readonly List<IMyGasGenerator> _gens = new List<IMyGasGenerator>();

        // ---------------- Lifecycle ----------------
        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10; // ~6x/sec
            ScanAll();                 // one-time discovery
            DisableAllProcessTanks();  // keep process tanks isolated when idle
            foreach (var kv in _locks) { DisableDoors(kv.Value.Inner); DisableDoors(kv.Value.Outer); }
            Echo("Args: list | rescan | enter <ID> | exit <ID>");
        }
        public void Save() { }

        public void Main(string argument, UpdateType updateSource)
        {
            if ((updateSource & UpdateType.Update10) != 0)
            {
                AutoCloseTick();
                if (_run != null) TickRun();
                return;
            }

            var a = (argument ?? "").Trim();
            if (a.Length == 0) { PrintSummary(); return; }
            if (string.Equals(a, "rescan", StringComparison.OrdinalIgnoreCase)) { ScanAll(); DisableAllProcessTanks(); Echo("Rescanned.\n"); PrintSummary(); return; }
            if (string.Equals(a, "list",   StringComparison.OrdinalIgnoreCase)) { PrintDetails(); return; }

            var parts = a.Split(' ');
            if (parts.Length == 2)
            {
                if (string.Equals(parts[0], "enter", StringComparison.OrdinalIgnoreCase)) { StartEnter(parts[1]); return; }
                if (string.Equals(parts[0], "exit",  StringComparison.OrdinalIgnoreCase)) { StartExit(parts[1]);  return; }
                if (string.Equals(parts[0], "test",  StringComparison.OrdinalIgnoreCase)) { TestSeal(parts[1]);   return; }
            }

            Echo("Args: (none)=summary | list | rescan | test <ID> | enter <ID> | exit <ID>");
        }

        // ---------------- Discovery ----------------
        void ScanAll()
        {  
            _locks.Clear(); _baseTanks.Clear(); _gens.Clear();  
  
            var all = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(all);

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                foreach (var tag in TagsOf(b.CustomName))
                {
                    if (tag.Count >= 2)
                    {
                        var kind = tag[1].ToUpperInvariant();
                        if (kind=="INNER"||kind=="OUTER"||kind=="VENT"||kind=="INNERBUTTON"||kind=="OUTERBUTTON"||
                            kind=="INNERSENSOR"||kind=="OUTERSENSOR"||kind=="LIGHT"||kind=="PROCESSTANK")
                            ids.Add(tag[0]);
                    }
                }
            }
            foreach (var id in ids) _locks[id] = new AirlockRec(id);

            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (!b.IsSameConstructAs(Me)) continue;

                var nameU = (b.CustomName ?? "").ToUpperInvariant();

                var gt = b as IMyGasTank;
                if (gt != null && nameU.IndexOf("[BASETANK]", StringComparison.Ordinal) >= 0) _baseTanks.Add(gt);

                var g = b as IMyGasGenerator;
                if (g != null && nameU.IndexOf("[O2H2]", StringComparison.Ordinal) >= 0) _gens.Add(g);

                foreach (var tag in TagsOf(b.CustomName))
                {
                    if (tag.Count == 0) continue;
                    AirlockRec al; if (!_locks.TryGetValue(tag[0], out al)) continue;

                    var d = b as IMyDoor;
                    if (d != null && tag.Count >= 2)
                    {
                        var side = tag[1].ToUpperInvariant();
                        if (side == "INNER") al.Inner.Add(d);
                        else if (side == "OUTER") al.Outer.Add(d);
                    }

                    var v = b as IMyAirVent;
                    if (v != null && tag.Count >= 2 && string.Equals(tag[1], "VENT", StringComparison.OrdinalIgnoreCase))
                        al.Vents.Add(v);

                    var bp = b as IMyButtonPanel;
                    if (bp != null && tag.Count >= 2)
                    {
                        var k = tag[1].ToUpperInvariant();
                        if (k == "INNERBUTTON") al.ButtonsInner.Add(bp);
                        else if (k == "OUTERBUTTON") al.ButtonsOuter.Add(bp);
                    }

                    var s = b as IMySensorBlock;
                    if (s != null && tag.Count >= 2)
                    {
                        var k = tag[1].ToUpperInvariant();
                        if (k == "INNERSENSOR") al.SensorInner.Add(s);
                        else if (k == "OUTERSENSOR") al.SensorOuter.Add(s);
                    }

                    var lb = b as IMyLightingBlock;
                    if (lb != null && tag.Count >= 2 && string.Equals(tag[1], "LIGHT", StringComparison.OrdinalIgnoreCase))
                        al.Lights.Add(lb);

                    var pt = b as IMyGasTank;
                    if (pt != null && tag.Count >= 2 && string.Equals(tag[1], "PROCESSTANK", StringComparison.OrdinalIgnoreCase))
                        al._processTanks.Add(pt);
                }
            }

            if (_baseTanks.Count == 0)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var t2 = all[i] as IMyGasTank;
                    if (t2 == null || !t2.IsSameConstructAs(Me)) continue;

                    bool isProc = false;
                    foreach (var kv in _locks)
                    {
                        var list = kv.Value._processTanks;
                        for (int j = 0; j < list.Count; j++)
                            if (list[j] == t2) { isProc = true; break; }
                        if (isProc) break;
                    }
                    if (!isProc) _baseTanks.Add(t2);
                }
            }

            // << moved out of the per-block loop >>
            RefreshAutoDoors();
        }

        // ---------------- Process-tank isolation ----------------
        void DisableAllProcessTanks()
        {
            foreach (var kv in _locks)
            {
                var list = kv.Value._processTanks;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t != null && t.IsSameConstructAs(Me))
                    {
                        t.Stockpile = false;
                        t.Enabled = false;
                    }
                }
            }
        }

        // ---------------- Lights ----------------
        void LightsBlinkRed(List<IMyLightingBlock> ls)
        {
            for (int i = 0; i < ls.Count; i++)
            {
                var l = ls[i];
                var fb = l as IMyFunctionalBlock; if (fb != null) fb.Enabled = true;
                l.Color = new Color(255, 0, 0);
                l.BlinkIntervalSeconds = 1.0f;
                l.BlinkLength = 60f;
                l.BlinkOffset = 0f;
            }
        }
        void LightsNormal(List<IMyLightingBlock> ls)
        {
            for (int i = 0; i < ls.Count; i++)
            {
                var l = ls[i];
                l.BlinkLength = 0f; l.BlinkIntervalSeconds = 0f; l.BlinkOffset = 0f;
                l.Color = new Color(255, 255, 255);
            }
        }

        // ---------------- Start enter/exit ----------------
        void StartEnter(string id)
        {
            AirlockRec al; if (!_locks.TryGetValue(id, out al)) { Echo("Unknown ID: " + id); return; }
            if (_run != null) { Echo("Busy"); return; }
            var proc = PickProcessTank(al, false); if (proc == null) { Echo("No ProcessTank for " + id); return; }
            _run = new RunCtx { Id = id, Mode = Mode.Enter, Step = Step.Seal, Al = al, Proc = proc };
        }
        void StartExit(string id)
        {
            AirlockRec al; if (!_locks.TryGetValue(id, out al)) { Echo("Unknown ID: " + id); return; }
            if (_run != null) { Echo("Busy"); return; }
            var proc = PickProcessTank(al, false); if (proc == null) { Echo("No ProcessTank for " + id); return; }
            _run = new RunCtx { Id = id, Mode = Mode.Exit, Step = Step.ExitOpenInner, Al = al, Proc = proc };
        }

        IMyGasTank PickProcessTank(AirlockRec al, bool preferFullest)
        {
            var cand = new List<IMyGasTank>();
            for (int i = 0; i < al._processTanks.Count; i++)
                if (al._processTanks[i] != null && al._processTanks[i].IsSameConstructAs(Me)) cand.Add(al._processTanks[i]);
            if (cand.Count == 0) return null;

            IMyGasTank best = cand[0];
            var bestVal = best.FilledRatio;
            for (int i = 1; i < cand.Count; i++)
            {
                var t = cand[i];
                var v = t.FilledRatio;
                if (preferFullest ? (v > bestVal) : (v < bestVal)) { best = t; bestVal = v; }
            }
            return best;
        }

        // ---------------- State machine ----------------
        void TickRun()
        {
            // Trace progress each tick
            Echo("[" + _run.Id + "] " + _run.Mode + " -> " + _run.Step + " t=" + _run.Timeout + " O2=" + RoomO2(_run.Al).ToString("0.00"));

            if (_run.Wait > 0) { _run.Wait--; return; }

            switch (_run.Step)
            {
                // EXIT prelude: inner opens first (pressurized) so you can step in
                case Step.ExitOpenInner:
                    CloseAll(_run.Al.Outer);
                    SetDepressurize(_run.Al.Vents, false);
                    OpenAll(_run.Al.Inner);
                    if (AllOpen(_run.Al.Inner)) Next(Step.ExitWaitIn, WAIT_PASS);
                    break;

                case Step.ExitWaitIn:
                    Next(Step.ExitCloseInner, WAIT_SHORT);
                    break;

                case Step.ExitCloseInner:
                    CloseAll(_run.Al.Inner);
                    if (AllClosed(_run.Al.Inner)) Next(Step.Seal, 1);
                    break;

                // Shared sequence
                case Step.Seal:
                    CloseAll(_run.Al.Inner); CloseAll(_run.Al.Outer);
                    SetDepressurize(_run.Al.Vents, false);
                    if (AllClosed(_run.Al.Inner) && AllClosed(_run.Al.Outer)) Next(Step.Isolate, 1);
                    break;

                case Step.Isolate:
                    SetEnabled(_gens, false);
                    for (int i = 0; i < _baseTanks.Count; i++) _baseTanks[i].Enabled = false;
                    DisableAllProcessTanks();

                    _run.Proc.Enabled = true;
                    _run.Proc.Stockpile = true;  // capture air first
                    _run.Timeout = 0; _run.Stable = 0;
                    _run.StartO2 = RoomO2(_run.Al);
                    _run.LastO2  = _run.StartO2;
                    _run.ProcStart = ProcFill(_run.Proc);
                    _run.ProcLast  = _run.ProcStart;
                    SaveLightState(_run);
                    LightsBlinkRed(_run.Al.Lights);
                    Next(Step.DepressInit, 1);
                    break;

                case Step.DepressInit:
                    SetDepressurize(_run.Al.Vents, true);
                    Next(Step.Depressurize, SETTLE_TICKS); // let conveyors/vent settle
                    break;

                case Step.Depressurize:
                {
                    _run.Timeout++;
                    if (_run.Timeout == 1) DebugIsolation(_run, true); // show base/proc tank states

                    float o2 = RoomO2(_run.Al);
                    float dO2 = _run.LastO2 - o2;
                    _run.LastO2 = o2;

                    float proc = ProcFill(_run.Proc);
                    float dProc = proc - _run.ProcLast;
                    _run.ProcLast = proc;

                    // Progress if room O2 falling or process tank fill rising
                    bool capturing = (dO2 > 0.001f) || (dProc > MIN_PROC_DELTA);

                    if (_run.Timeout >= MIN_DEPRESS_TICKS &&
                        (o2 <= VAC_OK || _run.Timeout >= TIMEOUT_TICKS || (capturing && o2 <= (VAC_OK + 0.05f))))
                    {
                        Next(Step.OpenOuter, 1);
                    }
                    else if (_run.Timeout % 30 == 0 && !capturing)
                    {
                        Echo("WARN " + _run.Id + ": No capture — check conveyor path & OXYGEN tank.");
                    }
                    break;
                }

                case Step.OpenOuter:
                    OpenAll(_run.Al.Outer);
                    if (AllOpen(_run.Al.Outer)) Next(Step.WaitOuter, WAIT_PASS);
                    break;

                case Step.WaitOuter:
                    Next(Step.CloseOuter, WAIT_SHORT);
                    break;

                case Step.CloseOuter:
                    CloseAll(_run.Al.Outer);
                    if (AllClosed(_run.Al.Outer))
                    {
                        _run.Proc.Stockpile = false; _run.Proc.Enabled = true; // supply now
                        Next(Step.PressInit, SETTLE_TICKS);
                    }
                    break;

                case Step.PressInit:
                    SetDepressurize(_run.Al.Vents, false);
                    _run.StartO2 = RoomO2(_run.Al); // baseline AFTER enabling supply
                    _run.LastO2  = _run.StartO2;
                    _run.Timeout = 0; _run.Stable = 0;
                    Next(Step.Pressurize, 1);
                    break;

                case Step.Pressurize:
                {
                    if (_run.Timeout == 0) DebugIsolation(_run, false); // show base/proc tank states

                    _run.Proc.Enabled = true; _run.Proc.Stockpile = false;
                    for (int i = 0; i < _baseTanks.Count; i++) _baseTanks[i].Enabled = false;
                    SetEnabled(_gens, false);

                    float o2 = RoomO2(_run.Al);
                    if (o2 >= _run.LastO2 - 0.001f) _run.Stable++; else _run.Stable = 0; // stable/rising
                    _run.LastO2 = o2;
                    _run.Timeout++;

                    bool hasRisen = o2 >= _run.StartO2 + MIN_O2_DELTA;
                    bool stableOk = _run.Stable >= STABLE_TICKS;
                    bool fullOk   = (o2 >= PRESS_OK) && stableOk;

                    if (_run.Timeout >= MIN_PRESS_TICKS &&
                        (fullOk || (hasRisen && AnyVentCanPressurize(_run.Al) && stableOk) || _run.Timeout >= TIMEOUT_TICKS))
                    {
                        if (_run.Mode == Mode.Enter) Next(Step.OpenInner, 1);
                        else Next(Step.Restore, 1);
                    }
                    break;
                }

                case Step.OpenInner:
                    OpenAll(_run.Al.Inner);
                    if (AllOpen(_run.Al.Inner)) Next(Step.WaitInner, WAIT_PASS);
                    break;

                case Step.WaitInner:
                    Next(Step.CloseInner, WAIT_SHORT);
                    break;

                case Step.CloseInner:
                    CloseAll(_run.Al.Inner);
                    if (AllClosed(_run.Al.Inner)) Next(Step.Restore, 1);
                    break;

                case Step.Restore:
                    SetDepressurize(_run.Al.Vents, false);
                    LightsRestore(_run);

                    _run.Proc.Stockpile = false; _run.Proc.Enabled = false; // process tank OFF
                    for (int i = 0; i < _baseTanks.Count; i++) _baseTanks[i].Enabled = true;
                    SetEnabled(_gens, true);

                    DisableDoors(_run.Al.Inner);
                    DisableDoors(_run.Al.Outer);

                    Next(Step.Done, 1);
                    break;

                case Step.Done:
                    Echo("Done " + _run.Id + " (" + _run.Mode + ")");
                    _run = null;
                    break;
            }
        }

        void Next(Step s, int delay) { _run.Step = s; _run.Wait = delay; _run.Timeout = 0; }

        // ---------------- Helpers ----------------
        float ProcFill(IMyGasTank t) { return (float)t.FilledRatio; }

        void EnsureDoorCtrl(IMyDoor d)
        {
            var fb = d as IMyFunctionalBlock;
            if (LOCK_MANUAL && fb != null && !fb.Enabled) fb.Enabled = true;
        }
        void OpenAll(List<IMyDoor> ds)  { for (int i=0;i<ds.Count;i++){ EnsureDoorCtrl(ds[i]); ds[i].OpenDoor(); } }
        void CloseAll(List<IMyDoor> ds) { for (int i=0;i<ds.Count;i++){ EnsureDoorCtrl(ds[i]); ds[i].CloseDoor(); } }

        void DisableDoors(List<IMyDoor> ds)
        {
            if (LOCK_MANUAL)
            {
                for (int i=0;i<ds.Count;i++)
                {
                    var fb = ds[i] as IMyFunctionalBlock;
                    if (fb != null) fb.Enabled = false;
                }
            }
        }

        bool AllOpen(List<IMyDoor> ds)   { for (int i=0;i<ds.Count;i++) if (ds[i].Status!=DoorStatus.Open)   return false; return true; }
        bool AllClosed(List<IMyDoor> ds) { for (int i=0;i<ds.Count;i++) if (ds[i].Status!=DoorStatus.Closed) return false; return true; }

        void SetDepressurize(List<IMyAirVent> vs, bool dep) { for (int i=0;i<vs.Count;i++) vs[i].Depressurize = dep; }
        bool AnyVentCanPressurize(AirlockRec al) { for (int i=0;i<al.Vents.Count;i++) if (al.Vents[i].CanPressurize) return true; return false; }
        float RoomO2(AirlockRec al)
        {
            if (al.Vents.Count == 0) return 0f;
            float sum=0f; for (int i=0;i<al.Vents.Count;i++) sum+=al.Vents[i].GetOxygenLevel(); return sum/al.Vents.Count;
        }

        void SetEnabled<T>(List<T> blocks, bool on) where T : class, IMyTerminalBlock
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                var fb = blocks[i] as IMyFunctionalBlock;
                if (fb != null) fb.Enabled = on;
                else blocks[i].ApplyAction(on ? "OnOff_On" : "OnOff_Off");
            }
        }

        // Isolation debug to verify base tanks OFF / process tank ON(+Stockpile when capturing)
        void DebugIsolation(RunCtx r, bool expectStockpile)
        {
            int baseOn = 0;
            for (int i = 0; i < _baseTanks.Count; i++)
            {
                var fb = _baseTanks[i] as IMyFunctionalBlock;
                if (fb != null && fb.Enabled) baseOn++;
            }
            var pf = r.Proc as IMyFunctionalBlock;
            bool pOn  = (pf != null && pf.Enabled);
            bool pStk = r.Proc.Stockpile;

            Echo($"ISO {r.Id}: baseOn={baseOn}, procOn={(pOn ? 1:0)}, procStockpile={(pStk?1:0)}");
            if (expectStockpile && (!pOn || !pStk))
                Echo("  -> Need proc: ON+STOCKPILE and all base tanks OFF");
            if (!expectStockpile && (!pOn || pStk))
                Echo("  -> Need proc: ON (not stockpile) and all base tanks OFF");
        }

        // ---------------- Utilities ----------------
        void TestSeal(string id)
        {
            AirlockRec al; if (!_locks.TryGetValue(id, out al)) { Echo("Unknown ID: " + id); return; }
            CloseAll(al.Inner); CloseAll(al.Outer); SetDepressurize(al.Vents, false);
            Echo("Sealed " + id);
        }

        void PrintSummary()
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("[Discovery Summary]");
            foreach (var kv in _locks)
            {
                var al = kv.Value;
                sb.AppendLine(
                    " - " + al.Id + ": " +
                    "Inner=" + al.Inner.Count + ", Outer=" + al.Outer.Count + ", Vents=" + al.Vents.Count + ", " +
                    "ButtonsInner=" + al.ButtonsInner.Count + ", ButtonsOuter=" + al.ButtonsOuter.Count + ", " +
                    "SensorsInner=" + al.SensorInner.Count + ", SensorsOuter=" + al.SensorOuter.Count + ", " +
                    "Lights=" + al.Lights.Count + ", ProcessTanks=" + al._processTanks.Count
                );
            }
            sb.AppendLine();
            sb.AppendLine("BaseTanks=" + _baseTanks.Count + ", O2H2=" + _gens.Count);
            sb.AppendLine("AutoCloseDoors=" + _autoDoors.Count);
            Echo(sb.ToString());
        }

        void PrintDetails()
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine("[Discovery Details]");
            foreach (var kv in _locks)
            {
                var al = kv.Value;
                sb.AppendLine("> " + al.Id);
                AppendNames(sb, "  Inner",         al.Inner);
                AppendNames(sb, "  Outer",         al.Outer);
                AppendNames(sb, "  Vents",         al.Vents);
                AppendNames(sb, "  ButtonsInner",  al.ButtonsInner);
                AppendNames(sb, "  ButtonsOuter",  al.ButtonsOuter);
                AppendNames(sb, "  SensorsInner",  al.SensorInner);
                AppendNames(sb, "  SensorsOuter",  al.SensorOuter);
                AppendNames(sb, "  Lights",        al.Lights);
                AppendNames(sb, "  ProcessTanks",  al._processTanks);
                sb.AppendLine();
            }
            AppendNames(sb, "BaseTanks", _baseTanks);
            AppendNames(sb, "O2H2",      _gens);
            Echo(sb.ToString());
        }

        void AppendNames<T>(StringBuilder sb, string label, List<T> list) where T : class, IMyTerminalBlock
        {
            sb.Append(label).Append(" (").Append(list.Count).Append("): ");
            if (list.Count == 0) { sb.AppendLine("(none)"); return; }
            for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(list[i].CustomName); }
            sb.AppendLine();
        }

        IEnumerable<List<string>> TagsOf(string name)
        {
            var s = name ?? "";
            int i = 0;
            while (i < s.Length)
            {
                int a = s.IndexOf('[', i);
                if (a < 0) yield break;
                int b = s.IndexOf(']', a + 1);
                if (b < 0) yield break;
                var inside = s.Substring(a + 1, b - a - 1);
                var parts = SplitParts(inside);
                if (parts.Count > 0) yield return parts;
                i = b + 1;
            }
        }
        List<string> SplitParts(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            var parts = raw.Split(':');
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length > 0) list.Add(p);
            }
            return list;
        }
        void SaveLightState(RunCtx r) {
            r.LightBackup.Clear();
            for (int i = 0; i < r.Al.Lights.Count; i++) {
                var l  = r.Al.Lights[i];
                var fb = l as IMyFunctionalBlock;
                var st = new LightState();
                st.Color         = l.Color;
                st.BlinkInterval = l.BlinkIntervalSeconds;
                st.BlinkLength   = l.BlinkLength;
                st.BlinkOffset   = l.BlinkOffset;
                st.Enabled       = (fb != null ? fb.Enabled : true);
                r.LightBackup[l] = st;
            }
        }

        void LightsRestore(RunCtx r) {
            for (int i = 0; i < r.Al.Lights.Count; i++) {
                var l = r.Al.Lights[i];
                LightState st;
                if (r.LightBackup.TryGetValue(l, out st)) {
                    l.Color = st.Color;
                    l.BlinkIntervalSeconds = st.BlinkInterval;
                    l.BlinkLength = st.BlinkLength;
                    l.BlinkOffset = st.BlinkOffset;
                    var fb = l as IMyFunctionalBlock;
                    if (fb != null) fb.Enabled = st.Enabled;
                } else {
                    // fallback if no snapshot
                    l.BlinkLength = 0f; l.BlinkIntervalSeconds = 0f; l.BlinkOffset = 0f;
                    l.Color = new Color(255,255,255);
                }
            }
        }

        void RefreshAutoDoors()
        {
            _autoDoors.Clear();
            //Collect Airlock Doors to Exclude
            var air = new HashSet<IMyDoor>();
            foreach (var kv in _locks)
            {
                var al = kv.Value;
                for(int i = 0; i < al.Inner.Count; i++) air.Add(al.Inner[i]);
                for(int i = 0; i < al.Outer.Count; i++) air.Add(al.Outer[i]);
            }
            var all = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(all);
            int defaultTicks = AUTO_CLOSE_DEFAULT_SECONDS * TICKS_PER_SECOND;

            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i] as IMyDoor;
                if(d == null || !d.IsSameConstructAs(Me)) continue;
                if(air.Contains(d)) continue;

                bool skip = false;
                int limit = defaultTicks;

                foreach (var tag in TagsOf(d.CustomName))
                {
                    if(tag.Count == 0) continue;
                    string k = tag[0].ToUpperInvariant();
                    if (k == "NOAUTOCLOSE" || k=="KEEPOPEN")
                    {
                        skip = true;
                        break;
                    }

                    if (k == "AUTOCLOSE" && tag.Count >= 2)
                    {
                        int s;
                        if (int.TryParse(tag[1], out s) && s > 0) limit = s * TICKS_PER_SECOND;
                    }
                }
                if(!skip) _autoDoors.Add(new AutoDoor(d,limit));
            }
        }

        void AutoCloseTick()
        {
            if(!AUTO_CLOSE_ENABLED) return;
            for (int i = 0; i < _autoDoors.Count; i++)
            {
                var ad = _autoDoors[i];
                if (ad.Door == null || !ad.Door.IsSameConstructAs(Me))
                {
                    ad.Counter = 0;
                    continue;
                }

                var st = ad.Door.Status;
                if (st == DoorStatus.Open)
                {
                    ad.Counter++;
                    if (ad.Counter >= ad.LimitTicks)
                    {
                        ad.Door.CloseDoor();
                        ad.Counter = 0;
                    }
                }
                else
                {
                    ad.Counter = 0;
                }
            }
        }
    }
}
