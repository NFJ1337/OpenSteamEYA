using System.Globalization;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class CsLoadoutService
{
    private static readonly TimeSpan EquipSoWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly SemaphoreSlim EquipGate = new(1, 1);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // 一键装配整套预设：把两阵营各槽位的原版武器（itemdef）一次性写入，读回校验。
    public async Task<CsLoadoutApplyResult> ApplyPresetAsync(
        CsLoadoutPreset preset,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!await EquipGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new InvalidOperationException(Loc.T("Cs_Loadout_Busy"));
        }

        try
        {
            return await ApplyPresetCoreAsync(preset, refreshToken, steamId, cancellationToken);
        }
        finally
        {
            EquipGate.Release();
        }
    }

    private async Task<CsLoadoutApplyResult> ApplyPresetCoreAsync(
        CsLoadoutPreset preset,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
            {
                throw new InvalidOperationException(Loc.T("Cs_Loadout_BadSteam64"));
            }

            var accountId = CsGcSession.GetAccountId(steamId64);

            var requested = new List<(uint Team, uint Slot, uint Def)>();
            foreach (var (slot, def) in preset.T)
            {
                requested.Add((CsLoadoutConstants.TeamTerrorist, slot, def));
            }
            foreach (var (slot, def) in preset.Ct)
            {
                requested.Add((CsLoadoutConstants.TeamCounterTerrorist, slot, def));
            }

            if (requested.Count == 0)
            {
                return new CsLoadoutApplyResult(0, 0, []);
            }

            // 预检：只发送武器目录内、阵营可用、槽位组匹配的条目；非法条目（手改 settings.json 等）直接记失败。
            // 这是后面「无显式 SO 条目 ⇒ 判定达标」的前提：GC 对合法请求要么改写 SO，要么槽位本就是目标武器。
            var failures = new List<string>();
            var validRequests = new List<(uint Team, uint Slot, uint Def)>();
            foreach (var item in requested)
            {
                var weapon = CsWeaponCatalog.ByDef(item.Def);
                if (weapon is not null &&
                    weapon.UsableBy(item.Team == CsLoadoutConstants.TeamCounterTerrorist) &&
                    CsWeaponCatalog.SlotsForGroup(CsWeaponCatalog.GroupOf(weapon)).Contains(item.Slot))
                {
                    validRequests.Add(item);
                }
                else
                {
                    failures.Add(DescribeSlot(item.Team, item.Slot, item.Def));
                }
            }

            await using var cmClient = new SteamCmClient(HttpClient);
            await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);

            try
            {
                await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
                var welcomePayload = await CsGcSession.ConnectAsync(cmClient, cancellationToken);

                // 读当前配装（welcome 内嵌 SO 缓存，已放宽到全槽位）。SO 缓存只存与游戏内置默认布局不同的
                // 显式条目；缺席的槽位处于其内置默认武器。用「显式条目 → 有则用，无则查默认表」解析出每个槽的
                // 真实武器，据此算差异——这既是权威也无需任何「缺席 = 达标」猜测（见 CsLoadoutConstants 默认表）。
                var explicitCurrent = new Dictionary<(uint Team, uint Slot), uint>();
                foreach (var entry in CsSoCacheParser.ParseLoadoutFromWelcome(welcomePayload, accountId))
                {
                    explicitCurrent[(entry.ClassId, entry.SlotId)] = entry.ItemDefinition;
                }

                // 已是目标武器（解析后一致）的槽位无需重发，只发差异。
                var toSend = new List<(uint Team, uint Slot, uint Def)>();
                foreach (var item in validRequests)
                {
                    if (ResolveSlot(explicitCurrent, item.Team, item.Slot) != item.Def)
                    {
                        toSend.Add(item);
                    }
                }

                if (toSend.Count == 0)
                {
                    AppLog.Info($"一键配装：{validRequests.Count} 个位置已是目标状态，无需改动（非法条目 {failures.Count} 个）。");
                    return new CsLoadoutApplyResult(requested.Count, validRequests.Count, failures);
                }

                var tappedMessages = new List<SteamCmClient.SteamGcClientMessage>();
                var tappedMessagesLock = new object();
                cmClient.SetGcMessageTap(message =>
                {
                    lock (tappedMessagesLock)
                    {
                        tappedMessages.Add(message);
                    }
                });

                // 增量终态从「初始显式条目」起步，逐条应用 GC 回包的 SO 更新/删除。
                var finalExplicit = new Dictionary<(uint Team, uint Slot), uint>(explicitCurrent);
                try
                {
                    CsSoCacheParser.TryGetSoCacheVersionFromWelcome(welcomePayload, out var soCacheVersion);
                    var changeNum = BuildChangeNum(soCacheVersion);

                    var slotEntries = toSend
                        .Select(r => (r.Team, r.Slot, CsLoadoutConstants.BuildDefaultBaseItemId(r.Def)))
                        .ToList();

                    // 发送前抓到的消息（握手尾随的 SO 缓存等）不能算作对本次 2531 的响应，记录基线跳过它们。
                    int baselineTapCount;
                    lock (tappedMessagesLock)
                    {
                        baselineTapCount = tappedMessages.Count;
                    }

                    await cmClient.SendGcProtobufMessageAsync(
                        CsGcSession.Cs2AppId,
                        CsLoadoutConstants.AdjustEquipSlotsManual,
                        EncodeAdjustEquipSlotsMulti(slotEntries, changeNum),
                        cancellationToken);

                    try
                    {
                        // GC 对每条 2531 都回同号 ACK，SO 更新与 ACK 同批到达；等到任一响应即可。
                        await WaitForEquipResponseAsync(
                            cmClient,
                            tappedMessages,
                            tappedMessagesLock,
                            baselineTapCount,
                            cancellationToken);

                        // 整套配装可能分多条 SO 更新返回，首条到达后再宽限一会，收齐尾随更新再合并。
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // 装配请求已发出，GC 大概率已执行；此时按「已取消」提示会误导用户以为没生效。
                        throw new InvalidOperationException(Loc.T("Cs_Loadout_CancelledAfterSend"));
                    }

                    ApplyTappedLoadoutMessages(finalExplicit, tappedMessages, tappedMessagesLock, baselineTapCount, accountId);
                }
                finally
                {
                    cmClient.SetGcMessageTap(null);
                }

                // 校验：每个槽的真实武器 = 显式条目（有则用）否则该槽内置默认。缺席永远等于默认（CS2 语义），
                // 因此无论 GC 是否真生效，这里都给出 in-game 实况——不会把「已是默认」的槽误判失败（旧版非 30/30
                // 根因），也不会在 GC 未生效时误判成功（增量丢包时下面兜底重读权威 welcome 纠正）。
                bool IsSatisfied((uint Team, uint Slot, uint Def) item) =>
                    ResolveSlot(finalExplicit, item.Team, item.Slot) == item.Def;

                // 仍有未达标槽位时重拉 welcome 读回全量 SO 缓存（实测重复 hello 必重发完整缓存），用权威快照
                // 整体重建显式条目集——若只做合并，被 GC 删除的条目会残留陈旧值造成误报。
                if (!validRequests.All(IsSatisfied))
                {
                    try
                    {
                        var freshWelcome = await CsGcSession.RequestWelcomeAsync(
                            cmClient,
                            TimeSpan.FromSeconds(15),
                            cancellationToken);
                        finalExplicit = new Dictionary<(uint Team, uint Slot), uint>();
                        foreach (var entry in CsSoCacheParser.ParseLoadoutFromWelcome(freshWelcome, accountId))
                        {
                            finalExplicit[(entry.ClassId, entry.SlotId)] = entry.ItemDefinition;
                        }
                    }
                    catch (TimeoutException)
                    {
                        // 兜底读回超时则沿用现有判定，不阻断结果返回。
                    }
                    catch (OperationCanceledException)
                    {
                        throw new InvalidOperationException(Loc.T("Cs_Loadout_CancelledAfterSend"));
                    }
                }

                var confirmed = 0;
                var implicitConfirmed = 0;
                foreach (var item in validRequests)
                {
                    if (IsSatisfied(item))
                    {
                        confirmed++;
                        if (!finalExplicit.ContainsKey((item.Team, item.Slot)))
                        {
                            implicitConfirmed++;
                        }
                    }
                    else
                    {
                        failures.Add(DescribeSlot(item.Team, item.Slot, item.Def));
                    }
                }

                var result = new CsLoadoutApplyResult(requested.Count, confirmed, failures);
                AppLog.Info(
                    $"一键配装：请求 {result.Requested}，确认 {result.Confirmed}" +
                    $"（其中已是内置默认 {implicitConfirmed} 个），失败 {failures.Count}。");
                return result;
            }
            finally
            {
                try
                {
                    await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
                }
                catch
                {
                    // best-effort
                }
            }
        }
        catch (Exception ex)
        {
            if (IsSteamSessionConflict(ex))
            {
                var conflict = new InvalidOperationException(
                    (ex.Data["CmConflict"] as string) == "SessionReplaced"
                        ? ex.Message
                        : Loc.T("Cs_Loadout_CmDisconnected"),
                    ex);
                AppLog.Error($"一键配装失败：{conflict.Message}");
                throw conflict;
            }

            AppLog.Error($"一键配装失败：{ex.Message}");
            throw;
        }
    }

    // 等待 GC 对 2531 的任一响应（ACK 或 SO 更新）。超时不抛：后续「合并 + fresh welcome 兜底」会给出判定。
    private static async Task WaitForEquipResponseAsync(
        SteamCmClient cmClient,
        List<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + EquipSoWaitTimeout;
        var processedCount = startIndex;
        var waitTypes = new[]
        {
            CsLoadoutConstants.AdjustEquipSlotsManual,
            CsLoadoutConstants.SoUpdateMultiple,
            CsLoadoutConstants.SoCacheSubscribed,
            CsLoadoutConstants.SoUpdate,
            CsLoadoutConstants.SoCreate,
            CsLoadoutConstants.SoDestroy
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            List<SteamCmClient.SteamGcClientMessage> pendingMessages;
            lock (tappedMessagesLock)
            {
                pendingMessages = tappedMessages
                    .Skip(processedCount)
                    .ToList();
                processedCount = tappedMessages.Count;
            }

            foreach (var message in pendingMessages)
            {
                if (IsEquipResponseMessage(message.MessageType))
                {
                    return;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var waitSlice = remaining < TimeSpan.FromSeconds(1)
                ? remaining
                : TimeSpan.FromSeconds(1);

            foreach (var msgType in waitTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.WaitForGcMessageAsync(
                        CsGcSession.Cs2AppId,
                        msgType,
                        waitSlice,
                        cancellationToken,
                        cacheUnmatched: true);
                    return;
                }
                catch (TimeoutException)
                {

                }
            }
        }
    }

    private static bool IsEquipResponseMessage(uint msgType) =>
        msgType == CsLoadoutConstants.AdjustEquipSlotsManual ||
        CsSoCacheParser.IsLoadoutSoMessage(msgType);

    // 把发送后抓到的 GC SO 消息按到达顺序落到显式条目状态上（跳过基线之前的握手尾随消息）。
    private static void ApplyTappedLoadoutMessages(
        Dictionary<(uint Team, uint Slot), uint> state,
        IReadOnlyList<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        int startIndex,
        uint accountId)
    {
        List<SteamCmClient.SteamGcClientMessage> messages;
        lock (tappedMessagesLock)
        {
            messages = tappedMessages.Skip(startIndex).ToList();
        }

        foreach (var message in messages)
        {
            if (CsSoCacheParser.IsLoadoutSoMessage(message.MessageType))
            {
                CsSoCacheParser.ApplyLoadoutSoMessage(state, message.MessageType, message.Payload, accountId);
            }
        }
    }

    // 某槽的真实武器：显式 SO 条目优先，否则该槽内置默认；两者都无（表外槽位）返回 0。
    private static uint ResolveSlot(
        IReadOnlyDictionary<(uint Team, uint Slot), uint> explicitEntries,
        uint team,
        uint slot) =>
        explicitEntries.TryGetValue((team, slot), out var def)
            ? def
            : CsLoadoutConstants.TryGetImplicitDefault(team, slot, out var fallback)
                ? fallback
                : 0;

    private static string DescribeSlot(uint team, uint slot, uint def)
    {
        var teamName = team == CsLoadoutConstants.TeamTerrorist ? "T" : "CT";
        var name = CsWeaponCatalog.ByDef(def)?.LocalizedName ?? def.ToString(CultureInfo.InvariantCulture);
        return $"{teamName} #{slot} {name}";
    }

    // 用 SteamCmClient 打在异常上的语言中立标记判定，不依赖本地化后的 Message 文本。
    private static bool IsSteamSessionConflict(Exception ex) =>
        ex.Data["CmConflict"] is string;

    private static uint BuildChangeNum(ulong soCacheVersion) =>
        soCacheVersion != 0
            ? (uint)((soCacheVersion + 1) & 0xFFFFFFFF)
            : 1;

    // 每槽各自带 itemId 的批量装配消息（整套配装一次发出）。
    private static byte[] EncodeAdjustEquipSlotsMulti(
        IReadOnlyList<(uint ClassId, uint SlotId, ulong ItemId)> slots,
        uint changeNum) =>
        SteamProtoWriter.Build(writer =>
        {
            foreach (var (classId, slotId, itemId) in slots)
            {
                writer.WriteBytes(1, SteamProtoWriter.Build(slotWriter =>
                {
                    slotWriter.WriteUInt32(1, classId);
                    slotWriter.WriteUInt32(2, slotId);
                    slotWriter.WriteUInt64(3, itemId);
                }));
            }

            writer.WriteUInt32(2, changeNum);
        });
}
