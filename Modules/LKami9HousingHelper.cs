using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

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
/// 四个动作（全部走 OmenTools 的公开指令 API，不写内存）：
///   · 批量存入     ：背包 家具/庭具 → 房屋仓库          ❌ 见下方「未打通」
///   · 批量取出     ：房屋仓库 家具/庭具 → 背包          HousingCommand.Restore(仓库, 槽, toStoreRoom:false)   ★实测有效
///   · 收回布置→背包：屋里/院里已摆放的 → 背包           HousingCommand.Restore(已布置, 槽, toStoreRoom:false) ★实测有效
///   · 收回布置→仓库：屋里/院里已摆放的 → 房屋仓库       HousingCommand.Restore(已布置, 槽, toStoreRoom:true)  ★实测有效
///
/// 【「背包 → 仓库」为什么没打通】这条方向游戏没有对外接口，实测过两条路都被服务器拒：
///   ① ExecuteCommandFlag 那一族：`StoreFurniture`(1112)、`RestoreFurniture`(1113+移仓标记)、
///      1112 带「目标仓库页」—— 三种各 3 次、共 9 条，源侧一件不少。
///   ② `InventoryManager.MoveItemSlot(背包, 槽, 27001, 空槽, false)` —— **这是个陷阱**：
///      调用后源侧确实变少、界面看着像成功，但家具仓库里没有、游戏也没有收纳提示，
///      **重新登录后道具又回到背包**（客户端本地搬了一下，服务器没收、随后回滚）。
///      → 所以本模块已彻底移除 MoveItemSlot 用法；任何「本地搬运」都不能当成功，必须核对目标容器件数。
///   现阶段请手动收纳，或先把家具布置到屋里再用「收回已布置家具 → 仓库」（这条实测有效）。
///
/// 容器号来自 FFXIVClientStructs 的 InventoryType（与 DailyRoutines 的「自动取回家具」同源）：
///   背包        Inventory1..4                  （0..3）
///   室内已布置  HousingInteriorPlacedItems1..12（25003..25014）
///   院内已布置  HousingExteriorPlacedItems     （25001）
///   室内仓库    HousingInteriorStoreroom1..11  （27001..27011）
///   院内仓库    HousingExteriorStoreroom(27000) / HousingExteriorStoreroom2(27200)
///
/// 前置条件（游戏规则，绕不过）：
///   · 必须在自己家 / 院子里（HousingManager 的房屋指针有值），否则游戏静默丢弃搬运指令
///   · 家具界面要开着（DailyRoutines 的同功能模块就是把按钮挂在 HousingGoods 窗口上、
///     要求玩家先打开家具界面再点开始）；本模块的「自动载入」只是尽力而为，
///     载入不成功也照样发搬运指令，由「源侧一件都没少」兜住，不会静默卡死
/// </summary>
public sealed class LKami9HousingHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9装修助手",
        Description = "批量搬家具：仓库 → 背包、屋里/院里已摆放的 → 背包/仓库（三个功能实测可用）。背包 → 仓库 游戏没有对外接口，只能手动（本地搬运会被服务器回滚，已移除）。",
        Category    = ModuleCategory.Automation,
        Author      = "LKami9",
        Commands    =
        [
            new("批量存入（背包 → 仓库）", "/omni LKami9HousingHelper store"),
            new("批量取出（仓库 → 背包）", "/omni LKami9HousingHelper take"),
            new("收回已布置家具 → 背包", "/omni LKami9HousingHelper pick"),
            new("收回已布置家具 → 仓库", "/omni LKami9HousingHelper pack"),
        ],
    };

    public override bool HasSettings => true;

    /// <summary>代码版本（只用于日志：一眼看出游戏里编的是哪一版）。</summary>
    private const string ModuleVersion = "1.3.1";

    // 宿主按「以 Config 结尾的成员」绑定已保存的配置；不能是 readonly
    private LKami9HousingHelperConfig config = new();

    // ── 搬运状态 ──
    private enum MoveKind { None, Store, StoreAsStore, StoreAsDest, TakeOut, RetrieveToBag, RetrieveToStore }

    private MoveKind job = MoveKind.None;
    private int storeLadderStep;                           // 「存入」当前在第几种发法
    private int storeLadderPass;                           // 「存入」整套发法跑到第几轮
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
    private int jobSrcRefresh;                             // 状态行的源侧件数刷新计数
    private int jobSrcNow;                                 // 状态行用的源侧当前件数
    private int prepPhase;                                 // 0=不用准备 1=等几帧 2=发指令+等载入 3=仓库存档就绪 4=等玩家手动开窗
    private int prepWait;
    private int prepStepsDone;                             // 准备阶段已经发过几条载入指令
    private string status = "空闲。先站到自己家里，再点下面的按钮。";

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
            DalamudServices.PluginLog.Information($"[装修助手] 模块已加载，代码版本 v{ModuleVersion}");
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
            case "pick":
            case "收回背包":
                this.StartJob(MoveKind.RetrieveToBag);
                return true;
            case "pack":
            case "收回仓库":
                this.StartJob(MoveKind.RetrieveToStore);
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
        ImGui.TextDisabled("（家具：室内家具；庭具：院子里的）");

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
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("游戏没有对外暴露「背包 → 仓库」接口，实测无效；请手动收纳，或用「收回已布置家具 → 仓库」");
            ImGui.SameLine();
            if (ImGui.Button("批量取出（仓库 → 背包）")) this.StartJob(MoveKind.TakeOut);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("房屋仓库里的 家具/庭具 全部取回背包");

            if (ImGui.Button("收回已布置家具 → 背包")) this.StartJob(MoveKind.RetrieveToBag);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("把屋里/院里已经摆放的 家具/庭具 全部收回背包（源：25001 / 25003~25014）");
            ImGui.SameLine();
            if (ImGui.Button("收回已布置家具 → 仓库")) this.StartJob(MoveKind.RetrieveToStore);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("把屋里/院里已经摆放的 家具/庭具 全部收回房屋仓库（源：25001 / 25003~25014）");
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
        var (placedAll, placedDetail) = this.CountWithDetail(PlacedContainers, null);
        var placedReady = this.AnyContainerLoaded(PlacedContainers);

        ImGui.TextDisabled($"背包：家具 {bagFur} 件 / 庭具 {bagYard} 件");
        if (storeReady)
        {
            ImGui.TextDisabled($"仓库：共 {storeAll} 件");
            ImGui.TextDisabled($"仓库明细：{storeDetail}");
        }
        else
        {
            ImGui.TextColored(OmniTheme.Tokens.Warning,
                "仓库：没读到数据 —— 请先打开家具界面（家具/庭具窗口）再点");
        }

        if (placedReady)
            ImGui.TextDisabled($"已布置：共 {placedAll} 件　明细：{placedDetail}");
        else
            ImGui.TextColored(OmniTheme.Tokens.Warning, "已布置：没读到数据 —— 站在自己家/院子里时应该能读到");

        ImGui.TextDisabled($"家具窗口（HousingGoods）：{DescribeStoreroomWindow()}");

        if (this.furnitureIds is { Count: 0 } && this.yardIds is { Count: 0 })
            ImGui.TextColored(OmniTheme.Tokens.Warning, "家具/庭具数据表读取失败（看 /xllog）");

        var detail = this.BuildStatusDiagnostics();
        ImGui.TextDisabled("诊断：" + detail);
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(this.cachedStatusDetail))
            ImGui.SetTooltip(this.cachedStatusDetail);

        ImGui.TextDisabled("用法：站到自己家（或院子）里 → 打开家具界面（家具/庭具窗口）→ 点上面四个按钮之一。");
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
        var (placed, placedDetail) = this.CountWithDetail(PlacedContainers, null);
        var house = GetHouseInfo();

        return string.Join(" | ",
            $"家具表 {this.furnitureIds?.Count ?? -1} 种",
            $"庭具表 {this.yardIds?.Count ?? -1} 种",
            $"勾选 家具:{(this.config.IncludeFurniture ? "开" : "关")} 庭具:{(this.config.IncludeYard ? "开" : "关")}",
            $"本体容器 2/3:{this.CountInContainers(IntermediateContainers, null)}",
            $"背包 {bag} 件（其中家具/庭具 {toStore} 件）",
            $"仓库 {store} 件（{storeDetail}）",
            $"已布置 {placed} 件（{placedDetail}）",
            $"家具窗口:{DescribeStoreroomWindow()}",
            $"在自家区域:{(house.OwnTerritory ? "是" : "否")} 房屋ID {house.HighId:X8}:{house.LowId:X8} 单元 {house.Unit}",
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

    /// <summary>已摆放的家具/庭具（室内 25003..25014 / 院内 25001）——「收回布置」的源。</summary>
    private static readonly InventoryType[] PlacedContainers =
    [
        InventoryType.HousingExteriorPlacedItems,        // 25001 院子里已摆放的庭具
        InventoryType.HousingInteriorPlacedItems1, InventoryType.HousingInteriorPlacedItems2,
        InventoryType.HousingInteriorPlacedItems3, InventoryType.HousingInteriorPlacedItems4,
        InventoryType.HousingInteriorPlacedItems5, InventoryType.HousingInteriorPlacedItems6,
        InventoryType.HousingInteriorPlacedItems7, InventoryType.HousingInteriorPlacedItems8,
        InventoryType.HousingInteriorPlacedItems9, InventoryType.HousingInteriorPlacedItems10,
        InventoryType.HousingInteriorPlacedItems11, InventoryType.HousingInteriorPlacedItems12,
    ];

    /// <summary>这个动作的「源侧容器」是哪一批。</summary>
    private static InventoryType[] SourceContainers(MoveKind kind) => kind switch
    {
        MoveKind.Store or MoveKind.StoreAsStore or MoveKind.StoreAsDest
            or MoveKind.StoreAsDest => BagContainers,
        MoveKind.RetrieveToBag or MoveKind.RetrieveToStore => PlacedContainers,
        _ => StoreroomContainers,
    };

    /// <summary>源侧的名字（日志/界面用）。</summary>
    private static string SourceNoun(MoveKind kind) => kind switch
    {
        MoveKind.Store or MoveKind.StoreAsStore or MoveKind.StoreAsDest
            or MoveKind.StoreAsDest => "背包",
        MoveKind.RetrieveToBag or MoveKind.RetrieveToStore => "已布置",
        _ => "仓库",
    };

    /// <summary>动作名（日志/界面用）。</summary>
    private static string ActionName(MoveKind kind) => kind switch
    {
        MoveKind.Store or MoveKind.StoreAsStore or MoveKind.StoreAsDest
            or MoveKind.StoreAsDest => "存入（背包 → 仓库）",
        MoveKind.RetrieveToBag => "收回已布置 → 背包",
        MoveKind.RetrieveToStore => "收回已布置 → 仓库",
        _ => "取出（仓库 → 背包）",
    };

    /// <summary>
    /// 仓储类容器的「件数明细」，用来做**服务器侧核对**：
    /// 客户端本地搬运（MoveItemSlot）会让源侧件数变少，但服务器不收、重登就还原，
    /// 所以「源侧变少」不足以下结论，必须同时看目标容器有没有真的多出来。
    /// </summary>
    private unsafe string StoreroomDetail() => this.CountWithDetail(StoreroomContainers, null).Detail;

    /// <summary>存入失败换招时用：把「仓库页」当第 3 参传的目标容器（室内 27001 / 站在院里时 27000）。</summary>
    private static unsafe InventoryType StoreroomDest()
    {
        var hm = HousingManager.Instance();
        if (hm != null && hm->OutdoorTerritory != null && hm->IndoorTerritory == null)
            return InventoryType.HousingExteriorStoreroom;
        return InventoryType.HousingInteriorStoreroom1;
    }

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
                parts.Add($"{(int)type}:无");
                continue;
            }
            if (!cont->IsLoaded)
            {
                parts.Add($"{(int)type}:未载入");
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
            parts.Add($"{(int)type}:{here}");
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
        parts.Add(TryMember(() => hm->HasHousePermissions() ? "权限:有" : "权限:无"));
        parts.Add(TryMember(() => hm->IsInside() ? "室内:是" : "室内:否"));
        parts.Add(TryMember(() => hm->IsOutside() ? "院内:是" : "院内:否"));
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
        // 「存入」从「实测有效的那种发法」开始；后面一件都没搬动就按 StoreLadder 依次换招。
        if (IsStoreKind(kind))
        {
            this.storeLadderStep = 0;
            this.storeLadderPass = 0;
            kind = StoreLadder[0];
        }

        var what = ActionName(kind);
        DalamudServices.PluginLog.Information($"[装修助手] ===== 开始{what}（代码版本 v{ModuleVersion}）=====");
        if (IsStoreKind(kind))
            DalamudServices.PluginLog.Information($"[装修助手] 存入发法：{StoreMethodName(kind)}");

        // ① 建索引（失败也照样往下走，日志里能看到数量）
        this.LoadFurnitureIndexIfNeeded();
        var idxFur = this.furnitureIds?.Count ?? -1;
        var idxYard = this.yardIds?.Count ?? -1;
        // 这三个结果一定要打日志 —— 之前守卫失败是静默的，害得排查全靠猜
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置①索引：家具 {idxFur} 种 / 庭具 {idxYard} 种");

        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置②范围：家具:{(this.config.IncludeFurniture ? "开" : "关")} 庭具:{(this.config.IncludeYard ? "开" : "关")} 间隔 {this.config.IntervalMs}ms");

        var house = GetHouseInfo();
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置③房屋：在自家区域:{(house.OwnTerritory ? "是" : "否")} 房屋ID {house.HighId:X8}:{house.LowId:X8} 单元 {house.Unit} {house.PermissionText}");

        var srcTypes = SourceContainers(kind);
        var (srcAll, srcDetail) = this.CountWithDetail(srcTypes, null);
        var srcMatch = kind is MoveKind.Store or MoveKind.StoreAsStore
            ? this.CountInContainers(BagContainers, this.furnitureIds) + this.CountInContainers(BagContainers, this.yardIds)
            : srcAll;
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置④容器：{SourceNoun(kind)} {srcAll} 件（其中要搬 {srcMatch} 件）明细 {srcDetail}");

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
            DalamudServices.PluginLog.Information($"[装修助手] 中止：{SourceNoun(kind)}里没有要搬的家具/庭具（0 件）");
            this.status = kind is MoveKind.Store or MoveKind.StoreAsStore
                ? "背包里没找到家具/庭具（或没勾对应的范围）。"
                : $"没读到要搬的东西（{SourceNoun(kind)}）—— 盖上家具界面（家具/庭具窗口）再点一次。";
            return;
        }

        var storeReady = this.AnyContainerLoaded(StoreroomContainers);
        var placedReady = this.AnyContainerLoaded(PlacedContainers);
        DalamudServices.PluginLog.Information(
            $"[装修助手] 前置⑤容器状态：仓库已载入:{(storeReady ? "是" : "否")} 已布置已载入:{(placedReady ? "是" : "否")} 家具窗口:{DescribeStoreroomWindow()}"
            + (storeReady || placedReady ? "" : "（都没载入 → 先尽力发 3 条载入指令，之后照常尝试搬运）"));

        this.job = kind;
        this.jobWait = 0;
        this.jobCurrent = (-1, -1);
        this.jobCurrentTries = 0;
        this.jobSkipStreak = 0;
        this.jobSent = 0;
        this.jobSkipped = 0;
        this.jobSrcRefresh = 0;
        this.jobTried.Clear();

        this.jobStartCount = srcAll;
        this.jobStartMatch = srcMatch;
        this.jobSrcNow = srcAll;
        this.jobDoneFrames = 0;
        this.jobDoneBaseline = srcAll;

        if (storeReady || placedReady || kind is MoveKind.Store or MoveKind.StoreAsStore)
        {
            // 源侧数据已经在手里（背包天然可读）→ 直接开工
            this.prepPhase = 3;
            this.prepWait = 0;
            this.status = $"开始{what}：源侧 {srcAll} 件（要搬 {srcMatch} 件）…";
            DalamudServices.PluginLog.Information(
                $"[装修助手] 开始{what}：源侧 {srcAll} 件（要搬 {srcMatch} 件），每件间隔 {this.jobIntervalFrames} 帧");
            return;
        }

        // 源侧（仓库 / 已布置）没读到数据 → 先尽力发载入指令等一会儿；
        // 之后无论载入成功与否都照常往下走（由「源侧一件都没少」兜住，不再直接停手）。
        this.prepPhase = 1;
        this.prepWait = 0;
        this.status = "准备中：先试着把家具界面里的仓库数据叫出来…";
        DalamudServices.PluginLog.Information($"[装修助手] 准备{what}：先尝试自动载入（载入失败也会继续尝试搬运）");
    }

    /// <summary>
    /// 开工前的「仓库载入」准备：逐帧推进（同一帧连发多条指令游戏只处理一条）。
    /// 只是尽力而为 —— 发 3 条指令、每步等 1 秒，之后无论载入成功与否都放行；
    /// 真正判断有没有搬动靠「源侧件数有没有变少」。
    /// 返回 true = 这帧还在准备，搬运逻辑不要动。
    /// </summary>
    private unsafe bool PrepTick()
    {
        if (this.prepPhase == 3) return false;

        // phase 1：先等 6 帧（让「准备中」的状态行显示出来），再发第 1 条指令
        if (this.prepPhase == 1)
        {
            if (this.prepWait++ < 6) return true;
            this.prepWait = 0;

            var house = GetHouseInfo();
            if (house.HighId == 0 && house.LowId == 0)
            {
                DalamudServices.PluginLog.Information("[装修助手] 准备跳过：不在自家房屋区域，载入指令发不出去");
                this.PrepGiveUp("（不在自家房屋区域，先回自己家/院子里）");
                return true;
            }

            this.prepPhase = 2;
            this.PrepSendStep(0, house);
            return true;
        }

        // phase 2：每帧读一次原生容器（只读，无副作用），看源侧有没有载入
        if (this.AnyContainerLoaded(SourceContainers(this.job)))
        {
            this.PrepDone();
            return false;
        }

        if (this.prepWait++ < PrepStepWaitFrames) return true;

        if (this.prepStepsDone < PrepStepNames.Length)
        {
            this.PrepSendStep(this.prepStepsDone, GetHouseInfo());
            return true;
        }

        // 3 条都发过还是没读到 → 放行（照常发搬运指令，靠「源侧没变少」判断并停手）
        DalamudServices.PluginLog.Information(
            $"[装修助手] 自动载入没生效（发过 {PrepStepNames.Length} 条指令，家具窗口:{DescribeStoreroomWindow()}）"
            + " → 照常尝试搬运；若源侧一直不变少，说明游戏侧还没被满足（需要开着家具界面 / 换室内外位置）");
        this.PrepGiveUp("");
        return true;
    }

    /// <summary>准备阶段全部没成功 → 不再拦着，直接放行开工（附一句人话原因）。</summary>
    private void PrepGiveUp(string reason)
    {
        this.prepPhase = 3;
        this.prepWait = 0;
        this.jobDoneFrames = 0;
        this.jobDoneBaseline = this.CountInContainers(SourceContainers(this.job), null);
        this.status = $"直接开始{ActionName(this.job)}（没自动载入到仓库数据{reason}），源侧一件没变少就会自动停。";
    }

    /// <summary>每条载入/请求指令给多少帧（60 帧 ≈ 1 秒）。</summary>
    private const int PrepStepWaitFrames = 60;

    /// <summary>准备阶段依次尝试的载入/请求指令（日志里会说清是哪一步生效的）。</summary>
    private static readonly string[] PrepStepNames =
    [
        "请求已放置家具列表（RequestPlacedFurnitures(室内) 1121）",
        "载入室内家具仓库（LoadInteriorFurnish 1111）",
        "请求仓库状态（RequestStoreroomStatus 1151）",
    ];

    /// <summary>每条指令对应哪个动作：0=室内家具 1=院内庭具 2=请求仓库状态 3=请求已放置家具。</summary>
    private static readonly int[] PrepStepAction = [3, 0, 2];

    private unsafe void PrepSendStep(int index, HouseInfo house)
    {
        switch (PrepStepAction[index])
        {
            case 0:
                HousingCommand.LoadInteriorFurnish();
                break;
            case 1:
                HousingCommand.LoadExteriorFurnish();
                break;
            case 3:
                HousingCommand.RequestPlacedFurnitures(isIndoor: true);
                break;
            default:
                HousingCommand.RequestStoreroomStatus(house.HighId, house.LowId);
                break;
        }

        this.prepStepsDone = index + 1;
        this.prepWait = 0;

        var leftSec = (PrepStepNames.Length - index - 1) * PrepStepWaitFrames / 60f;
        DalamudServices.PluginLog.Information(
            $"[装修助手] 准备第 {index + 1}/{PrepStepNames.Length} 步：{PrepStepNames[index]}"
            + $"（房屋 {house.HighId:X8}:{house.LowId:X8}，家具窗口:{DescribeStoreroomWindow()}），等 1 秒");
        this.status = $"正在尝试载入仓库数据…（第 {index + 1}/{PrepStepNames.Length} 步，还剩约 {leftSec:0.#} 秒）";
    }

    /// <summary>看一眼「家具/庭具仓库」窗口（HousingGoods）什么状态 —— 只读，用来判断指令有没有把窗口叫出来。</summary>
    private static unsafe string DescribeStoreroomWindow()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return "读不到(AtkUnitManager)";

        var addon = manager->GetAddonByName("HousingGoods");
        if (addon == null) return "未打开";

        return addon->IsVisible ? "已打开" : "已创建未显示";
    }

    /// <summary>源侧数据载入成功 → 正式开工。</summary>
    private void PrepDone()
    {
        var srcTypes0 = SourceContainers(this.job);
        var (n, detail) = this.CountWithDetail(srcTypes0, null);
        DalamudServices.PluginLog.Information(
            $"[装修助手] 准备完成：{SourceNoun(this.job)}数据已载入（约在第 {this.prepStepsDone} 步之后），共 {n} 件，明细 {detail}");

        this.prepPhase = 3;
        this.prepWait = 0;
        this.jobDoneFrames = 0;

        var (again, _) = this.CountWithDetail(srcTypes0, null);
        this.jobStartCount = again;
        this.jobDoneBaseline = again;

        this.status = $"开始{ActionName(this.job)}：源侧共 {again} 件…";
        DalamudServices.PluginLog.Information(
            $"[装修助手] 开始{ActionName(this.job)}：源侧 {again} 件（要搬 {this.jobStartMatch} 件），每件间隔 {this.jobIntervalFrames} 帧");
    }

    /// <summary>是不是「存入」这一族（背包 → 仓库）。</summary>
    private static bool IsStoreKind(MoveKind kind) =>
        kind is MoveKind.Store or MoveKind.StoreAsStore or MoveKind.StoreAsDest;

    /// <summary>「存入」的 3 种发法（都是 ExecuteCommandFlag 那一族；目前实测**都不生效**）。</summary>
    private static readonly MoveKind[] StoreLadder =
    [
        MoveKind.Store,               // 指令 1112 StoreFurniture(背包, 槽)
        MoveKind.StoreAsStore,        // 指令 1113 RestoreFurniture(背包, 槽 + 0x10000)
        MoveKind.StoreAsDest,         // 指令 1112（第 3 参当目标仓库页）
    ];

    /// <summary>发法的名字（日志里用，能一眼看出这条日志是哪一招）。</summary>
    private static string StoreMethodName(MoveKind kind) => kind switch
    {
        MoveKind.Store => "指令 1112 StoreFurniture",
        MoveKind.StoreAsStore => "指令 1113 RestoreFurniture+移仓标记",
        MoveKind.StoreAsDest => "指令 1112（第 3 参当目标仓库页）",
        _ => kind.ToString(),
    };

    /// <summary>「存入」换招：清空重试状态并把同一批从头试一遍（含重新请求仓库上下文）。</summary>
    private void SwitchStoreVariant(MoveKind next, string log)
    {
        this.job = next;
        this.jobTried.Clear();
        this.jobCurrent = (-1, -1);
        this.jobCurrentTries = 0;
        this.jobSkipStreak = 0;
        this.jobSent = 0;
        this.jobDoneFrames = 0;
        this.jobDoneBaseline = this.CountInContainers(BagContainers, null);

        // 换招前把仓库上下文再请求一次（这两条指令只影响游戏侧的 UI 数据，不搬东西）
        var house = GetHouseInfo();
        HousingCommand.RequestPlacedFurnitures(isIndoor: true);
        HousingCommand.RequestStoreroomStatus(house.HighId, house.LowId);

        DalamudServices.PluginLog.Information("[装修助手] " + log);
        this.status = log + "…";
        if (IsStoreKind(next))
            DalamudServices.PluginLog.Information($"[装修助手] 换招前核对：背包 {this.CountInContainers(BagContainers, null)} 件，仓库 {this.StoreroomDetail()}");
    }

    private void StopJob(string why)
    {
        var kind = this.job;
        this.job = MoveKind.None;
        this.jobCurrent = (-1, -1);
        this.prepPhase = 0;
        this.prepWait = 0;

        var srcTypes = SourceContainers(kind);
        var (left, leftDetail) = this.CountWithDetail(srcTypes, null);
        var moved = Math.Max(0, this.jobStartCount - left);

        this.status = $"{why} 已搬 {moved} 件" +
                      (this.jobSkipped > 0 ? $"，跳过 {this.jobSkipped} 件（游戏没收）" : "") +
                      $"，源侧还剩 {left} 件。";
        DalamudServices.PluginLog.Information(
            $"[装修助手] {why} 已搬 {moved} 件，共发 {this.jobSent} 次指令，跳过 {this.jobSkipped}，" +
            $"源侧 {this.jobStartCount}→{left} 件（其中要搬 {this.jobStartMatch} 件）明细 {leftDetail}；" +
            $"目标容器核对 仓库 {this.StoreroomDetail()}");
    }

    private unsafe void OnFrameworkTick(Dalamud.Plugin.Services.IFramework framework)
    {
        if (this.job == MoveKind.None) return;

        try
        {
            // 开工前的「仓库载入」准备：每帧只发一条指令，载入好了才开始搬
            if (this.PrepTick()) return;

            if (++this.jobWait < this.jobIntervalFrames) return;
            this.jobWait = 0;

            // 源侧一直没变少（游戏压根没收指令）→ 等够 8 轮就停，不然会发几千条无效指令
            var loopDetail = "";
            if (this.job != MoveKind.None && this.jobSent > 0)
            {
                var srcTypes = SourceContainers(this.job);
                var now = this.CountInContainers(srcTypes, null);
                if (now < this.jobDoneBaseline)
                {
                    this.jobDoneBaseline = now;
                    this.jobDoneFrames = 0;
                }
                else if (++this.jobDoneFrames >= 8)
                {
                    loopDetail = "（源侧一件都没少：指令可能被游戏拒了 —— 家具界面没开 / 仓库满 / 不是自己的房子 / 物品类型不对）";
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
                // 「存入」专用：三种发法依次实测（这条指令到底认哪个参数，只有试了才知道）。
                // 只在「一件都没搬动」时换招，避免把单件失败误判成整条路走不通。
                var srcNow = this.CountInContainers(BagContainers, null);
                // 「存入」换招：一条都不动就换下一种发法；整套（5 种）试完再来一遍
                // （有的发法需要前面几条指令把游戏侧的上下文"热"起来才生效，所以留了两轮）。
                var inStoreFamily = IsStoreKind(this.job);
                var nothingMoved = inStoreFamily && srcNow >= this.jobStartCount;

                if (nothingMoved)
                {
                    this.storeLadderStep++;
                    if (this.storeLadderStep < StoreLadder.Length)
                    {
                        this.SwitchStoreVariant(StoreLadder[this.storeLadderStep],
                            $"存入：{StoreMethodName(this.job)} 一件都没搬动 → 换 {StoreMethodName(StoreLadder[this.storeLadderStep])} 重试");
                        return;
                    }

                    this.storeLadderPass++;
                    if (this.storeLadderPass < 2)
                    {
                        this.storeLadderStep = 0;
                        this.SwitchStoreVariant(StoreLadder[0],
                            $"存入：{StoreLadder.Length} 种发法都没搬动 → 第一轮结束，从头再来一遍（{StoreMethodName(StoreLadder[0])}）");
                        return;
                    }

                    this.StopJob("存入失败：游戏没给「背包 → 房屋仓库」这条对外接口（1112 / 1113 / 1112带目标页 三种发法 "
                               + "以及本地搬运 MoveItemSlot 都被服务器拒），请手动收纳，或用「收回已布置家具 → 仓库」"
                               + "（把家具先布置到屋里再收回）。");
                    return;
                }

                DalamudServices.PluginLog.Information(
                    $"[装修助手] 跳过：容器 {container} 槽位 {slot} 道具 {itemId}（连续 3 次没搬动）");
                this.jobTried.Add((container, slot));
                this.jobSkipped++;
                this.jobCurrent = (-1, -1);
                this.jobCurrentTries = 0;
                this.jobSkipStreak++;

                if (this.jobSkipStreak >= 10)
                {
                    this.StopJob($"连续 10 件都搬不动（{SourceNoun(this.job)} → 目标没成功）。" + loopDetail);
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
            switch (this.job)
            {
                case MoveKind.Store:
                    HousingCommand.Store(type, (uint)slot);
                    break;
                case MoveKind.StoreAsStore:
                    HousingCommand.Restore(type, (uint)slot, toStoreRoom: true);
                    break;
                case MoveKind.StoreAsDest:
                    HousingCommand.Store(StoreroomDest(), (uint)slot);
                    break;
                case MoveKind.RetrieveToStore:
                    HousingCommand.Restore(type, (uint)slot, toStoreRoom: true);
                    break;
                default:
                    HousingCommand.Restore(type, (uint)slot, toStoreRoom: false);
                    break;
            }

            this.jobSent++;
            this.jobCurrentTries++;
            this.jobSkipStreak = 0;

            // 状态行给「已搬几件」这个玩家真正关心的数（源侧件数约每 15 帧刷新一次）
            if (++this.jobSrcRefresh >= 15)
            {
                this.jobSrcRefresh = 0;
                var st = SourceContainers(this.job);
                this.jobSrcNow = this.CountInContainers(st, null);
            }
            var movedNow = Math.Max(0, this.jobStartCount - this.jobSrcNow);
            this.status = $"{ActionName(this.job)}中… 已搬 {movedNow} 件 / 已发 {this.jobSent} 次指令"
                        + (this.jobSkipped > 0 ? $"，跳过 {this.jobSkipped} 件（游戏没收）" : "");
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

        var types = SourceContainers(this.job);
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
