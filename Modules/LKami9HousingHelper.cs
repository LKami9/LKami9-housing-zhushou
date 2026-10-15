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
    private int jobStartCount;                             // 开工时源侧容器里的全部件数
    private int jobStartMatch;                             // 开工时源侧「能搬」的件数
    private int jobSent;
    private int jobSkipped;
    private int jobDoneFrames;                             // 连续多少轮「源侧一件都没少」
    private int jobDoneBaseline;
    private string status = "空闲。先站到自己家里，再点下面两个按钮。";

    private readonly HashSet<(int Container, int Slot)> jobTried = [];
    private HashSet<uint>? furnitureIds;                   // 家具（HousingFurniture）
    private HashSet<uint>? yardIds;                        // 庭具（HousingYardObject）
    private bool frameworkHooked;

    // 上一次现场快照的缓存（诊断用，避免每帧重复读原生结构）
    private string cachedStatusDetail = "";
    private DateTime statusDetailAt = DateTime.MinValue;

    /// <summary>一次「数容器 + 生成明细」的结果。</summary>
    private readonly record struct ContainerCount(int Total, string Detail);

    /// <summary>现场读到的房屋信息（全部来自原生结构，零副作用）。</summary>
    private readonly record struct HouseInfo(
        bool OwnTerritory,      // 是否处在「自己家/自己院子/自己部队工坊」区域
        ulong HouseId,          // 当前区域的房屋 ID（0 = 没在任何房屋区域）
        string UnitText,        // 房屋类型 / 单元号
        string PermissionText,  // HasHousePermissions / IsInside / IsOutside 的诊断
        string Text)            // 给界面显示的一句话
    {
        public uint HighId => (uint)(this.HouseId >> 32);
        public uint LowId  => (uint)this.HouseId;
        public string Unit => this.UnitText;
    }

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

        var house = GetHouseInfo();
        if (house.OwnTerritory) ImGui.TextColored(OmniTheme.Tokens.Success, house.Text);
        else ImGui.TextColored(OmniTheme.Tokens.Warning, house.Text);

        var bagFur = this.CountInContainers(BagContainers, this.furnitureIds);
        var bagYard = this.CountInContainers(BagContainers, this.yardIds);
        var (storeAll, storeDetail) = this.CountWithDetail(StoreroomContainers, null);
        var storeReady = this.AnyContainerLoaded(StoreroomContainers);

        ImGui.TextDisabled($"背包：家具 {bagFur} 件 / 庭具 {bagYard} 件");
        if (storeReady)
        {
            ImGui.TextDisabled($"仓库：共 {storeAll} 件");
            ImGui.TextDisabled($"仓库明细：{storeDetail}");
        }
        else
        {
            ImGui.TextColored(OmniTheme.Tokens.Warning,
                "仓库：没读到数据 —— 请先在自己家/院子里打开「家具/庭具仓库」窗口");
        }

        if (this.furnitureIds is { Count: 0 } && this.yardIds is { Count: 0 })
            ImGui.TextColored(OmniTheme.Tokens.Warning, "家具/庭具数据表读取失败（看 /xllog）");

        var detail = this.BuildStatusDiagnostics();
        ImGui.TextDisabled("诊断：" + detail);
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(this.cachedStatusDetail))
            ImGui.SetTooltip(this.cachedStatusDetail);

        ImGui.TextDisabled("用法：站到自己家（或院子）里 → 打开「家具/庭具仓库」→ 点上面两个按钮。");
    }

    /// <summary>
    /// 现场快照（最多每 2 秒重算一次）：把两个前置条件的真实数值摆出来，
    /// 这样「点了没反应」时一眼能看出卡在哪一条（日志里同样会打一份）。
    /// </summary>
    private string BuildStatusDiagnostics()
    {
        if (DateTime.UtcNow - this.statusDetailAt > TimeSpan.FromSeconds(2))
        {
            this.statusDetailAt = DateTime.UtcNow;
            this.cachedStatusDetail = this.BuildStatusDetail();
        }
        return this.cachedStatusDetail;
    }

    private string BuildStatusDetail()
    {
        var bag = this.CountInContainers(BagContainers, null);
        var toStore = this.CountInContainers(BagContainers, this.furnitureIds)
                    + this.CountInContainers(BagContainers, this.yardIds);
        var (store, storeDetail) = this.CountWithDetail(StoreroomContainers, null);
        var house = GetHouseInfo();

        return string.Join(" | ",
            $"家具表 {this.furnitureIds?.Count ?? -1} 种",
            $"庭具表 {this.yardIds?.Count ?? -1} 种",
            $"勾选 家具={this.config.IncludeFurniture} 庭具={this.config.IncludeYard}",
            $"本体容器 2/3={this.CountInContainers(IntermediateContainers, null)}",
            $"背包 {bag} 件（其中家具/庭具 {toStore} 件）",
            $"仓库 {store} 件（{storeDetail}）",
            $"在自家区域={house.OwnTerritory} 房屋ID={house.HighId:X8}:{house.LowId:X8} 单元={house.Unit}",
            $"权限诊断 {house.PermissionText}");
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

    /// <summary>身上的容器：只为了诊断「背包那一栏到底读没读到」。</summary>
    private static readonly InventoryType[] IntermediateContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory4,
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
        => this.CountWithDetail(types, filter).Total;

    /// <summary>数容器里有多少件（可选道具过滤），同时生成「容器号=件数」明细，供界面/日志定位。</summary>
    private unsafe (int Total, string Detail) CountWithDetail(InventoryType[] types, HashSet<uint>? filter)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return (0, "InventoryManager 没读到");

        var n = 0;
        var parts = new List<string>();
        foreach (var type in types)
        {
            var cont = mgr->GetInventoryContainer(type);
            if (cont == null)
            {
                parts.Add($"{(int)type}=无");
                continue;
            }
            if (!cont->IsLoaded)
            {
                parts.Add($"{(int)type}=未载入");
                continue;
            }

            var here = 0;
            for (var i = 0; i < cont->Size; i++)
            {
                var slot = cont->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (filter != null && !filter.Contains(slot->ItemId)) continue;
                here++;
            }
            n += here;
            parts.Add($"{(int)type}={here}");
        }

        return (n, string.Join(" ", parts));
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

    /// <summary>
    /// 现场房屋信息（纯读原生结构，没有任何会被游戏拒绝的调用）。
    ///
    /// 【为什么这样判】游戏侧真正决定「这条搬运指令发不发」的是
    /// HousingManager 的 IndoorTerritory / OutdoorTerritory / WorkshopTerritory 三个指针
    /// （三个都为空 = 不在任何房屋区域，指令会被丢弃）；把它们的 HouseId 读出来，
    /// 就是最可靠的「在不在自己家」判据，而且不依赖任何 AOB 签名。
    /// HasHousePermissions / IsInside / IsOutside 走的是 MemberFunctionPointers（签名地址），
    /// 签名过期会抛异常 —— 所以只当诊断信息，不让它挡流程。
    /// </summary>
    private static unsafe HouseInfo GetHouseInfo()
    {
        try
        {
            var hm = HousingManager.Instance();
            if (hm == null) return new HouseInfo(false, 0, "读不到", "HousingManager 没读到", "房屋：没读到数据");

            var indoor = hm->IndoorTerritory;
            var outdoor = hm->OutdoorTerritory;
            var workshop = hm->WorkshopTerritory;

            HouseId id = default;
            var zone = "无";
            var unitText = "-";
            if (indoor != null)
            {
                id = indoor->HouseId;
                zone = "室内";
                unitText = DescribeUnit(id);
            }
            else if (outdoor != null)
            {
                id = outdoor->HouseId;
                zone = "院子";
                unitText = DescribeUnit(id);
            }
            else if (workshop != null)
            {
                id = workshop->HouseId;
                zone = "工坊";
                unitText = "部队工坊";
            }

            var raw = id.Id;
            var own = raw != 0;
            var perms = DescribePermissions(hm);
            var text = own
                ? $"房屋：在自家区域（{zone}） {unitText} 房屋ID {id.TerritoryTypeId}区/{id.WardIndex}号/{unitText}"
                : "房屋：没读到房屋区域 —— 先回自己家/院子里再点（游戏会拒绝搬运）";
            return new HouseInfo(own, raw, unitText, perms, text);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[装修助手] 读房屋信息失败");
            return new HouseInfo(false, 0, "读失败", "读取异常（看 /xllog）", "房屋：读取失败（看 /xllog）");
        }
    }

    private static unsafe string DescribeUnit(HouseId id)
    {
        var unit = id.Unit;
        return unit.IsApartment
            ? $"公寓 {unit.ApartmentDivision} 号"
            : $"地块 {unit.PlotIndex} 号";
    }

    /// <summary>把三个「签名型」成员函数的结果打包成诊断字符串；任何一个抛异常都不会影响主流程。</summary>
    private static unsafe string DescribePermissions(HousingManager* hm)
    {
        var parts = new List<string>(3);
        parts.Add(TryMember(() => hm->HasHousePermissions() ? "权限=True" : "权限=False"));
        parts.Add(TryMember(() => hm->IsInside() ? "室内=True" : "室内=False"));
        parts.Add(TryMember(() => hm->IsOutside() ? "院内=True" : "院内=False"));
        return string.Join(" ", parts);
    }

    private static string TryMember(Func<string> probe)
    {
        try { return probe(); }
        catch (Exception ex) { return "读取异常(" + ex.GetType().Name + ")"; }
    }

    /// <summary>是否在自家区域（见 GetHouseInfo 的说明：按原生房屋指针判，不依赖 AOB 签名）。</summary>
    private static HouseInfo CheckHouse() => GetHouseInfo();

    // ══════════════════════════════════════════════════════════
    //  搬运流程
    // ══════════════════════════════════════════════════════════

    private void StartJob(MoveKind kind)
    {
        var what = kind == MoveKind.Store ? "存入" : "取出";

        // ① 建索引（失败也照样往下走，日志里能看到数量）
        this.LoadFurnitureIndexIfNeeded();
        var idxFur = this.furnitureIds?.Count ?? -1;
        var idxYard = this.yardIds?.Count ?? -1;
        // 这三个结果一定要打日志 —— 之前守卫失败是静默的，害得排查全靠猜
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置①索引：家具 {idxFur} 种 / 庭具 {idxYard} 种");

        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置②范围：家具={this.config.IncludeFurniture} 庭具={this.config.IncludeYard} 间隔={this.config.IntervalMs}ms");

        var house = GetHouseInfo();
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置③房屋：在自家区域={house.OwnTerritory} 房屋ID={house.HighId:X8}:{house.LowId:X8} 单元={house.Unit} {house.PermissionText}");

        var srcTypes = kind == MoveKind.Store ? BagContainers : StoreroomContainers;
        var (srcAll, srcDetail) = this.CountWithDetail(srcTypes, null);
        var srcMatch = kind == MoveKind.Store
            ? this.CountInContainers(BagContainers, this.furnitureIds) + this.CountInContainers(BagContainers, this.yardIds)
            : srcAll;
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置④容器：{(kind == MoveKind.Store ? "背包" : "仓库")} {srcAll} 件（其中要搬 {srcMatch} 件）明细 {srcDetail}");

        // 守卫 1：索引空
        if (idxFur <= 0 && idxYard <= 0)
        {
            DalamudServices.PluginLog.Information("[装修助手] 中止：家具/庭具数据表为空");
            this.status = "家具/庭具数据表没读到，先别搬。";
            return;
        }

        // 守卫 2：范围全没勾
        if (!this.config.IncludeFurniture && !this.config.IncludeYard)
        {
            DalamudServices.PluginLog.Information("[装修助手] 中止：家具/庭具两个都没勾");
            this.status = "「家具」「庭具」两个都没勾，没得搬。";
            return;
        }

        // 守卫 3：不在自家区域
        // （判据 = 原生房屋指针有值。这里放行后即使真的没权限，游戏也只是不收指令，
        //   由下面的「连续 3 次搬不动 → 跳过 / 连 10 件 → 停」兜住，不会静默卡死。）
        if (!house.OwnTerritory)
        {
            DalamudServices.PluginLog.Information("[装修助手] 中止：不在自家房屋区域（房屋ID 为 0）");
            this.status = "看起来不在自己家/院子里 —— 先回去再点。";
            return;
        }

        // 守卫 4：源侧根本没有要搬的东西
        if (srcMatch <= 0)
        {
            DalamudServices.PluginLog.Information($"[装修助手] 中止：{(kind == MoveKind.Store ? "背包" : "仓库")}里没有要搬的家具/庭具（0 件）");
            this.status = kind == MoveKind.Store
                ? "背包里没找到家具/庭具（或没勾对应的范围）。"
                : "仓库里没读到家具/庭具 —— 先打开「家具/庭具仓库」窗口。";
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

        this.jobStartCount = srcAll;
        this.jobStartMatch = srcMatch;
        this.jobDoneFrames = 0;
        this.jobDoneBaseline = srcAll;

        this.status = kind == MoveKind.Store
            ? $"开始存入：共 {srcMatch} 件家具/庭具…"
            : $"开始取出：仓库里共 {srcMatch} 件…";
        DalamudServices.PluginLog.Information(
            $"[装修助手] 开始{what}：源侧 {srcAll} 件（要搬 {srcMatch} 件），每件间隔 {this.jobIntervalFrames} 帧");
    }

    private void StopJob(string why)
    {
        var kind = this.job;
        this.job = MoveKind.None;
        this.jobCurrent = (-1, -1);

        var srcTypes = kind == MoveKind.Store ? BagContainers : StoreroomContainers;
        var (left, leftDetail) = this.CountWithDetail(srcTypes, null);
        var moved = Math.Max(0, this.jobStartCount - left);

        this.status = $"{why} 已搬 {moved} 件" +
                      (this.jobSkipped > 0 ? $"，跳过 {this.jobSkipped} 件（游戏没收）" : "") +
                      $"，源侧还剩 {left} 件。";
        DalamudServices.PluginLog.Information(
            $"[装修助手] {why} 已搬 {moved} 件，共发 {this.jobSent} 次指令，跳过 {this.jobSkipped}，" +
            $"源侧 {this.jobStartCount}→{left} 件（其中要搬 {this.jobStartMatch} 件）明细 {leftDetail}");
    }

    private unsafe void OnFrameworkTick(Dalamud.Plugin.Services.IFramework framework)
    {
        if (this.job == MoveKind.None) return;

        try
        {
            if (++this.jobWait < this.jobIntervalFrames) return;
            this.jobWait = 0;

            // 源侧一直没变少（游戏压根没收指令）→ 等够 8 轮就停，不然会发几千条无效指令
            var loopDetail = "";
            if (this.job != MoveKind.None && this.jobSent > 0)
            {
                var srcTypes = this.job == MoveKind.Store ? BagContainers : StoreroomContainers;
                var now = this.CountInContainers(srcTypes, null);
                if (now < this.jobDoneBaseline)
                {
                    this.jobDoneBaseline = now;
                    this.jobDoneFrames = 0;
                }
                else if (++this.jobDoneFrames >= 8)
                {
                    loopDetail = "（源侧一件都没少：指令可能被游戏拒了 —— 仓库满 / 没打开仓库窗口 / 不是自己的房子）";
                }
            }

            if (!this.TryFindNext(out var container, out var slot, out var itemId))
            {
                this.StopJob(string.IsNullOrEmpty(loopDetail) ? "搬完了。" : "停手了。" + loopDetail);
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
                {
                    this.StopJob("连续 10 件都搬不动 —— 可能是仓库已满 / 没打开仓库窗口 / 不在自己家。" + loopDetail);
                    return;
                }

                if (!string.IsNullOrEmpty(loopDetail))
                {
                    this.StopJob(loopDetail);
                    return;
                }
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

                // 两边都按勾选的范围筛：存入时筛背包，取出时筛仓库
                // （仓库里只可能有家具/庭具，多筛一次不影响结果，但能防住「仓库页里混进别的东西」）
                if (!this.MatchesFilter(s->ItemId)) continue;

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
