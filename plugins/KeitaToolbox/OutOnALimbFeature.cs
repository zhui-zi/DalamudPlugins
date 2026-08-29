using System;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Network;
using OmenTools.Info.Game.Packets.Downstream;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed unsafe class OutOnALimbFeature : IDisposable
{
    private const uint EventId = 2_359_302;
    private const uint RoundStartCategory = 17_235_982;
    private const uint DifficultyCategory = 17_367_054;
    private const uint NextRoundCategory = 720_910;
    private const uint CutTreeCategory = 17_432_590;
    private const uint GoldSaucerTerritory = 388;
    private const string RestartSchedule = "OutOnALimb:Restart";

    private readonly Hook<PacketDispatcher.Delegates.HandleEventYieldPacket> eventYieldHook;
    private readonly OutOnALimbSearchPolicy search = new();
    private int currentRound;
    private uint hitPosition;
    private long gameStartTimestamp;
    private bool running;
    private int disposeState;

    public OutOnALimbFeature()
    {
        eventYieldHook =
            Plugin.Interop.HookFromAddress<PacketDispatcher.Delegates.HandleEventYieldPacket>(
                (nint)PacketDispatcher.MemberFunctionPointers.HandleEventYieldPacket,
                HandleEventYieldDetour);
        eventYieldHook.Enable();
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup,
            "MiniGameAimg",
            OnMiniGameSetup);
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref disposeState, 1) != 0)
            return;

        Plugin.Scheduler.Cancel(RestartSchedule);
        Plugin.AddonLifecycle.UnregisterListener(
            AddonEvent.PostSetup,
            "MiniGameAimg",
            OnMiniGameSetup);
        eventYieldHook.Dispose();
        if (running)
            CompleteGame();
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader(
                "自动游玩孤树无援",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (Plugin.DrawFeatureToggle(
                "自动游玩孤树无援",
                Plugin.Config.Features.AutoOutOnALimb,
                value => Plugin.Config.Features.AutoOutOnALimb = value) &&
            !Plugin.Config.Features.AutoOutOnALimb)
        {
            Stop();
        }

        Plugin.DrawHelp("自动选择最高难度，根据每次砍伐结果定位目标，并在完成后持续重开。");

        var canStart = Plugin.Config.Features.AutoOutOnALimb &&
                       Plugin.ClientState.TerritoryType == GoldSaucerTerritory &&
                       !running &&
                       !Plugin.Condition[ConditionFlag.OccupiedInEvent];
        if (!canStart)
            ImGui.BeginDisabled();
        if (ImGui.Button("开始"))
            Start();
        if (!canStart)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (!running)
            ImGui.BeginDisabled();
        if (ImGui.Button("停止"))
            Stop();
        if (!running)
            ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(running ? $"进行中：第 {currentRound + 1} 回合" : "未运行");
    }

    private void Start()
    {
        if (!Plugin.Config.Features.AutoOutOnALimb ||
            Plugin.ClientState.TerritoryType != GoldSaucerTerritory ||
            Plugin.ObjectTable.LocalPlayer == null)
        {
            return;
        }

        Plugin.Scheduler.Cancel(RestartSchedule);
        ResetRound();
        running = true;
        new EventStartPackt(LocalPlayerState.EntityID, EventId).Send();
    }

    private void Stop()
    {
        Plugin.Scheduler.Cancel(RestartSchedule);
        if (running)
            CompleteGame();
        running = false;
        currentRound = 0;
        ResetRound();
    }

    private static void OnMiniGameSetup(AddonEvent _, AddonArgs __)
    {
        if (!Plugin.Config.Features.AutoOutOnALimb)
            return;

        new EventActionPacket(EventId, RoundStartCategory).Send();
        new EventActionPacket(EventId, DifficultyCategory, 2).Send();
    }

    private void HandleEventYieldDetour(
        EventId eventId,
        short scene,
        byte yieldId,
        int* intParams,
        byte intParamsCount)
    {
        eventYieldHook.Original(eventId, scene, yieldId, intParams, intParamsCount);

        if (!Plugin.Config.Features.AutoOutOnALimb ||
            eventId != EventId ||
            intParams == null)
        {
            return;
        }

        try
        {
            var packet = (OutOnALimbPacket*)intParams;
            switch (yieldId)
            {
                case 23 when packet->Result == OutOnALimbResult.Start &&
                             packet->Health == 10 &&
                             packet->BonusLevel == 5:
                    running = true;
                    currentRound = 0;
                    gameStartTimestamp = Stopwatch.GetTimestamp();
                    ResetRound();
                    CutAt(20);
                    break;
                case 24:
                    HandleRoundResult(*packet);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to process an Out on a Limb result.");
            Stop();
        }
    }

    private void HandleRoundResult(OutOnALimbPacket packet)
    {
        if (!running)
            return;

        if (packet.Health != 0)
        {
            if (packet.BonusLevel != 5)
                CutAt(search.SelectNext(hitPosition, (int)packet.Result + 1));
            return;
        }

        currentRound++;
        if (packet.Result == OutOnALimbResult.Fail)
        {
            ScheduleRestart(TimeSpan.Zero);
            return;
        }

        if (currentRound >= 6)
        {
            var remaining = TimeSpan.FromSeconds(10) -
                            Stopwatch.GetElapsedTime(gameStartTimestamp);
            ScheduleRestart(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            return;
        }

        ResetRound();
        new EventActionPacket(EventId, NextRoundCategory).Send();
        CutAt(20);
    }

    private void ScheduleRestart(TimeSpan delay)
    {
        Plugin.Scheduler.Cancel(RestartSchedule);
        Plugin.Scheduler.Schedule(
            RestartSchedule,
            (int)Math.Clamp(delay.TotalMilliseconds, 0, int.MaxValue),
            () =>
            {
                if (!running || !Plugin.Config.Features.AutoOutOnALimb)
                    return;

                CompleteGame();
                ResetRound();
                Plugin.Scheduler.Schedule(RestartSchedule, 100, Start);
            });
    }

    private void ResetRound()
    {
        hitPosition = 20;
        search.Reset();
    }

    private void CutAt(uint position)
    {
        new EventActionPacket(EventId, CutTreeCategory, position).Send();
        hitPosition = position;
    }

    private static void CompleteGame() =>
        new EventCompletePackt(EventId, 14).Send();
}
