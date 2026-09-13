namespace SteamEyaWinUI.Models;

internal static class CsLoadoutConstants
{
    public const uint AdjustEquipSlotsManual = 2531;

    public const uint SoCreate = 21;
    public const uint SoUpdate = 22;
    public const uint SoDestroy = 23;
    public const uint SoCacheSubscribed = 24;
    public const uint SoUpdateMultiple = 26;
    public const uint SoCacheSubscriptionRefresh = 28;
    public const int SoTypeEquipSlot = 3;
    public const int SoTypeDefaultEquippedDefinition = 43;
    public const uint SoOwnerTypeIndividual = 1;

    public const uint TeamTerrorist = 2;
    public const uint TeamCounterTerrorist = 3;

    public const ulong ItemIdDefaultItemMask = 0xF000000000000000;
    public const uint ItemDefinitionRevolver = 64;
    public const uint ItemDefinitionDeagle = 1;

    public const uint SecondarySlotDeagleDefault = 6;

    public static readonly uint[] SecondarySlots = [3, 4, 5, 6, 7];

    public static ulong BuildDefaultBaseItemId(uint itemDefinition) =>
        ItemIdDefaultItemMask | itemDefinition;

    // 每个 (阵营, 槽位) 的游戏内置默认武器 itemdef。取自 items_game.txt 的 flexible_loadout_default 标记
    // （按 used_by_classes 阵营专属消歧），并用测试账号 GC 实测逐槽核对：SO 缓存里「缺席」的槽恰好都是
    // 目标 == 该表默认值的槽。有了它，任一槽的真实武器 = 显式 SO 条目（若有）否则此默认值——校验无需再靠
    // 「缺席 + GC 有响应」猜测，既不会把「已是默认」的槽误报失败，也不会在 GC 未生效时误报成功。
    private static readonly IReadOnlyDictionary<(uint Team, uint Slot), uint> DefaultLoadout =
        new Dictionary<(uint, uint), uint>
        {
            // T：secondary0-4 / smg0-4 / rifle0-4
            [(TeamTerrorist, 2)] = 4,   [(TeamTerrorist, 3)] = 2,   [(TeamTerrorist, 4)] = 36,
            [(TeamTerrorist, 5)] = 30,  [(TeamTerrorist, 6)] = 1,
            [(TeamTerrorist, 8)] = 35,  [(TeamTerrorist, 9)] = 25,  [(TeamTerrorist, 10)] = 23,
            [(TeamTerrorist, 11)] = 19, [(TeamTerrorist, 12)] = 17,
            [(TeamTerrorist, 14)] = 13, [(TeamTerrorist, 15)] = 7,  [(TeamTerrorist, 16)] = 40,
            [(TeamTerrorist, 17)] = 39, [(TeamTerrorist, 18)] = 9,

            // CT
            [(TeamCounterTerrorist, 2)] = 32,  [(TeamCounterTerrorist, 3)] = 2,   [(TeamCounterTerrorist, 4)] = 36,
            [(TeamCounterTerrorist, 5)] = 3,   [(TeamCounterTerrorist, 6)] = 1,
            [(TeamCounterTerrorist, 8)] = 35,  [(TeamCounterTerrorist, 9)] = 25,  [(TeamCounterTerrorist, 10)] = 23,
            [(TeamCounterTerrorist, 11)] = 19, [(TeamCounterTerrorist, 12)] = 34,
            [(TeamCounterTerrorist, 14)] = 10, [(TeamCounterTerrorist, 15)] = 60, [(TeamCounterTerrorist, 16)] = 40,
            [(TeamCounterTerrorist, 17)] = 8,  [(TeamCounterTerrorist, 18)] = 9,
        };

    // 该槽的游戏内置默认 itemdef；表外槽位（近战/Zeus 等）返回 false。
    public static bool TryGetImplicitDefault(uint team, uint slot, out uint itemDefinition) =>
        DefaultLoadout.TryGetValue((team, slot), out itemDefinition);
}
