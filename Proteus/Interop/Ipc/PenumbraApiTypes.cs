// SPDX-License-Identifier: LicenseRef-Proteus
// Only the enum names, member names and values are taken from Penumbra's IPC API, as interoperability facts; no
// Penumbra.Api code is copied or shipped (see THIRD_PARTY_NOTICES.txt, "Interoperability").
namespace Penumbra.Api.Enums;

// Proteus's own copies of the enums that cross Penumbra's IPC call gates (docs/ipc-contract.md §3). Names, values and
// underlying types must match Penumbra's: event arguments of these types are converted by value across the gate.

public enum ModSettingChange
{
    Inheritance = 0,
    EnableState = 1,
    Priority = 2,
    Setting = 3,
    MultiInheritance = 4,
    MultiEnableState = 5,
    TemporaryMod = 6,
    Edited = 7,
    TemporarySetting = 8,
}

public enum PenumbraApiEc
{
    Success = 0,
    NothingChanged = 1,
    CollectionMissing = 2,
    ModMissing = 3,
    OptionGroupMissing = 4,
    OptionMissing = 5,
    CharacterCollectionExists = 6,
    LowerPriority = 7,
    InvalidGamePath = 8,
    FileMissing = 9,
    InvalidManipulation = 10,
    InvalidArgument = 11,
    PathRenameFailed = 12,
    CollectionExists = 13,
    AssignmentCreationDisallowed = 14,
    AssignmentDeletionDisallowed = 15,
    InvalidIdentifier = 16,
    SystemDisposed = 17,
    AssignmentDeletionFailed = 18,
    TemporarySettingDisallowed = 19,
    TemporarySettingImpossible = 20,
    InvalidCredentials = 21,
    CollectionInactive = 22,
    UnknownError = 255,
}

public enum RedrawType
{
    Redraw = 0,
    AfterGPose = 1,
}

public enum TabType
{
    None = -1,
    Settings = 0,
    Mods = 1,
    Collections = 2,
    ChangedItems = 3,
    EffectiveChanges = 4,
    ResourceWatcher = 5,
    Debug = 6,
    ResourceManager = 7,
    OnScreen = 8,
    Messages = 9,
    Management = 10,
}
