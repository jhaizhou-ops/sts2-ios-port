# StS2 Mobile Patch Catalog — iOS Porting Reference

Source: `src/STS2MobileIos/Patches/` + `PatchHelper.cs`
Generated: 2026-07-08

## How targets are resolved (critical for static weaving)

`PatchHelper.Patch()` resolves the **target method by name string** via
`targetType.GetMethod(methodName, flags)` with all binding flags. The **target type** is resolved
two ways, mixed throughout:

1. `typeof(...)` compile-time references for types the mod project links against
   (`NGame`, `SaveManager`, `ModelDb`, `ModManager`, `CloudSaveStore`, `GodotFileIo`,
   `OsDebugInfo`, `PrefsSave`, `SentryService`, `ProgressSaveManager`).
2. **Assembly-qualified name strings** via `sts2Asm.GetType("MegaCrit.Sts2...")` for types not
   referenced at compile time (most UI screen types, `NBackgroundModeHandler`, `NCombatRoom`,
   `NJoinFriendScreen`, etc.).

`PatchHelper.PatchGetter()` resolves property getters via `GetProperty(name).GetGetMethod(true)`.
`PatchHelper.PatchCritical()` is identical to `Patch()` but throws instead of logging (used by
LauncherPatches only). Note: `GetMethod(name)` with no parameter types will throw
`AmbiguousMatchException` if the target has overloads — a static weaver must resolve by
(declaring type full name, method name) and disambiguate overloads the same way (currently none
of the targets are overloaded, but this is an implicit assumption).

Hook methods are resolved by `PatchHelper.Method(type, name)` — also a name string, public static
only. A static IL weaver has everything it needs from the string pairs below; no signatures are
encoded anywhere except implicitly in the hook method parameter lists.

**Global NativeAOT caveat**: Harmony itself (`harmony.Patch`, `HarmonyMethod`) is runtime codegen
(MonoMod/DynamicMethod under the hood). *Every* patch in this catalog breaks under iOS NativeAOT
as-is; the point of this catalog is what must be re-expressed as static IL weaving. The per-file
"JIT tricks" sections flag what is problematic *beyond* that baseline.

---

## AppLifecyclePatches.cs

**Purpose**: Handle app background/foreground: mute audio, pause SceneTree, flush cloud writes on background; open pause menu on resume; redirect Quit to app restart.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NBackgroundModeHandler.EnterBackgroundMode` (type by name string) | postfix | Mutes FMOD master volume (via reflection on `NGame.AudioManager.SetMasterVol`), mutes Godot Master bus, sets `SceneTree.Paused = true`, flushes `SteamKit2CloudSaveStore`. |
| `NBackgroundModeHandler.ExitBackgroundMode` | prefix (replaces, returns false) | Opens the pause menu via a 6-level reflection chain (`NGame.CurrentRunNode.GlobalUi.SubmenuStack.ShowScreen(CapstoneSubmenuType.PauseMenu)` with hardcoded enum value 4), unpauses tree, restores audio volume from settings, resets `_isBackgrounded`/`_savedMaxFps` private fields via `AccessTools.Field`. |
| `NGame.Quit` (typeof) | prefix (replaces) | Flushes cloud store, then restarts the app via **Android JavaClassWrapper** → `com.game.sts2launcher.GodotApp.restartApp()`. Falls back to original on failure. |

**Required for iOS**: YES (app lifecycle). But `QuitPrefix` must be rewritten — its whole body is Android JNI.

**JIT tricks**: None beyond Harmony. Heavy `GetProperty`/`GetMethod`/`Invoke` reflection and `AccessTools.Field` — needs metadata preservation (rd.xml / TrimmerRootAssembly) under NativeAOT, or rewrite as direct calls once woven into sts2.dll (private access becomes free inside the assembly).

**Android APIs**: `Engine.GetSingleton("JavaClassWrapper")`, `com.game.sts2launcher.GodotApp.restartApp` (QuitPrefix). Also note: iOS has no equivalent of "restart the app" — on iOS the correct behavior is probably to suspend/return to menu, not restart.

---

## CardRewardPatches.cs

**Purpose**: Fix a crash on closing the rewards screen (tween race: `_Process` fires after QueueFree).

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NRewardsScreen.AfterOverlayClosed` (type by name string) | prefix (non-blocking, returns void) | `SetProcess(false)` on the node, kills the private `_fadeTween` (read via `AccessTools.Field`) and nulls it. |

**Required for iOS**: YES (crash fix, platform-independent bug that manifests on mobile).

**JIT tricks**: None beyond Harmony. One `AccessTools.Field` read/write — trivial to replace with direct field access when woven into the assembly.

**Android APIs**: None.

---

## CombatBackgroundPatches.cs

**Purpose**: Scale combat backgrounds and creature positions for non-16:9 aspect ratios (no black bars on tall screens).

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NCombatRoom.SetUpBackground` (type by name string) | postfix | Computes viewport ratio vs. 2764.8/1296 bg ratio, scales `%BgContainer` up on taller screens (only when AspectRatio=Auto). |
| `NCombatRoom._Ready` | postfix | Defers a reflection call to private `AdjustCreatureScaleForAspectRatio` (via `AccessTools.Method` + `Callable.CallDeferred`), and subscribes to `UiScalePatches.UiScaleChanged` to re-apply mid-combat. |

**Required for iOS**: YES (UI scaling — iPhone/iPad aspect ratios are exactly the tall-screen case).

**JIT tricks**: None beyond Harmony. `AccessTools.Method` + `MethodInfo.Invoke` on a private method; static state (event subscription with closure capture). Replaceable with direct call when woven.

**Android APIs**: None.

---

## EarlyAccessDisclaimerPatches.cs

**Purpose**: Fix early-access disclaimer layout on non-16:9 screens (fixed pixel offsets → proportional anchors).

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NEarlyAccessDisclaimer._Ready` (type by name string) | postfix | Rewrites `VBoxContainer` anchors/offsets to proportional values. Pure Godot node API. |

**Required for iOS**: YES (UI scaling).

**JIT tricks**: None at all — cleanest patch in the set.

**Android APIs**: None.

---

## EventLayoutPatches.cs

**Purpose**: Adjust event screen panel position and clamp option button widths when UI scale > 100%.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NEventLayout._Ready` (type by name string) | postfix | Applies layout shift, subscribes to `UiScalePatches.UiScaleChanged` for live re-apply. |
| `NEventLayout.AddOptions` | postfix | Clamps each option button's `CustomMinimumSize.X` to viewport width minus margins. |

**Required for iOS**: YES (UI scaling).

**JIT tricks**: None beyond Harmony. No reflection; depends on `UiScalePatches` static state/event.

**Android APIs**: None.

---

## LanMultiplayerPatcher.cs

**Purpose**: Replace Steam friends list with LAN multiplayer: UDP broadcast beacon (port 33770) for hosts, discovery + manual IP entry for clients.

**Patches** (applied with raw `harmony.Patch`, not PatchHelper; all 6 target types resolved by name string):
| Target | Type | Hook behavior |
|---|---|---|
| `NJoinFriendScreen._Ready` | postfix | Injects LAN UI (retitle, IP LineEdit + JOIN button) into the join screen. |
| `NJoinFriendScreen.OnSubmenuOpened` | prefix (replaces) | Skips Steam friend fetch entirely; clears buttons, starts `LanDiscovery` (UDP listen thread + Godot poll Timer). |
| `NJoinFriendScreen.OnSubmenuClosed` | postfix | Stops discovery. |
| `NetHostGameService.StartENetHost` | postfix | Starts the `LanBeacon` broadcast thread on successful host start. |
| `NetHostGameService.Disconnect` | postfix | Stops the beacon. |
| `NullPlatformUtilStrategy.GetPlayerName` | prefix (replaces) | Returns "Player1 (Host)"/"Player2"... instead of debug names, keyed on hardcoded player IDs. |

**Required for iOS**: OPTIONAL/DROPPABLE (LAN multiplayer). Note if kept: iOS requires the Local Network privacy entitlement + `NSLocalNetworkUsageDescription` for UDP broadcast, and background threads are suspended on backgrounding.

**JIT tricks**: **`ConstructorInfo.Invoke`** on `ENetClientConnectionInitializer(ulong, string, ushort)` and `MethodInfo.Invoke` on `JoinGameAsync`/`TaskHelper.RunSafely`/`NJoinFriendButton.Create` — all resolved by name string at Apply() time and cached in static fields. No Emit/DynamicMethod, but the heaviest cached-reflection surface in the set. Also raw sockets/threads (fine on iOS runtime-wise, entitlement-gated).

**Android APIs**: Only a cosmetic `"Android"` hostname fallback in `GetDeviceHostname()`.

---

## LauncherPatches.cs

**Purpose**: Intercept game startup to show the Steam-login launcher UI first, inject SteamKit2 cloud-save store into SaveManager, redirect cloud sync to `CloudSyncCoordinator`.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NGame.GameStartupWrapper` (typeof, **PatchCritical** — throws if missing) | prefix (replaces, rewrites `ref Task __result`) | Substitutes an async method: shows `LauncherUI`, awaits user launch, resets private static `SaveManager._instance` via reflection, runs shader warmup, then invokes private `GameStartup` via reflection. |
| `SaveManager.ConstructDefault` (typeof) | prefix (conditional replace, `ref SaveManager __result`) | If saved Steam credentials exist, constructs `SaveManager` wrapping a `CloudSaveStore(localStore, SteamKit2CloudSaveStore)`; otherwise falls through to original. |
| `CloudSaveStore.SyncCloudToLocal` (typeof, **PatchCritical**) | prefix (replaces, `ref Task __result`) | Redirects sync to `CloudSyncCoordinator.AutoSyncFileAsync`. |

**Required for iOS**: OPTIONAL as launcher UI / Steam-cloud feature — BUT `GameStartupWrapper` is the entry point of the whole mobile flow. If the iOS port keeps the sideloaded-Steam-download model, this is the spine; if the iOS build ships game files directly, all three patches are droppable (PlatformPatches alone stubs Steam). Decide the distribution model first.

**JIT tricks**: None beyond Harmony, but note: replacing an async `Task`-returning method via `ref Task __result` prefix, `GetField("_instance").SetValue` on a private static, `GetMethod("GameStartup").Invoke`. All straightforward under static weaving (direct access once inside the assembly).

**Android APIs**: None directly (its `LauncherUI`/`SteamKit2` dependencies are separate files).

---

## MerchantLayoutPatches.cs

**Purpose**: Shift the merchant shop open animation upward on short viewports (effective height < 1080) so the inventory stays visible.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NMerchantInventory.DoOpenAnimation` (type by name string) | prefix (conditional replace, `ref Task __result`) | If scaled height < 1080: rebuilds the open tween itself with an adjusted target Y, reads/writes private fields `_slotsContainer`, `_backstop`, `_inventoryTween` via `AccessTools.Field`, sets `__result = Task.CompletedTask`. Otherwise passes through. |

**Required for iOS**: YES (UI scaling).

**JIT tricks**: None beyond Harmony; `AccessTools.Field` x4 — direct field access once woven.

**Android APIs**: None.

---

## MobileLayoutPatches.cs

**Purpose**: Rework main menu for mobile: scale background to fill tall screens, move buttons left / logo right when UI scale > 100%.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NMainMenu._Ready` (type by name string) | postfix | Applies layout + subscribes to `UiScalePatches.UiScaleChanged` for live re-apply. Pure Godot node/anchor manipulation. |

**Required for iOS**: YES (UI scaling).

**JIT tricks**: None beyond Harmony; no reflection.

**Android APIs**: None.

---

## ModLoaderPatches.cs

**Purpose**: Extend `ModManager` to load sideloaded mods from Android external storage.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `ModManager.Initialize` (typeof) | postfix | Opens `/storage/emulated/0/StS2Launcher/Mods/`, temporarily flips private static `_initialized` to false via reflection, invokes private static `LoadModsInDirRecursive`, rebuilds private `_loadedMods` list. |

**Required for iOS**: OPTIONAL/DROPPABLE (mod loader). Additionally, .pck-content-only mods could work on iOS, but any mod carrying managed code cannot load under NativeAOT — mod loading on iOS is fundamentally limited.

**JIT tricks**: None beyond Harmony; private static field/method reflection (direct access once woven). The *feature itself* (loading external .pck/assemblies) is the JIT-adjacent problem.

**Android APIs**: `AppPaths.ExternalModsDir = /storage/emulated/0/StS2Launcher/Mods` (hard Android path); `AppPaths` also uses `JavaClassWrapper`/`GodotApp` for storage permissions.

---

## ModelDbInitPatch.cs

**Purpose**: Replace `ModelDb.Init()` with two-phase init to break circular-dependency crashes during model registration (Phase 1: pre-register uninitialized objects; Phase 2: run constructors).

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `ModelDb.Init` (typeof) | prefix (replaces, returns false) | Reads `AllAbstractModelSubtypes`, for each type calls **`RuntimeHelpers.GetUninitializedObject`** and inserts into private `_contentById` dict via reflection (`set_Item`); then runs each type's cctor (`RunClassConstructor`) and parameterless instance ctor via `ConstructorInfo.Invoke` on the pre-allocated object. |
| `ModelDb.Contains` | prefix — **applied and removed AT RUNTIME inside InitPrefix** via a second `new Harmony("com.sts2mobile.modeldb").Patch(...)` / `Unpatch(...)` | Returns false while `_suppressContains` flag is set, so constructors don't short-circuit during Phase 2. |

**Required for iOS**: YES, most likely — this is a boot-blocking crash fix for the mobile build. Verify whether the circular-dependency crash reproduces under NativeAOT at all (it may be Mono/JIT-init-order specific); if it does, this must be ported.

**JIT tricks — BIGGEST RED FLAG IN THE SET**:
1. **Runtime patch application/removal inside a running hook** (`Harmony.Patch`/`Unpatch` of `Contains` mid-execution). Impossible with static weaving as-is. Fix: statically weave the `Contains` prefix permanently and gate on the `_suppressContains` flag (the flag logic already exists — the dynamic patch/unpatch is redundant belt-and-suspenders).
2. `RuntimeHelpers.GetUninitializedObject(type)` on a runtime-enumerated open set of types — works under NativeAOT only if every model subtype's metadata + ctor is preserved from trimming (needs `DynamicDependency`/rd.xml roots for all `ModelDb` subtypes).
3. `ConstructorInfo.Invoke` on nonpublic parameterless ctors across that same open type set — same preservation requirement.

**Android APIs**: None.

---

## PlatformPatches.cs

**Purpose**: Stub out desktop/Steam platform features: Steam init, Sentry, system-info logging, telemetry, and an invalid `CreateDirectory(".")` call.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NGame.InitializePlatform` (typeof) | prefix (replaces) | Skips Steam init, returns `Task.FromResult(true)`. |
| `OsDebugInfo.LogSystemInfo` (typeof) | prefix (skip, `return false`) | No-ops system info logging. |
| `PrefsSave.UploadData` **getter** (via `PatchGetter`) | prefix (replaces) | Forces telemetry opt-in to `false`. |
| `GodotFileIo.CreateDirectory` (typeof) | prefix (conditional skip) | Skips paths without `"://"` (the `NullPlatformUtilStrategy` ctor's `CreateDirectory(".")` crash). |
| `SentryService.Initialize` (typeof) | prefix (skip) | Disables Sentry (GDExtension not bundled). |

**Required for iOS**: YES (platform/Steam stubbing — the core enabler; equally needed on iOS where the Sentry extension and Steam are absent).

**JIT tricks**: None at all. Purest stub file; note it patches a **property getter**, so the weaver must handle `get_UploadData`.

**Android APIs**: None in code (one comment references Android path semantics; the `"://"` check is equally valid on iOS Godot).

---

## SaveDiagnosticPatches.cs

**Purpose**: Inject diagnostic logging into `ProgressSaveManager.LoadProgress()` IL to trace why a fresh default save gets created instead of the pulled one.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `ProgressSaveManager.LoadProgress` (typeof) | **TRANSPILER** | Scans IL for `MigrationManager.LoadSave` call → inserts `Dup` + call to `LogLoadResult`; scans for `ProgressState.CreateDefault` call → inserts call to `LogCreatingDefault`. Uses `System.Reflection.Emit.OpCodes` for matching/insertion. |

**Required for iOS**: NO — OPTIONAL/DROPPABLE (save diagnostics, debug-only tooling).

**JIT tricks**: The only transpiler in the codebase. Transpilers are inherently runtime-IL-rewriting; under static weaving this becomes an offline IL rewrite (Cecil insert at the same call sites) — mechanically doable but this file isn't worth it. Drop it.

**Android APIs**: None.

---

## SettingsPatches.cs

**Purpose**: Apply mobile-friendly default settings on first launch (VSync=On, AspectRatio=Auto, MSAA=0) and fix the upstream swapped VSync Off/On label bug.

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `SaveManager.InitSettingsData` (typeof) | postfix | Once per install (marker file in user data dir), writes mobile default settings and saves. |
| `NVSyncPaginator.GetVSyncString` (type by name string) | prefix (replaces) | Maps VSync enum → correct localization key, constructs `LocString` via reflection (`GetConstructor` + `Invoke`) and calls `GetFormattedText` via reflection. |

**Required for iOS**: YES for the mobile defaults (AspectRatio=Auto is what all the layout patches key off). The VSync label fix is cosmetic but free to keep.

**JIT tricks**: None beyond Harmony; `ConstructorInfo.Invoke` on `LocString` — direct construction once woven.

**Android APIs**: None.

---

## TouchInputPatches.cs

**Purpose**: Cancel card play when a touch is released outside the play zone (prevents accidental plays from swipe drift).

**Patches**:
| Target | Type | Hook behavior |
|---|---|---|
| `NMouseCardPlay._Input` (type by name string) | postfix | On left-button release, calls private `IsCardInPlayZone` via reflection; if false, calls `CancelPlayCard` via reflection. |

**Required for iOS**: YES (touch input).

**JIT tricks**: None beyond Harmony; two `GetMethod(...).Invoke` calls per release event — direct calls once woven.

**Android APIs**: None.

---

## UiScalePatches.cs

**Purpose**: Replace the desktop resolution dropdown with a UI-scale selector (100–150%), persist to `user://ui_scale.cfg`, apply via `Window.ContentScaleSize`, and keep scale correct across window changes/rotation. Central hub: exposes `UiScalePercent`, `UiScaleChanged` event, and `EnsureUiScaleLoaded()` consumed by 4 other patch files.

**Patches** (all target types by name string):
| Target | Type | Hook behavior |
|---|---|---|
| `NResolutionDropdown.RefreshEnabled` | prefix (replaces) | Always enables the dropdown (no windowed/fullscreen concept on mobile). |
| `NResolutionDropdown.PopulateDropdownItems` | prefix (replaces) | Clears items, instantiates dropdown item scenes for 100–150% (encoded as `Vector2I(scale, 0)`), wires `Selected` signal. Uses `AccessTools.Field` for `_dropdownItems`/`_dropdownItemScene`. |
| `NResolutionDropdown.RefreshCurrentlySelectedResolution` | prefix (replaces) | Shows "N%" in the label (private `_currentOptionLabel` via `AccessTools.Field`). |
| `NResolutionDropdown.OnDropdownItemSelected` | prefix (conditional replace) | Reads item's `resolution` field; if Y==0 it's a scale item → saves, applies `ContentScaleSize = (1680/scale, 1080/scale)` with Expand aspect, fires `UiScaleChanged`. |
| `NResolutionDropdownItem.Init` | prefix (conditional replace) | Renders scale items as "N%" text. |
| `NSettingsScreen.LocalizeLabels` | postfix | Renames "Resolution" label to "UI Scale". |
| `NGlobalUi.OnWindowChange` | prefix (conditional replace) | When AspectRatio=Auto, reapplies scaled ContentScaleSize instead of original handler (rotation/resize). |
| `NMainMenu.OnWindowChange` | prefix (conditional replace) | Same, for the main menu path. |

**Required for iOS**: YES (UI scaling core — everything else depends on it).

**JIT tricks**: None beyond Harmony; pervasive `AccessTools.Field`/`AccessTools.Method` + `GodotObject.Call` string dispatch. Static mutable state + event. All direct-access-able once woven.

**Android APIs**: None (rotation handling is generic Godot).

---

## Non-patch infrastructure notes

- **ModEntry.cs**: `[UnmanagedCallersOnly]` entry points called from a patched `gd_mono.cpp`
  (`InitializeGodotSharp` bootstraps GodotSharp interop manually; `Apply()` runs all patches).
  Under NativeAOT the GodotSharp bootstrap changes shape entirely, and `Apply()`'s
  runtime-Harmony pass disappears — patch application order still matters for the woven
  equivalent (ModelDb → Platform → Settings → UiScale → layout patches → lifecycle → input).
- **AppPaths.cs**: entirely Android (`/storage/emulated/0`, `JavaClassWrapper`,
  `GodotApp.hasStoragePermission/requestStoragePermission`). iOS equivalent: app container
  Documents dir + `UIFileSharingEnabled`, or drop with the mod loader.
- **AppLifecycle trigger source**: `NBackgroundModeHandler.EnterBackgroundMode/ExitBackgroundMode`
  is the game's own node — verify what drives it on iOS (Godot `NOTIFICATION_APPLICATION_PAUSED`
  should map, but confirm the handler node exists/fires in the iOS export).

---

## Summary table

| Patch file | Required for iOS? | Static-weave difficulty | Notes |
|---|---|---|---|
| AppLifecyclePatches.cs | REQUIRED (lifecycle) | medium | QuitPrefix is 100% Android JNI — rewrite for iOS |
| CardRewardPatches.cs | REQUIRED (crash fix) | medium | one private field access |
| CombatBackgroundPatches.cs | REQUIRED (UI scaling) | medium | private method invoke + event state |
| EarlyAccessDisclaimerPatches.cs | REQUIRED (UI scaling) | easy | zero reflection |
| EventLayoutPatches.cs | REQUIRED (UI scaling) | medium | no reflection, shared static state |
| LanMultiplayerPatcher.cs | OPTIONAL (LAN multiplayer) | medium | heavy cached reflection, sockets/threads, iOS Local Network entitlement if kept |
| LauncherPatches.cs | DEPENDS on distribution model (launcher/Steam cloud) | medium | GameStartupWrapper is the flow entry point; PatchCritical x2 |
| MerchantLayoutPatches.cs | REQUIRED (UI scaling) | medium | private fields, Task-result replacement |
| MobileLayoutPatches.cs | REQUIRED (UI scaling) | easy | zero reflection |
| ModLoaderPatches.cs | OPTIONAL (mod loader) | medium | Android storage path; managed-code mods impossible under NativeAOT anyway |
| ModelDbInitPatch.cs | REQUIRED (boot crash fix — verify repro under NativeAOT) | **hard** | runtime Harmony.Patch/Unpatch inside a hook; GetUninitializedObject + ctor.Invoke over open type set |
| PlatformPatches.cs | REQUIRED (platform/Steam stubbing) | easy | includes one property-getter patch |
| SaveDiagnosticPatches.cs | OPTIONAL (diagnostics) | **hard** (transpiler) | drop it |
| SettingsPatches.cs | REQUIRED (mobile defaults) | medium | LocString reflection ctor |
| TouchInputPatches.cs | REQUIRED (touch input) | medium | two private method invokes |
| UiScalePatches.cs | REQUIRED (UI scaling core) | medium | hub for 4 other files; 8 patches, pervasive AccessTools |

Difficulty rubric: easy = pure prefix/postfix, no reflection; medium = private state/reflection
(becomes direct access once woven into sts2.dll); hard = transpiler or runtime codegen/dynamic
patching that has no direct static-weave equivalent.
