# Airlock Controller (Space Engineers)

A programmable-block script that runs **multiple airlocks** by name, captures air into a **per-airlock “process tank”**, restores it on re-entry, blinks lights during the cycle, and **auto-closes non-airlock doors** after a delay. Verbose on-screen debug shows live state (mode/step/O₂).


---
## Note

- If you wish to make modifications to parameters, or the scritp itself you will need to pull the MDK Nuget packages
  https://github.com/malforge/mdk2/wiki
- I use Rider for writing anything in C# https://github.com/malforge/mdk2/wiki/Getting-Started-using-Jetbrains-Rider
- Make sure you modify the ini ifiles here accordingly the version I deploy needs to be minified as it contains 
far too many characters. (Look in the ini files and make sure you set them up accordingly)
- Shoutout for doing the build work and writing the libs goes to Malware, thanks!

---

## ✨ Features

- Multiple, named airlocks on one grid (A1, B1, …) via simple block name tags
- Lossless cycles using per-airlock **O₂ ProcessTank(s)**
- **Exit** flow opens **Inner first** (while pressurized) so you can step in
- **Enter** flow pressurizes before opening Inner
- Lights blink **red** during cycle; original color/blink restored afterward
- **Auto-close** for all non-airlock doors, with per-door overrides
- Debug `Echo` shows mode/step, O₂ %, and isolation checks

> The script only controls blocks on the **same construct** (grid) as the PB.

---

## 🏷️ Block Tagging

Pick an ID per airlock (`A1`, `B1`, …) and add tags in **square brackets** to block names.

### Per-airlock (replace `A1` with your ID)
- Doors (base side vs space side):  
  `[A1:Inner]`, `[A1:Outer]`
- Vent(s) in the chamber:  
  `[A1:Vent]`
- Process tank(s) **(OXYGEN tank only; can be shared across IDs): Must be built and toggled off (it should remain off
the script will handle all f rom there, never turn it off and never set it to stockpile on your own)**  
  `[A1:ProcessTank]`
- Optional lights that blink during cycle:  
  `[A1:Light]`
- Optional inputs (discovered for future use):  
  `[A1:InnerButton]`, `[A1:OuterButton]`, `[A1:InnerSensor]`, `[A1:OuterSensor]`

### Global (base-wide)
- Base O₂ tanks (optional; otherwise auto-detected):  
  `[BaseTank]`
- O₂/H₂ generators:  
  `[O2H2]`

### Auto-close (non-airlock doors)
- Enable default auto-close (10s):  
  `[AutoClose]`
- Custom delay:  
  `[AutoClose:30]`  ← seconds
- Opt-out / keep open:  
  `[NoAutoClose]` or `[KeepOpen]`

#### Example
`Where you  put brackets in the custom name does not matter`

[A1:Inner] Airlock A1 – Inner Door

[A1:Outer] Airlock A1 – Outer Door

[A1:Vent] Airlock A1 – Vent

[A1:Light] Airlock A1 – Indicator

OxgenTankP[A1:ProcessTank]  or [B1:ProcessTank]OxgenTankP 
`A process tank can be shared across airlocks, keep in mind it need to be able to take in air so make sure you have
enough Volume to capture the Oxygen. Only 1 tank can be used per airlock`

[BaseTank] Main O₂ Tank #1

[O2H2] Base O₂/H₂ Generator #1

[AutoClose:20] Refinery Room Door

[KeepOpen] Hangar Main Door


---

## 🕹️ Commands (run the PB with these arguments)

- `rescan` — rediscover blocks
- `list` — detailed listing
- *(no argument)* — summary
- `test <ID>` — **seal** an airlock (close both doors; vents `Depressurize = Off`)
- `enter <ID>` — run **enter** cycle for airlock `ID`
- `exit <ID>` — run **exit** cycle for airlock `ID`

> Tip: bind Button Panels to `enter A1` / `exit A1`.

---

## 🔁 What the script does

### Exit (go outside)
1. **Open Inner** (pressurized) → wait → **Close Inner**.
2. **Seal** both doors.
3. **Isolate gas** on the base:
    - O₂/H₂ generators **Off**
    - Base O₂ tanks **Off**
    - Selected **ProcessTank**: **Enabled = On**, **Stockpile = On** (captures air)
4. **Depressurize** chamber (`Depressurize = On`) until near-vacuum or timeout.
5. **Open Outer** → wait → **Close Outer**.
6. **Pressurize** using the ProcessTank (**Stockpile = Off**, supply) with vents `Depressurize = Off`.
7. **Restore**: lights back, ProcessTank **Off**, base tanks/gens **On**.

### Enter (come inside)
1. **Seal** → **Isolate gas** (as above).
2. **Pressurize** chamber from ProcessTank (supply; base tanks/gens off).
3. **Open Inner** → wait → **Close Inner**.
4. **Restore** (lights, enable base tanks/gens, process tank off).

Lights tagged `[ID:Light]` blink **red** during the cycle; their original color/blink/enabled state is restored at the end.

---

## 🚪 Auto-close (non-airlock doors)

- All doors **not tagged** as `[ID:Inner]` / `[ID:Outer]` are tracked.
- Default close delay is **10s** (configurable).
- Per-door overrides via `[AutoClose:<seconds>]`; disable with `[NoAutoClose]` / `[KeepOpen]`.

---

## 🚀 Docked Ships & Conveyors (important)

Depressurizing pushes O₂ into the **entire connected conveyor network**.

- If a docked ship has enabled O₂ tanks, it will **soak up air**.
- **Recommended practice:** on each ship, use a Timer/Event Controller tied to connector state:
    - **When connected:** set ship O₂ tanks **Stockpile = On** (or **Enabled = Off**)
    - **When disconnected:** set **Stockpile = Off** (or re-enable)

> **Note:** If your **ProcessTank is unexpectedly filling with air**, you likely have a **docked ship** whose oxygen tanks are **enabled** or **not set to Stockpile**. Ensure connected ships isolate their tanks as above.

---

## 🧰 Tuning (constants in the script)

- `PRESS_OK` (default **0.90**) — min O₂ % to open Inner
- `VAC_OK` (default **0.02**) — target vacuum O₂ level
- `MIN_O2_DELTA` (default **0.01**) — require ≥1% O₂ rise to accept pressurize
- `MIN_DEPRESS_TICKS` / `MIN_PRESS_TICKS` — minimum time in depress/press before doors open
- `AUTO_CLOSE_DEFAULT_SECONDS` — default non-airlock door auto-close delay
- `LOCK_MANUAL` — if `true`, script disables door “F key” when idle to discourage manual opening

---

## 🧪 Troubleshooting

- **Outer door never opens on Exit**  
  Vent must be `Depressurize = On`; ProcessTank must be **Enabled + Stockpile**.  
  If room O₂ isn’t dropping **and** tank fill isn’t rising, the conveyor path to the **Oxygen** ProcessTank is broken.

- **Room never reaches PRESS_OK on Enter**  
  ProcessTank must be **Enabled** and **Stockpile = Off** (supply).  
  Base O₂ tanks and O₂/H₂ generators remain **Off** until restore.  
  Vent(s) must show **Can Pressurize = Yes** (no leaks, correct orientation).

- **ProcessTank fills “by itself” during depressurize**  
  This usually means a **docked ship** is connected and its tanks are **enabled** or **not stockpiling**.  
  Isolate ship tanks when connected (see “Docked Ships & Conveyors”).

- **Normalize a chamber quickly**  
  Run `test <ID>` to close both doors and set vents `Depressurize = Off`.

---

## 📈 Performance

- Runs at `Update10` (~6 Hz) for smooth timing & O₂ checks.
- `rescan` is a one-time pass over blocks; normal ticks are lightweight.
- Auto-close iterates only the tracked non-airlock doors.

---

## 🧭 Quick Setup Checklist

- [ ] Doors tagged: `[ID:Inner]`, `[ID:Outer]`
- [ ] Vent(s) tagged: `[ID:Vent]` and looking into the chamber
- [ ] Oxygen ProcessTank tagged: `[ID:ProcessTank]` (O₂ tank)
- [ ] Base O₂ tanks tagged `[BaseTank]` (optional) or rely on auto-detect
- [ ] O₂/H₂ generators tagged `[O2H2]`
- [ ] (Optional) `[ID:Light]` for indicators
- [ ] (Optional) Auto-close tags on other doors
- [ ] (Docked ships) Ship timer sets O₂ tanks **Stockpile On** when connected

---

## 🔗 Example Button Actions

- Inside base: `enter A1`
- Exterior side: `exit A1`
- Maintenance: `rescan`, `list`, `test A1`



