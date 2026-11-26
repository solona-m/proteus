# IPC contract: Penumbra and Glamourer call gates used by Proteus

Written by Agent C (Phase B, step B1, plan D24 and D25). This is a facts-only contract taken from `Penumbra.Api/`, `Glamourer/Glamourer.Api/`, the Luna IPC subscriber bases those APIs derive from (facts only), and Proteus's own sources at `209e5bbc`. It holds no API code and no doc comments.

Conventions used in every table:
- **Label** is the exact string passed to `IDalamudPluginInterface.GetIpcSubscriber`. It is the API type's `const string Label`, which is also what the owning plugin's provider registers under.
- **Arguments** and **Returns** together are the call gate's generic arguments, in order: `GetIpcSubscriber<Arguments…, Returns>`. They are the types at the gate, not the API wrapper's friendlier parameters. Where the wrapper reorders or fills in parameters, the gate order is given and the wrapper's default is noted.
- **Action** gates and **Event** gates have `object?` (that is, `System.Object`) as their last generic argument. The Returns column shows it.
- Tuple element names are shown where the API gives them. They are not part of the runtime type (`System.ValueTuple`). `T?` on a value type is `System.Nullable<T>` and is part of the type. `?` on a reference type is a nullability annotation only.
- `nint` is `System.IntPtr`. `JObject` is `Newtonsoft.Json.Linq.JObject`. `Guid` is `System.Guid`. Collection types are from `System.Collections.Generic`.
- Rows are numbered P1… (Penumbra) and G1… (Glamourer) so that tests and reviews can cite them.

## 1. Calls Proteus makes

Func and Action gates used by Proteus's shipped code outside `Proteus/TestHarness/`. Events are in §2. All of them are used only in `Proteus/Interop/PenumbraBridge.cs` and `Proteus/Interop/GlamourerBridge.cs`. Each is created once in the bridge constructor, in a private readonly field.

| Owner | Label | Kind | Arguments | Returns | Used in |
|---|---|---|---|---|---|
| P1 Penumbra | `Penumbra.ApiVersion.V5` | Func | (none) | `(int Breaking, int Features)` | PenumbraBridge: field `apiVersion`; `CheckAvailability` (called from the constructor and `OnPenumbraInitialized`) |
| P2 Penumbra | `Penumbra.GetModList` | Func | (none) | `Dictionary<string, string>` (mod directory to mod name) | PenumbraBridge: `getModList`; `GetAllMods`, `GetAllModsQuiet` |
| P3 Penumbra | `Penumbra.GetModDirectory` | Func | (none) | `string` | PenumbraBridge: `getModDirectory`; `GetModDirectory` |
| P4 Penumbra | `Penumbra.GetCollectionForObject.V5` | Func | `int` gameObjectIdx (Proteus passes 0) | `(bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)` | PenumbraBridge: `getCollectionForObject`; `GetPlayerCollection` (and through it `GetPlayerCollectionId`, `OnGameObjectRedrawn`, `SetPlayerTemporaryMod`, `RemovePlayerTemporaryMod`) |
| P5 Penumbra | `Penumbra.GetCollections.V5` | Func | (none) | `Dictionary<Guid, string>` | PenumbraBridge: `getCollections`; `GetCollections` |
| P6 Penumbra | `Penumbra.GetCurrentModSettingsWithTemp` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (wrapper default ""; Proteus uses it), `bool` ignoreInheritance (default false; Proteus uses it), `bool` ignoreTemporary (default false; Proteus uses it), `int` key (default 0; Proteus uses it) | `(int, (bool, int, Dictionary<string, List<string>>, bool, bool)?)`: the int is a PenumbraApiEc; the inner tuple is enabled, priority, options (group to selected option names), inherited, temporary | PenumbraBridge: `getCurrentModSettings`; `GetModSettings`, `GetModSettingsSnapshot`, `ReadModSettings` |
| P7 Penumbra | `Penumbra.GetAllModSettings` | Func | `Guid` collectionId, `bool` ignoreInheritance (Proteus passes false), `bool` ignoreTemporary, `int` key | `(int, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?)`: the int is a PenumbraApiEc; the dictionary maps mod directory to the same five-element tuple as P6 | PenumbraBridge: `getAllModSettings`; `GetCollectionModSettings` |
| P8 Penumbra | `Penumbra.RemoveTemporaryModSettings.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `int` key (default 0; Proteus uses it). The wrapper takes key before modName; at the gate modName comes before key. | `int` (PenumbraApiEc) | PenumbraBridge: `removeTemporaryModSettings`; `ClearTemporaryModSettings` |
| P9 Penumbra | `Penumbra.SetTemporaryModSettings.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `(bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>)` (inherit, enabled, priority, settings; Proteus passes inherit false), `string` source, `int` key | `int` (PenumbraApiEc) | PenumbraBridge: `setTemporaryModSettings`; `SetTemporaryModSettings` |
| P10 Penumbra | `Penumbra.RemoveAllTemporaryModSettings.V5` | Func | `Guid` collectionId, `int` key | `int` (PenumbraApiEc) | PenumbraBridge: `removeAllTemporaryModSettings`; `RemoveAllTemporaryModSettings` |
| P11 Penumbra | `Penumbra.QueryTemporaryModSettings.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `int` key (default 0; Proteus uses it) | `(int, (bool, bool, int, Dictionary<string, List<string>>)?, string)`: PenumbraApiEc; the settings (the wrapper names them ForceInherit, Enabled, Priority, Settings), or null; the source | PenumbraBridge: `queryTemporaryModSettings`; `QueryTemporaryModSettings` |
| P12 Penumbra | `Penumbra.ResolvePlayerPath` | Func | `string` gamePath | `string` | PenumbraBridge: `resolvePlayerPath`; `ResolvePlayer` |
| P13 Penumbra | `Penumbra.ResolvePlayerPaths` | Func | `string[]` forward, `string[]` reverse (Proteus passes an empty array) | `(string[], string[][])` (forward resolved, reverse resolved) | PenumbraBridge: `resolvePlayerPaths`; `ResolvePlayerPaths` |
| P14 Penumbra | `Penumbra.AddMod.V5` | Func | `string` modDirectory | `int` (PenumbraApiEc) | PenumbraBridge: `addMod`; `AddModDirectory` |
| P15 Penumbra | `Penumbra.ReloadMod.V5` | Func | `string` modDirectory, `string` modName (default ""; Proteus uses it) | `int` (PenumbraApiEc) | PenumbraBridge: `reloadMod`; `ReloadModDirectory` |
| P16 Penumbra | `Penumbra.DeleteMod.V5` | Func | `string` modDirectory, `string` modName (default ""; Proteus uses it) | `int` (PenumbraApiEc) | PenumbraBridge: `deleteMod`; `DeleteModDirectory` |
| P17 Penumbra | `Penumbra.SetModPath.V5` | Func | `string` modDirectory, `string` modName (default ""), `string` newPath. The wrapper takes (modDirectory, newPath, modName); at the gate modName is second and newPath third. | `int` (PenumbraApiEc) | PenumbraBridge: `setModPath`; `SetModListPath` |
| P18 Penumbra | `Penumbra.TrySetMod.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `bool` (the enabled state Proteus passes; the wrapper's parameter is named `inherit`) | `int` (PenumbraApiEc) | PenumbraBridge: `trySetMod`; `SetModEnabled` |
| P19 Penumbra | `Penumbra.TrySetModPriority.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `int` priority | `int` (PenumbraApiEc) | PenumbraBridge: `trySetModPriority`; `SetModPriority` |
| P20 Penumbra | `Penumbra.TryInheritMod.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `bool` inherit (Proteus passes true) | `int` (PenumbraApiEc) | PenumbraBridge: `tryInheritMod`; `SetModInherited` |
| P21 Penumbra | `Penumbra.TrySetModSetting.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `string` optionGroupName, `string` optionName | `int` (PenumbraApiEc) | PenumbraBridge: `trySetModSetting`; `SetModOption` (exactly one option) |
| P22 Penumbra | `Penumbra.TrySetModSettings.V5` | Func | `Guid` collectionId, `string` modDirectory, `string` modName (default ""), `string` optionGroupName, `IReadOnlyList<string>` optionNames | `int` (PenumbraApiEc) | PenumbraBridge: `trySetModSettings`; `SetModOption` (zero or several options) |
| P23 Penumbra | `Penumbra.RedrawObject.V5` | Action | `int` gameObjectIndex (Proteus passes 0), `int` (RedrawType as int; Proteus passes Redraw = 0) | `object?` | PenumbraBridge: `redrawObject`; `RedrawPlayer` |
| P24 Penumbra | `Penumbra.OpenMainWindow.V5` | Func | `int` (TabType as int; Proteus passes Mods = 1), `string` modDirectory, `string` modName (default ""; Proteus uses it) | `int` (PenumbraApiEc; Proteus ignores it) | PenumbraBridge: `openMainWindow`; `OpenToMod` |
| P25 Penumbra | `Penumbra.GetGameObjectResourcePaths.V5` | Func | `ushort[]` gameObjectIndices (Proteus passes one element, 0) | `Dictionary<string, HashSet<string>>?[]` (one entry per index; resolved file to game paths; an entry may be null) | PenumbraBridge: `getGameObjectResourcePaths`; `GetActivePlayerResourceMap`, `GetActivePlayerMaterialPaths`, `GetActivePlayerModelPaths`, `GetActivePlayerModelFiles`, `GetActivePlayerModelGamePaths`, `GetActivePlayerResourcePaths` |
| P26 Penumbra | `Penumbra.AddTemporaryMod.V5` | Func | `string` tag, `Guid` collectionId, `Dictionary<string, string>` paths (game path to file), `string` manipString (Proteus passes ""), `int` priority | `int` (PenumbraApiEc) | PenumbraBridge: `addTemporaryMod`; `SetTemporaryMod` (and `SetPlayerTemporaryMod` through it) |
| P27 Penumbra | `Penumbra.RemoveTemporaryMod.V5` | Func | `string` tag, `Guid` collectionId, `int` priority | `int` (PenumbraApiEc) | PenumbraBridge: `removeTemporaryMod`; `RemoveTemporaryMod` (and `RemovePlayerTemporaryMod` through it) |
| P28 Penumbra | `Penumbra.GetPlayerMetaManipulations` | Func | (none) | `string` | PenumbraBridge: `getPlayerMetaManipulations`; `GetPlayerMetaManipulations` |
| G1 Glamourer | `Glamourer.GetDesignList.V2` | Func | (none) | `Dictionary<Guid, string>` (design id to display name) | GlamourerBridge: `getDesignList`; `GetDesigns` |
| G2 Glamourer | `Glamourer.GetDesignJObject` | Func | `Guid` designId | `JObject?` | GlamourerBridge: `getDesignJObject`; `GetDesign` |
| G3 Glamourer | `Glamourer.GetState` | Func | `int` objectIndex, `uint` key (default 0; Proteus uses it) | `(int, JObject?)` (GlamourerApiEc as int, state) | GlamourerBridge: `getState`; `GetObjectState` |
| G4 Glamourer | `Glamourer.ReapplyState` | Func | `int` objectIndex (Proteus passes 0), `uint` key (Proteus passes 0), `ulong` (ApplyFlag as ulong; Proteus passes Equipment = 0x02) | `int` (GlamourerApiEc) | GlamourerBridge: `reapplyState`; `ReapplyPlayerState` |
| G5 Glamourer | `Glamourer.SetBonusItem` | Func | `int` objectIndex (0), `byte` (ApiBonusSlot as byte; Proteus passes Glasses = 1), `ulong` itemId, `uint` key (0), `ulong` (ApplyFlag as ulong; Proteus passes Once = 0x01) | `int` (GlamourerApiEc) | GlamourerBridge: `setBonusItem`; `SetGlasses` |
| G6 Glamourer | `Glamourer.SetItem.V3` | Func | `int` objectIndex (0), `byte` (ApiEquipSlot as byte), `ulong` itemId, `IReadOnlyList<byte>` stain (Proteus always passes a two-element `List<byte>`), `uint` key (0), `ulong` (ApplyFlag as ulong; Proteus passes Once = 0x01) | `int` (GlamourerApiEc) | GlamourerBridge: `setItem`; `UnequipSlot`, `SetGearItem`, `SetAccessory` (and `SetRing` through it) |

Rules:
- R1.1 Every PenumbraApiEc and GlamourerApiEc crosses the gate as `int`, every ApplyFlag as `ulong`, every ApiEquipSlot and ApiBonusSlot as `byte`, and RedrawType and TabType as `int`. The enums are applied by the API wrapper by casting. No enum crosses a §1 gate as an enum.
- R1.2 Wherever a "default" is noted, the gate has no default: the caller passes that value explicitly.
- R1.3 Proteus at HEAD uses no other Func or Action gate in shipped code. A grep of `Proteus/` for `IpcSubscribers` finds only the two bridges and `TestHarness/GameHarnessAdapter.cs` (§6).

## 2. Events Proteus subscribes to

Each is subscribed once in the bridge constructor, with one handler, and disposed in the bridge's `Dispose`.

| Owner | Label | Kind | Arguments | Returns | Used in |
|---|---|---|---|---|---|
| P29 Penumbra | `Penumbra.ModSettingChanged.V5` | Event | `Penumbra.Api.Enums.ModSettingChange` change, `Guid` collectionId, `string` modDirectory, `bool` inherited | `object?` | PenumbraBridge: field `modSettingChangedSub`; constructor lambda (filters Proteus's own TemporaryMod echo within 1000 ms), raises `ModSettingChanged` |
| P30 Penumbra | `Penumbra.ModAdded` | Event | `string` modDirectory | `object?` | PenumbraBridge: `modAddedSub`; raises `ModAdded` |
| P31 Penumbra | `Penumbra.ModDeleted` | Event | `string` modDirectory | `object?` | PenumbraBridge: `modDeletedSub`; raises `ModDeleted` |
| P32 Penumbra | `Penumbra.ModMoved` | Event | `string` oldDirectory, `string` newDirectory | `object?` | PenumbraBridge: `modMovedSub`; `OnModMoved` (posts `ModMoved` one framework tick later) |
| P33 Penumbra | `Penumbra.GameObjectRedrawn` | Event | `nint` gameObjectAddress, `int` objectTableIndex | `object?` | PenumbraBridge: `gameObjectRedrawnSub`; `OnGameObjectRedrawn` (raises `LocalPlayerRedrawn` and `PlayerCollectionChanged`) |
| P34 Penumbra | `Penumbra.Initialized` | Event | (none) | `object?` | PenumbraBridge: `initializedSub`; `OnPenumbraInitialized` (re-runs P1, raises `PenumbraReady`) |
| P35 Penumbra | `Penumbra.Disposed` | Event | (none) | `object?` | PenumbraBridge: `disposedSub`; `OnPenumbraDisposed` (sets `IsAvailable` false) |
| G7 Glamourer | `Penumbra.StateChangedWithType` | Event | `nint` actorAddress, `Glamourer.Api.Enums.StateChangeType` type | `object?` | GlamourerBridge: `stateChangedSub`; `OnStateChanged` |
| G8 Glamourer | `Penumbra.StateFinalized` | Event | `nint` actorAddress, `Glamourer.Api.Enums.StateFinalizationType` type | `object?` | GlamourerBridge: `stateFinalizedSub`; `OnStateFinalized` |

Rules:
- R2.1 G7 and G8 are Glamourer's events, but their labels begin with `Penumbra.`. That is the API's `const Label`, which both the subscriber and Glamourer's own provider use. The API's UTF-8 label property for these two says `Glamourer.…`, and is not what the gate uses. Use the `Penumbra.`-prefixed string.
- R2.2 Unlike §1, three event arguments cross as the enum type itself: ModSettingChange (P29), StateChangeType (G7) and StateFinalizationType (G8). The provider sends values of its own copy of these types (see A6).
- R2.3 No Glamourer `Initialized` or `Disposed` event, and no Glamourer ApiVersion gate, is used by Proteus.

## 3. Types that cross the boundary

Enums: every member is listed, including the ones Proteus does not use. Enums with no explicit underlying type are `int`.

| Type | Member | Value | Underlying | Flags |
|---|---|---|---|---|
| Penumbra.Api.Enums.ModSettingChange | Inheritance | 0 | int | no |
| Penumbra.Api.Enums.ModSettingChange | EnableState | 1 | int | no |
| Penumbra.Api.Enums.ModSettingChange | Priority | 2 | int | no |
| Penumbra.Api.Enums.ModSettingChange | Setting | 3 | int | no |
| Penumbra.Api.Enums.ModSettingChange | MultiInheritance | 4 | int | no |
| Penumbra.Api.Enums.ModSettingChange | MultiEnableState | 5 | int | no |
| Penumbra.Api.Enums.ModSettingChange | TemporaryMod | 6 | int | no |
| Penumbra.Api.Enums.ModSettingChange | Edited | 7 | int | no |
| Penumbra.Api.Enums.ModSettingChange | TemporarySetting | 8 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | Success | 0 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | NothingChanged | 1 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | CollectionMissing | 2 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | ModMissing | 3 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | OptionGroupMissing | 4 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | OptionMissing | 5 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | CharacterCollectionExists | 6 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | LowerPriority | 7 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | InvalidGamePath | 8 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | FileMissing | 9 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | InvalidManipulation | 10 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | InvalidArgument | 11 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | PathRenameFailed | 12 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | CollectionExists | 13 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | AssignmentCreationDisallowed | 14 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | AssignmentDeletionDisallowed | 15 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | InvalidIdentifier | 16 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | SystemDisposed | 17 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | AssignmentDeletionFailed | 18 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | TemporarySettingDisallowed | 19 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | TemporarySettingImpossible | 20 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | InvalidCredentials | 21 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | CollectionInactive | 22 | int | no |
| Penumbra.Api.Enums.PenumbraApiEc | UnknownError | 255 | int | no |
| Penumbra.Api.Enums.RedrawType | Redraw | 0 | int | no |
| Penumbra.Api.Enums.RedrawType | AfterGPose | 1 | int | no |
| Penumbra.Api.Enums.TabType | None | -1 | int | no |
| Penumbra.Api.Enums.TabType | Settings | 0 | int | no |
| Penumbra.Api.Enums.TabType | Mods | 1 | int | no |
| Penumbra.Api.Enums.TabType | Collections | 2 | int | no |
| Penumbra.Api.Enums.TabType | ChangedItems | 3 | int | no |
| Penumbra.Api.Enums.TabType | EffectiveChanges | 4 | int | no |
| Penumbra.Api.Enums.TabType | ResourceWatcher | 5 | int | no |
| Penumbra.Api.Enums.TabType | Debug | 6 | int | no |
| Penumbra.Api.Enums.TabType | ResourceManager | 7 | int | no |
| Penumbra.Api.Enums.TabType | OnScreen | 8 | int | no |
| Penumbra.Api.Enums.TabType | Messages | 9 | int | no |
| Penumbra.Api.Enums.TabType | Management | 10 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | Success | 0 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | NothingDone | 1 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | ActorNotFound | 2 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | ActorNotHuman | 3 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | DesignNotFound | 4 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | ItemInvalid | 5 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | InvalidKey | 6 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | InvalidState | 7 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | CouldNotParse | 8 | int | no |
| Glamourer.Api.Enums.GlamourerApiEc | UnknownError | 2147483647 (int.MaxValue) | int | no |
| Glamourer.Api.Enums.ApplyFlag | Once | 0x01 (1) | ulong | yes |
| Glamourer.Api.Enums.ApplyFlag | Equipment | 0x02 (2) | ulong | yes |
| Glamourer.Api.Enums.ApplyFlag | Customization | 0x04 (4) | ulong | yes |
| Glamourer.Api.Enums.ApplyFlag | Lock | 0x08 (8) | ulong | yes |
| Glamourer.Api.Enums.ApiEquipSlot | Unknown | 0 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | MainHand | 1 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | OffHand | 2 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Head | 3 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Body | 4 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Hands | 5 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Legs | 7 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Feet | 8 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Ears | 9 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Neck | 10 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | Wrists | 11 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | RFinger | 12 | byte | no |
| Glamourer.Api.Enums.ApiEquipSlot | LFinger | 14 | byte | no |
| Glamourer.Api.Enums.ApiBonusSlot | Unknown | 0 | byte | no |
| Glamourer.Api.Enums.ApiBonusSlot | Glasses | 1 | byte | no |
| Glamourer.Api.Enums.StateChangeType | Model | 0 | int | no |
| Glamourer.Api.Enums.StateChangeType | EntireCustomize | 1 | int | no |
| Glamourer.Api.Enums.StateChangeType | Customize | 2 | int | no |
| Glamourer.Api.Enums.StateChangeType | Equip | 3 | int | no |
| Glamourer.Api.Enums.StateChangeType | Weapon | 4 | int | no |
| Glamourer.Api.Enums.StateChangeType | Stains | 5 | int | no |
| Glamourer.Api.Enums.StateChangeType | Crest | 6 | int | no |
| Glamourer.Api.Enums.StateChangeType | Parameter | 7 | int | no |
| Glamourer.Api.Enums.StateChangeType | MaterialValue | 8 | int | no |
| Glamourer.Api.Enums.StateChangeType | Design | 9 | int | no |
| Glamourer.Api.Enums.StateChangeType | Reset | 10 | int | no |
| Glamourer.Api.Enums.StateChangeType | Other | 11 | int | no |
| Glamourer.Api.Enums.StateChangeType | Reapply | 12 | int | no |
| Glamourer.Api.Enums.StateChangeType | BonusItem | 13 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | ModelChange | 0 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | DesignApplied | 1 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | Revert | 2 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | RevertCustomize | 3 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | RevertEquipment | 4 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | RevertAdvanced | 5 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | RevertAutomation | 6 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | Reapply | 7 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | ReapplyAutomation | 8 | int | no |
| Glamourer.Api.Enums.StateFinalizationType | Gearset | 9 | int | no |

Non-enum data. The API types are first, then the tuples, in the order of their elements.

| Type | Member | Value | Underlying | Flags |
|---|---|---|---|---|
| Glamourer.Api.Enums.ApplyFlagEx (static class of constants; used by the harness, §6) | DesignDefault | Once, Equipment, Customization = 7 | const ApplyFlag (ulong) | n/a |
| Glamourer.Api.Enums.ApplyFlagEx | StateDefault | Equipment, Customization, Lock = 14 | const ApplyFlag (ulong) | n/a |
| Glamourer.Api.Enums.ApplyFlagEx | RevertDefault | Equipment, Customization = 6 | const ApplyFlag (ulong) | n/a |
| P1 return tuple | Item1 Breaking, Item2 Features | (int, int) | System.ValueTuple`2 | n/a |
| P4 return tuple | Item1 ObjectValid `bool`, Item2 IndividualSet `bool`, Item3 EffectiveCollection `(Guid Id, string Name)` | nested ValueTuple`2 in Item3 | System.ValueTuple`3 | n/a |
| P6 and P7 settings tuple | Item1 enabled `bool`, Item2 priority `int`, Item3 options `Dictionary<string, List<string>>`, Item4 inherited `bool`, Item5 temporary `bool` | P6 wraps it as `Nullable<>` in Item2 of `(int, …?)`; P7 is the dictionary value | System.ValueTuple`5 | n/a |
| P9 settings argument | Item1 inherit `bool`, Item2 enabled `bool`, Item3 priority `int`, Item4 settings `IReadOnlyDictionary<string, IReadOnlyList<string>>` | not nullable | System.ValueTuple`4 | n/a |
| P11 return tuple | Item1 `int` (PenumbraApiEc), Item2 `(bool ForceInherit, bool Enabled, int Priority, Dictionary<string, List<string>> Settings)?`, Item3 `string` source | Item2 is `Nullable<ValueTuple`4>` | System.ValueTuple`3 | n/a |
| P13 return tuple | Item1 `string[]`, Item2 `string[][]` | n/a | System.ValueTuple`2 | n/a |
| G3 return tuple | Item1 `int` (GlamourerApiEc), Item2 `JObject?` | n/a | System.ValueTuple`2 | n/a |
| Newtonsoft.Json.Linq.JObject (G2, G3, §6 ApplyState) | Newtonsoft's own type, not owned by either API | n/a | reference type | n/a |

Rules:
- R3.1 The owned copies (plan D24) must keep each enum's full name, member names, values and underlying type exactly as above. `[Flags]` is on ApplyFlag only.
- R3.2 The Proteus code outside the bridges uses, from Penumbra.Api.Enums, only ModSettingChange, PenumbraApiEc, RedrawType and TabType. From Glamourer.Api.Enums it uses only the Glamourer types above, plus ApplyFlagEx in the harness. A grep for every other API enum name (ApiCollectionType, ChangedItemIcon, ChangedItemType, GroupType, MouseButton, PresetApplyMode, PresetQueryMode, ResourceType, TextureType, MainTabType, SetMetaFlag) found none in Proteus `.cs` files.

## 4. Threading, availability and errors

Sources are tagged as follows. [API] is the Penumbra.Api or Glamourer.Api source. [Luna] is the Luna subscriber bases named in the log, read for facts only. [Proteus] is Proteus at HEAD. [Dalamud] is Dalamud's call-gate behaviour, which is not in the allowed sources: it is stated from general knowledge of Dalamud's IPC and corroborated where Proteus's own source records it, so the critic should confirm it against B2's code and tests.

| Row | Fact |
|---|---|
| A1 | [Luna] Func and Action wrappers call `GetIpcSubscriber<…>(label)` once, in their constructor, and keep the returned gate object. If that call throws, the exception is caught and logged at Error level, the gate stays null, and the constructor does not throw. |
| A2 | [Luna] The event wrapper also calls `GetIpcSubscriber<…, object?>(label)` once in its constructor, then subscribes the handlers passed to it. If the call throws, the exception is caught and logged at Error level, nothing is subscribed, and the constructor does not throw. The caller cannot see that it failed. |
| A3 | [Dalamud; Proteus GlamourerBridge.cs:79-80] `GetIpcSubscriber` does not need the owning plugin to be loaded: it returns the label's gate whether or not a provider has registered. Creating a gate is safe while Penumbra or Glamourer is absent. |
| A4 | [Luna] Invoking a Func wrapper whose gate is null throws `Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError` with the label. [Dalamud] Invoking a non-null Func gate when no provider has registered the function (the plugin is not loaded, not yet initialized, or disposed) also throws `IpcNotReadyError`. |
| A5 | [Luna] Invoking an Action wrapper whose gate is null does nothing and does not throw. [Dalamud] Invoking a non-null Action gate with no provider registered throws `IpcNotReadyError`. The Luna base's own remark says an unavailable action "does not throw", but its code guards only the null gate. Proteus's only Action use (P23, `RedrawPlayer`) catches every exception. |
| A6 | [Dalamud; Proteus GlamourerBridge.cs:247-248] When the subscriber's generic types are not the provider's own types, Dalamud converts each argument and return value with a Newtonsoft JSON round trip. If the shapes cannot convert, it throws `IpcTypeMismatchError`. If the argument count differs, it throws `IpcLengthMismatchError`. Proteus records the round trip: a `byte[]` would arrive as base64, and an empty list arrives as null, which crashes Glamourer. That is why G6 always sends a two-element `List<byte>`. Enums serialize as their numbers, so an owned copy of an enum with the same values converts. Tuples serialize by element position. |
| A7 | [Dalamud; Proteus GlamourerBridge.cs:126, :353] An exception thrown inside the provider's function reaches the caller as a wrapping exception with a constant message, with the real cause as its InnerException (Proteus calls it a TargetInvocationException). [API] The API wrappers add no try/catch around any call. |
| A8 | [API] Func wrappers turn the returned int into PenumbraApiEc or GlamourerApiEc by a plain cast, with no range check. An unknown code passes through as an undefined enum value. No wrapper throws because of a code. |
| A9 | [API] No subscriber Proteus uses checks a version. Versioning is in the label suffix (`.V2`, `.V3`, `.V5`). A provider on another version has not registered that label, so the call behaves as in A4. [Proteus] P1's (Breaking, Features) result is only logged. Proteus enforces no minimum, and any successful P1 call counts as "available". Proteus makes no Glamourer ApiVersion call. The API's version-checking plugin-subscriber base, which Penumbra.Api has, is not used by Proteus. |
| A10 | [Proteus] Penumbra availability: `IsAvailable` is set by a P1 call in the constructor and again on P34 (Initialized). The bridge raises `PenumbraReady` after P34 only if that P1 call succeeds. P35 (Disposed) sets it false. Every PenumbraBridge method returns early while it is false: null for reads, false for the temporary-mod and redraw calls, `PenumbraApiEc.SystemDisposed` for writes, nothing for `OpenToMod`. Each method also catches every exception, logs it, and returns null, false or `PenumbraApiEc.UnknownError`. |
| A11 | [Proteus] Glamourer availability: `IsAvailable` becomes true once the constructor has built the G7 and G8 subscriptions without an exception. By A2 that never throws, so at HEAD it is true whether or not Glamourer is loaded. `GetDesigns`, `GetDesign` and `GetObjectState` do not check `IsAvailable`; they rely on catching exceptions. `GetDesigns` returns an empty dictionary on failure. `GetDesign` falls back to reading the design's JSON file. `GetObjectState` returns null unless the code is Success. The writers (G4, G5, G6) check `IsAvailable` and, except G4, a non-null local player. They return false or null on a failure or a code other than Success. |
| A12 | [Dalamud; AGENTS.md Sensitive Areas; Proteus PenumbraBridge.cs:688-697] Event handlers run synchronously, inline, on the thread on which the provider raises the event. Penumbra raises its events on the thread of whoever made the change, so they can arrive on Proteus's own background thread when Proteus made the change. [Proteus GlamourerBridge.cs:67-70, :411-416] Glamourer raises G7 synchronously inside its operation, on the calling thread, before G8. [Luna] The event wrapper does no marshalling. Plan D24: event threading does not change. |
| A13 | [Luna] The event wrapper wraps each handler in its own try/catch and logs any exception at Error level with the label. A handler's exception never reaches the provider or the other handlers. Registering the same delegate twice has no effect. |
| A14 | [Dalamud] A subscription can be made before the provider exists. It starts receiving once the provider raises the event. Gates are keyed by label inside Dalamud, so subscriptions survive the owning plugin being unloaded and reloaded. [Luna/API] No wrapper re-subscribes on reload or listens for the owner's Initialized/Disposed events itself. Proteus handles reload only through P34 and P35 (A10). |
| A15 | [Luna] Disposing the event wrapper unsubscribes every handler it added and forgets them. A second Dispose does nothing. Adding or removing a handler after disposal throws ObjectDisposedException. The Func and Action wrappers hold no subscription and are not IDisposable. [Proteus] The bridges dispose all nine §2 subscriptions. PenumbraBridge also cancels any queued `ModMoved` raises (P32) in `Dispose`. |
| A16 | [Luna/API] Caching: the wrappers cache only the gate object. Every invoke is a live IPC call, and no result is cached. |
| A17 | [Proteus PenumbraBridge.cs:686-714] Every Penumbra write (P8, P9, P10, P14 to P22, P26, P27) runs on the framework thread. From any other thread, Proteus blocks on `RunOnFrameworkThread` and times the hop. Reads (P2 to P7, P11 to P13, P25, P28) run on the caller's thread. P13 and P28 must be called on the framework thread (Proteus's doc comments on `ResolvePlayerPaths` and `GetPlayerMetaManipulations` say so). P23, P24 and every Glamourer call run on the caller's thread. Proteus owns all of this behaviour, not the API. |
| A18 | [Proteus] Echo filters that the call gates depend on: P29 drops a `TemporaryMod` change with an empty directory within 1000 ms of Proteus's own P26 or P27. G7 and G8 drop the `Reapply` echo within 250 ms of Proteus's own G4, or before an `ExpectOwnReapplyUntil` deadline. G7 also skips an Equip or BonusItem raised on the same thread during Proteus's own G5 or G6 call. The `[ThreadStatic]` depth counter relies on A12's synchronous delivery. |

## 5. How to check it mechanically

This section is for a Mono.Cecil test (plan D26) that reads the built `Penumbra.Api.dll` and `Glamourer.Api.dll` and matches each §1, §2 and §6 row.

Where each row lives:

| Row | Assembly | Type (full name) |
|---|---|---|
| P1 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ApiVersion |
| P2 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetModList |
| P3 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetModDirectory |
| P4 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetCollectionForObject |
| P5 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetCollections |
| P6 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetCurrentModSettingsWithTemp |
| P7 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetAllModSettings |
| P8 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.RemoveTemporaryModSettings |
| P9 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.SetTemporaryModSettings |
| P10 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.RemoveAllTemporaryModSettings |
| P11 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.QueryTemporaryModSettings |
| P12 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ResolvePlayerPath |
| P13 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ResolvePlayerPaths |
| P14 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.AddMod |
| P15 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ReloadMod |
| P16 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.DeleteMod |
| P17 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.SetModPath |
| P18 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.TrySetMod |
| P19 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.TrySetModPriority |
| P20 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.TryInheritMod |
| P21 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.TrySetModSetting |
| P22 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.TrySetModSettings |
| P23 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.RedrawObject |
| P24 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.OpenMainWindow |
| P25 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths |
| P26 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.AddTemporaryMod |
| P27 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.RemoveTemporaryMod |
| P28 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GetPlayerMetaManipulations |
| P29 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ModSettingChanged |
| P30 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ModAdded |
| P31 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ModDeleted |
| P32 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.ModMoved |
| P33 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.GameObjectRedrawn |
| P34 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.Initialized |
| P35 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.Disposed |
| G1 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.GetDesignList |
| G2 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.GetDesignJObject |
| G3 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.GetState |
| G4 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.ReapplyState |
| G5 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.SetBonusItem |
| G6 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.SetItem |
| G7 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.StateChangedWithType |
| G8 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.StateFinalized |
| H1 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.ApplyDesign |
| H2 | Glamourer.Api.dll | Glamourer.Api.IpcSubscribers.ApplyState |
| H3 | Penumbra.Api.dll | Penumbra.Api.IpcSubscribers.InstallMod |

Rules for the test:
- M1 **Namespace.** Look up each type by its exact full name above. Both assemblies also have a `….IpcSubscribers.Legacy` namespace that reuses some simple names with older labels; for example `Penumbra.Api.IpcSubscribers.Legacy.GetGameObjectResourcePaths` has label `Penumbra.GetGameObjectResourcePaths`. Never match by simple name alone.
- M2 **Label.** Each type has a public static literal field named `Label` of type System.String. In Cecil it is a FieldDefinition with `HasConstant` true, and its `Constant` is the exact label string: the compiler folds the interpolated `nameof` into a plain literal. That is the value to compare with the row's Label column. The same literal also appears as an `ldstr` operand in IL. For Func and Action types it is in the instance constructor (`.ctor`), which passes it to the base constructor. For event types it is in the public static method `Subscriber`, which passes it to the event wrapper's constructor. That gives an optional second check.
- M3 **Not LabelU8.** Ignore the static property `LabelU8`: its getter returns a span over a data blob in `<PrivateImplementationDetails>`. For G7 and G8 that blob says `Glamourer.…` while `Label` says `Penumbra.…` (R2.1), and the gate uses `Label`. Some API types have no `LabelU8` at all.
- M4 **Kind.** The type is an Event if it is a static class (`IsAbstract` and `IsSealed`, no instance constructor) with a public static method named `Subscriber`. Otherwise it is a class (sealed or not: P1 and P3 are not sealed) with a generic base type, and its kind comes from the public instance methods named `Invoke` declared on the type itself. If every such method returns System.Void, it is an Action (P23). Otherwise it is a Func. H2 has two `Invoke` overloads, and both return GlamourerApiEc.
- M5 **Generic arguments, Func.** `TypeDefinition.BaseType` is a GenericInstanceType. Its `GenericArguments`, in order, are exactly the gate's generic arguments: the Arguments columns followed by Returns. A zero-argument Func (P1, P2, P3, P5, P28, G1) has one generic argument, the return type.
- M6 **Generic arguments, Action.** The base type's `GenericArguments` are the Arguments columns only. The gate appends System.Object.
- M7 **Generic arguments, Event.** The `Subscriber` method's `ReturnType` is a GenericInstanceType whose `GenericArguments` are the Arguments columns. It is a non-generic type for P34 and P35, which have no arguments. The gate appends System.Object. The method's last parameter is an array of System.Action (generic for P29 to P33 and G7 to G8) whose generic arguments are the same list. That gives a second check.
- M8 **Type spelling.** Compare the erased Cecil types. In Cecil's `FullName` form: string is System.String, int System.Int32, uint System.UInt32, ulong System.UInt64, ushort System.UInt16, byte System.Byte, bool System.Boolean, nint System.IntPtr, object System.Object, Guid System.Guid. An n-tuple is a GenericInstanceType of ``System.ValueTuple`n``. A value-type `T?` is ``System.Nullable`1<T>``. Arrays are ArrayType, so `string[][]` is an ArrayType of an ArrayType. Generic collections are their ``System.Collections.Generic.Dictionary`2``, ``List`1``, ``HashSet`1``, ``IReadOnlyDictionary`2`` and ``IReadOnlyList`1`` instances. JObject is Newtonsoft.Json.Linq.JObject. The API enums (P29, G7, G8) are TypeReferences to Penumbra.Api.Enums.ModSettingChange, Glamourer.Api.Enums.StateChangeType and Glamourer.Api.Enums.StateFinalizationType in the same module.
- M9 **Names and reference nullability are not in the signature.** Tuple element names are in a TupleElementNamesAttribute, and reference-type `?` is in a NullableAttribute. For a base type both sit on the TypeDefinition; for an event they sit on the `Subscriber` method's return value. The test should compare the erased types, and may skip both attributes.
- M10 **Enums (§3).** Each enum is a TypeDefinition in `Penumbra.Api.Enums` or `Glamourer.Api.Enums` with `IsEnum` true. Its underlying type is the type of the special instance field `value__`. Each member is a static literal field whose `Constant` is the value, boxed as the underlying type. `[Flags]` is a System.FlagsAttribute in `CustomAttributes`; only ApplyFlag has it. ApplyFlagEx is a static class whose three fields are literals of type ApplyFlag, with UInt64 constants 7, 14 and 6.
- M11 **No IL from Luna is needed.** The `GetIpcSubscriber` call itself is inside Luna's assembly, not the API's. The API assemblies hold only the base-type instantiation (M5 and M6) and the `Subscriber` return type (M7), and those are enough.

## 6. Calls only the harness makes

Used only in `Proteus/TestHarness/GameHarnessAdapter.cs`, which is compiled only into local harness builds (ADR 0002). Plan D24 names the owned replacements `GlamourerApplyDesign` and `GlamourerApplyState`. Every other harness IPC call goes through the two bridges (§1, §2).

| Owner | Label | Kind | Arguments | Returns | Used in |
|---|---|---|---|---|---|
| H1 Glamourer | `Glamourer.ApplyDesign` | Func | `Guid` designId, `int` objectIndex (harness passes 0), `uint` key (harness passes 0), `ulong` (ApplyFlag as ulong; harness passes ApplyFlagEx.DesignDefault = 7) | `int` (GlamourerApiEc) | GameHarnessAdapter: field `applyDesign` (constructor); `ApplyDesign` (returns null on Success, else the code's name, or "unavailable" on an exception) |
| H2 Glamourer | `Glamourer.ApplyState` | Func | `object` state (the harness passes a JObject, a deep copy of a G3 state; the API also accepts a base64 string here), `int` objectIndex (0), `uint` key (0), `ulong` (ApplyFlag as ulong; harness passes Once, Equipment, Customization = 7) | `int` (GlamourerApiEc) | GameHarnessAdapter: field `applyState` (constructor); `RestoreGlamourerState` (same result mapping as H1) |
| H3 Penumbra | `Penumbra.InstallMod.V5` | Func | `string` modFilePackagePath (an existing absolute .pmp the install route was given) | `int` (PenumbraApiEc) | GameHarnessAdapter: field `installMod` (constructor); `InstallMod` (framework thread; SystemDisposed while Penumbra is unavailable, UnknownError on an exception). Penumbra imports the package itself, in the background, into its own mod root: Success means queued |

Rules:
- R6.1 In H2 the first generic argument is System.Object, not JObject: the gate takes either form, and Dalamud passes whichever object it is given (A6).
- R6.2 `Proteus.Tests/HarnessBindingsWiringTests.cs` at HEAD asserts the text `new Glamourer.Api.IpcSubscribers.ApplyDesign(pluginInterface)` in the adapter, and asserts that neither ApplyDesign nor ApplyState appears in GlamourerBridge. Plan D26 updates these names.
