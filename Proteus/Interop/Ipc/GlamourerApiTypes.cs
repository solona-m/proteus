// SPDX-License-Identifier: Apache-2.0
// Enum and constant names and values from Glamourer.Api (Copyright Ottermandias, Apache License 2.0), copied as interface facts so Proteus can talk to Glamourer over IPC.

using System;

namespace Glamourer.Api.Enums;

public enum GlamourerApiEc
{
    Success = 0,
    NothingDone = 1,
    ActorNotFound = 2,
    ActorNotHuman = 3,
    DesignNotFound = 4,
    ItemInvalid = 5,
    InvalidKey = 6,
    InvalidState = 7,
    CouldNotParse = 8,
    UnknownError = int.MaxValue,
}

[Flags]
public enum ApplyFlag : ulong
{
    Once = 0x01,
    Equipment = 0x02,
    Customization = 0x04,
    Lock = 0x08,
}

public static class ApplyFlagEx
{
    public const ApplyFlag DesignDefault = ApplyFlag.Once | ApplyFlag.Equipment | ApplyFlag.Customization;
    public const ApplyFlag StateDefault = ApplyFlag.Equipment | ApplyFlag.Customization | ApplyFlag.Lock;
    public const ApplyFlag RevertDefault = ApplyFlag.Equipment | ApplyFlag.Customization;
}

public enum ApiEquipSlot : byte
{
    Unknown = 0,
    MainHand = 1,
    OffHand = 2,
    Head = 3,
    Body = 4,
    Hands = 5,
    Legs = 7,
    Feet = 8,
    Ears = 9,
    Neck = 10,
    Wrists = 11,
    RFinger = 12,
    LFinger = 14,
}

public enum ApiBonusSlot : byte
{
    Unknown = 0,
    Glasses = 1,
}

public enum StateChangeType
{
    Model = 0,
    EntireCustomize = 1,
    Customize = 2,
    Equip = 3,
    Weapon = 4,
    Stains = 5,
    Crest = 6,
    Parameter = 7,
    MaterialValue = 8,
    Design = 9,
    Reset = 10,
    Other = 11,
    Reapply = 12,
    BonusItem = 13,
}

public enum StateFinalizationType
{
    ModelChange = 0,
    DesignApplied = 1,
    Revert = 2,
    RevertCustomize = 3,
    RevertEquipment = 4,
    RevertAdvanced = 5,
    RevertAutomation = 6,
    Reapply = 7,
    ReapplyAutomation = 8,
    Gearset = 9,
}
