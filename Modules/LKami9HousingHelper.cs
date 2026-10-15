using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

using FFXIVClientStructs.FFXIV.Client.Game;

using Lumina.Excel.Sheets;

using OmenTools.Interop.Game.ExecuteCommand.Implementations;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI.Theme;

namespace LKami9.HousingHelper;

/// <summary>
/// LKami9装修助手 —— 批量搬运家具/庭具。
///
/// 两个动作（都走游戏自己的「家具/庭具仓库」指令）：
///   · 批量存入：背包里的家具/庭具 → 房屋仓库
///   · 批量取出：房屋仓库里的家具/庭具 → 背包
///
/// 前置条件（游戏规则，绕不过）：
///   · 必须在自己家 / 院子里（有房屋权限），否则游戏会拒绝
///   · 建议先打开「家具/庭具仓库」窗口（收纳箱 / 房屋菜单），部分情况下游戏要求 UI 上下文
///
/// 指令来自 OmenTools（宿主自带，公开 API）：
///   HousingCommand.Store(container, slot)                 → 存入仓库
///   HousingCommand.Restore(container, slot, toStoreRoom:false) → 从仓库取回
/// 容器号来自 FFXIVClientStructs 的 InventoryType：
///   HousingExteriorStoreroom(27000) / HousingExteriorStoreroom2(27200)  → 庭具置场
///   HousingInteriorStoreroom1..11(27001..27011)                         → 家具仓库
/// </summary>
public sealed class LKami9HousingHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9装修助手",
        Description = "批量搬家具：把背包里的家具/庭具一次存进房屋仓库，或把仓库里的家具/庭具批量取回背包。需要在自己家/院子里，建议先打开「家具/庭具仓库」窗口。",
        Category    = ModuleCategory.Automation,
        Author      = "LKami9",
        Commands    =
        [
            new("批量存入（背包 → 仓库）", "/omni LKami9HousingHelper store"),
            new("批量取出（仓库 → 背包）", "/omni LKami9HousingHelper take"),
        ],
    };

    public override bool HasSettings => true;

    // 宿主按「以 Config 结尾的成员」绑定已保存的配置；不能是 readonly
    private LKami9HousingHelperConfig config = new();

    // ── 搬运状态 ──
    private enum MoveKind { None, Store, TakeOut }

    private MoveKind job = MoveKind.None;
    private int jobWait;                                   // 距上一件过了几帧
    private int jobIntervalFrames = 19;                    // 每件之间隔多少帧（≈300ms）
    private (int Container, int Slot) jobCurrent = (-1, -1);
    private int jobCurrentTries;
    private int jobSkipStreak;
    private int jobStartCount;                             // 开工时源侧还有多少件
    private int jobSent;
    private int jobSkipped;
    private string status = "空闲。先识别……不，先站到自己家里，再点下面两个按钮。";

    private readonly HashSet<(int Container, int Slot)> jobTried = [];
    private HashSet<uint>? furnitureIds;                   // 家具（HousingFurniture）
    private HashSet<uint>? yardIds;                        // 庭具（HousingYardObject）
    private bool frameworkHooked;

    // ══════════════════════════════════════════════════════════
    //  生命周期
    // ══════════════════════════════════════════════════════════

    protected override void OnEnable()
    {
        try
        {
            DalamudServices.Framework.Update += this.OnFrameworkTick;
            this.frameworkHooked = true;
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[装修助手] 注册框架更新失败"); }
    }

    protected override void OnDisable()
    {
        try
        {
            if (this.frameworkHooked) DalamudServices.Framework.Update -= this.OnFrameworkTick;
            this.frameworkHooked = false;
        }
        catch { }
        this.job = MoveKind.None;
    }

    protected override void OnDispose() { }

    public override bool ResetSettings()
    {
        this.config = new LKami9HousingHelperConfig();
        this.status = "设置已重置。";
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        switch ((arguments ?? "").Trim().ToLowerInvariant())
        {
            case "store":
            case "存入":
                this.StartJob(MoveKind.Store);
                return true;
            case "take":
            case "取出":
                this.StartJob(MoveKind.TakeOut);
                return true;
            default:
                return false;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  UI
    // ══════════════════════════════════════════════════════════

    public override bool DrawSettings()
    {
        this.jobIntervalFrames = Math.Clamp(this.config.IntervalMs / 16, 5, 120);

        ImGui.TextUnformatted("① 范围");
        ImGui.SameLine();
        ImGui.TextDisabled("（家具 = 室内家具，庭具 = 院子里的）");

        var fur = this.config.IncludeFurniture;
        if (ImGui.Checkbox("家具###incFur", ref fur)) this.config.IncludeFurniture = fur;
        ImGui.SameLine();
        var yard = this.config.IncludeYard;
        if (ImGui.Checkbox("庭具###incYard", ref yard)) this.config.IncludeYard = yard;

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var ms = this.config.IntervalMs;
        if (ImGui.SliderInt("每件间隔（毫秒）###interval", ref ms, 100, 1000))
            this.config.IntervalMs = ms;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每搬一件之间等这么久。太快游戏可能处理不过来（默认 300）。");

        ImGui.Separator();

        ImGui.TextUnformatted("② 搬运");
        var busy = this.job != MoveKind.None;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("批量存入（背包 → 仓库）")) this.StartJob(MoveKind.Store);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("背包里的 家具/庭具 全部存进房屋仓库");
            ImGui.SameLine();
            if (ImGui.Button("批量取出（仓库 → 背包）")) this.StartJob(MoveKind.TakeOut);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("仓库里的 家具/庭具 全部取回背包");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("停止")) this.StopJob("已手动停止。");
        }

        if (!string.IsNullOrEmpty(this.status))
            ImGui.TextWrapped(this.status);

        ImGui.Separator();
        this.DrawStatusSection();

        return false;
    }

    private void DrawStatusSection()
    {
        ImGui.TextUnformatted("③ 现场情况");

        var (perms, houseText) = CheckHouse();
        if (perms) ImGui.TextColored(OmniTheme.Tokens.Success, houseText);
        else ImGui.TextColored(OmniTheme.Tokens.Warning, houseText);

        var bagFur = this.CountInContainers(BagContainers, this.furnitureIds);
        var bagYard = this.CountInContainers(BagContainers, this.yardIds);
        var storeAll = this.CountInContainers(StoreroomContainers, null);
        var storeReady = this.AnyContainerLoaded(StoreroomContainers);

        ImGui.TextDisabled(
            $"背包：家具 {bagFur} 件 / 庭具 {bagYard} 件");
        if (storeReady)
            ImGui.TextDisabled($"仓库：共 {storeAll} 件可用");
        else
            ImGui.TextColored(OmniTheme.Tokens.Warning,
                "仓库：没读到数据 —— 请先在自己家/院子里打开「家具/庭具仓库」窗口");

        if (this.furnitureIds is { Count: 0 } && this.yardIds is { Count: 0 })
            ImGui.TextColored(OmniTheme.Tokens.Warning, "家具/庭具数据表读取失败（看 /xllog）");

        ImGui.TextDisabled("用法：站到自己家（或院子）里 → 打开「家具/庭具仓库」→ 点上面两个按钮。");
    }

    // ══════════════════════════════════════════════════════════
    //  容器 / 家具索引
    // ══════════════════════════════════════════════════════════

    /// <summary>自己的背包（4 个）。</summary>
    private static readonly InventoryType[] BagContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>房屋仓库：庭具置场两个 + 家具仓库 11 页。</summary>
    private static readonly InventoryType[] StoreroomContainers =
    [
        InventoryType.HousingExteriorStoreroom,        // 27000 庭具置场
        InventoryType.HousingExteriorStoreroom2,       // 27200 庭具置场（第二处）
        InventoryType.HousingInteriorStoreroom1, InventoryType.HousingInteriorStoreroom2,
        InventoryType.HousingInteriorStoreroom3, InventoryType.HousingInteriorStoreroom4,
        InventoryType.HousingInteriorStoreroom5, InventoryType.HousingInteriorStoreroom6,
        InventoryType.HousingInteriorStoreroom7, InventoryType.HousingInteriorStoreroom8,
        InventoryType.HousingInteriorStoreroom9, InventoryType.HousingInteriorStoreroom10,
        InventoryType.HousingInteriorStoreroom11,      // 27001~27011 家具仓库
    ];

    /// <summary>建「家具/庭具 道具ID」索引（Lumina 的 HousingFurniture / HousingYardObject 表）。</summary>
    private void LoadFurnitureIndexIfNeeded()
    {
        if (this.furnitureIds != null && this.yardIds != null) return;

        var fur = new HashSet<uint>();
        var yard = new HashSet<uint>();
        try
        {
            foreach (var row in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>())
            {
                var id = row.Item.RowId;
                if (id != 0) fur.Add(id);
            }
            foreach (var row in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.HousingYardObject>())
            {
                var id = row.Item.RowId;
                if (id != 0) yard.Add(id);
            }
            DalamudServices.PluginLog.Information($"[装修助手] 家具 {fur.Count} 种 / 庭具 {yard.Count} 种");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[装修助手] 读家具表失败");
        }

        this.furnitureIds = fur;
        this.yardIds = yard;
    }

    private bool MatchesFilter(uint itemId)
    {
        this.LoadFurnitureIndexIfNeeded();
        if (this.config.IncludeFurniture && this.furnitureIds!.Contains(itemId)) return true;
        if (this.config.IncludeYard && this.yardIds!.Contains(itemId)) return true;
        return false;
    }

    private unsafe int CountInContainers(InventoryType[] types, HashSet<uint>? filter)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return 0;

        var n = 0;
        foreach (var type in types)
        {
            var cont = mgr->GetInventoryContainer(type);
            if (cont == null || !cont->IsLoaded) continue;
            for (var i = 0; i < cont->Size; i++)
            {
                var slot = cont->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (filter != null && !filter.Contains(slot->ItemId)) continue;
                n++;
            }
        }
        return n;
    }

    private unsafe bool AnyContainerLoaded(InventoryType[] types)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return false;
        foreach (var type in types)
        {
            var cont = mgr->GetInventoryContainer(type);
            if (cont != null && cont->IsLoaded) return true;
        }
        return false;
    }

    /// <summary>是否在自己家（有房屋权限）。</summary>
    private static unsafe (bool Ok, string Text) CheckHouse()
    {
        try
        {
            var hm = HousingManager.Instance();
            if (hm == null) return (false, "房屋：没读到数据");
            return hm->HasHousePermissions()
                ? (true, "房屋：有权限 ✓")
                : (false, "房屋：没有房屋权限 ✗（要先到自己家/院子里，且必须有权限）");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[装修助手] 读房屋权限失败");
            return (false, "房屋：读取失败（看 /xllog）");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  搬运流程
    // ══════════════════════════════════════════════════════════

    private void StartJob(MoveKind kind)
    {
        this.LoadFurnitureIndexIfNeeded();
        if (this.furnitureIds is { Count: 0 } && this.yardIds is { Count: 0 })
        {
            this.status = "家具/庭具数据表没读到，先别搬。";
            return;
        }
        if (!this.config.IncludeFurniture && !this.config.IncludeYard)
        {
            this.status = "「家具」「庭具」两个都没勾，没得搬。";
            return;
        }

        var (perms, _) = CheckHouse();
        if (!perms)
        {
            this.status = "看起来不在自己家（没有房屋权限）—— 游戏会拒绝搬运，先回自己家/院子再点。";
            return;
        }

        this.job = kind;
        this.jobWait = 0;
        this.jobCurrent = (-1, -1);
        this.jobCurrentTries = 0;
        this.jobSkipStreak = 0;
        this.jobSent = 0;
        this.jobSkipped = 0;
        this.jobTried.Clear();

        var srcTypes = kind == MoveKind.Store ? BagContainers : StoreroomContainers;
        this.jobStartCount = kind == MoveKind.Store
            ? this.CountInContainers(srcTypes, null)      // 背包里的全部（含非家具，仅用于显示）
            : this.CountInContainers(srcTypes, null);

        this.status = kind == MoveKind.Store
            ? $"开始存入：源侧共 {this.jobStartCount} 件（会跳过不是家具/庭具的东西）…"
            : $"开始取出：仓库里共 {this.jobStartCount} 件…";
        DalamudServices.PluginLog.Information($"[装修助手] 开始 {(kind == MoveKind.Store ? "存入" : "取出")}，源侧 {this.jobStartCount} 件");
    }

    private void StopJob(string why)
    {
        var kind = this.job;
        this.job = MoveKind.None;
        this.jobCurrent = (-1, -1);

        var srcTypes = kind == MoveKind.Store ? BagContainers : StoreroomContainers;
        var left = this.CountInContainers(srcTypes, null);
        var moved = Math.Max(0, this.jobStartCount - left);

        this.status = $"{why} 已搬 {moved} 件" +
                      (this.jobSkipped > 0 ? $"，跳过 {this.jobSkipped} 件（游戏没收）" : "") +
                      $"，源侧还剩 {left} 件。";
        DalamudServices.PluginLog.Information(
            $"[装修助手] {why} 搬了 {moved} 件，跳过 {this.jobSkipped}，源侧剩 {left}");
    }

    private unsafe void OnFrameworkTick(Dalamud.Plugin.Services.IFramework framework)
    {
        if (this.job == MoveKind.None) return;

        try
        {
            if (++this.jobWait < this.jobIntervalFrames) return;
            this.jobWait = 0;

            if (!this.TryFindNext(out var container, out var slot, out var itemId))
            {
                this.StopJob("搬完了。");
                return;
            }

            // 同一个位置连着没搬动 → 试几次后跳过（游戏拒绝了：仓库满 / 不是家具 / 没权限…）
            if (this.jobCurrent != (container, slot))
            {
                this.jobCurrent = (container, slot);
                this.jobCurrentTries = 0;
            }

            if (this.jobCurrentTries >= 3)
            {
                DalamudServices.PluginLog.Information(
                    $"[装修助手] 跳过：容器 {container} 槽位 {slot} 道具 {itemId}（连续 3 次没搬动）");
                this.jobTried.Add((container, slot));
                this.jobSkipped++;
                this.jobCurrent = (-1, -1);
                this.jobCurrentTries = 0;
                this.jobSkipStreak++;

                if (this.jobSkipStreak >= 10)
                    this.StopJob("连续 10 件都搬不动 —— 可能是仓库已满 / 没打开仓库窗口 / 权限不足。");
                return;
            }

            var type = (InventoryType)container;
            if (this.job == MoveKind.Store)
                HousingCommand.Store(type, (uint)slot);
            else
                HousingCommand.Restore(type, (uint)slot, toStoreRoom: false);

            this.jobSent++;
            this.jobCurrentTries++;
            this.jobSkipStreak = 0;

            this.status = this.job == MoveKind.Store
                ? $"存入中… 已发 {this.jobSent} 次指令" + (this.jobSkipped > 0 ? $"，跳过 {this.jobSkipped}" : "")
                : $"取出中… 已发 {this.jobSent} 次指令" + (this.jobSkipped > 0 ? $"，跳过 {this.jobSkipped}" : "");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[装修助手] 搬运异常，已停止");
            this.StopJob("出错停下了。");
        }
    }

    /// <summary>找下一件要搬的东西（跳过已经失败的）。</summary>
    private unsafe bool TryFindNext(out int container, out int slot, out uint itemId)
    {
        container = -1;
        slot = -1;
        itemId = 0;

        var mgr = InventoryManager.Instance();
        if (mgr == null) return false;

        var types = this.job == MoveKind.Store ? BagContainers : StoreroomContainers;
        foreach (var type in types)
        {
            var cont = mgr->GetInventoryContainer(type);
            if (cont == null || !cont->IsLoaded) continue;

            for (var i = 0; i < cont->Size; i++)
            {
                var s = cont->GetInventorySlot(i);
                if (s == null || s->ItemId == 0) continue;
                if (this.jobTried.Contains(((int)type, i))) continue;

                // 存入时只挑家具/庭具；取出时仓库里都是家具/庭具，不用筛
                if (this.job == MoveKind.Store && !this.MatchesFilter(s->ItemId)) continue;

                container = (int)type;
                slot = i;
                itemId = s->ItemId;
                return true;
            }
        }
        return false;
    }
}

/// <summary>装修助手的配置（宿主自动持久化）。</summary>
public sealed class LKami9HousingHelperConfig
{
    /// <summary>搬家具（室内家具）。</summary>
    public bool IncludeFurniture { get; set; } = true;

    /// <summary>搬庭具（院子里的）。</summary>
    public bool IncludeYard { get; set; } = true;

    /// <summary>每件之间的间隔（毫秒），太快游戏可能处理不过来。</summary>
    public int IntervalMs { get; set; } = 300;
}
