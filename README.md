# LKami9装修助手

批量搬运家具/庭具的 OmniToolbox 在线模块。

## 功能（v1.0.0）

| 功能 | 说明 |
|---|---|
| 批量存入 | 背包里的**家具/庭具**一次性存进房屋仓库（家具仓库 / 庭具置场） |
| 批量取出 | 把房屋仓库里的家具/庭具批量取回背包 |
| 范围 | 可勾选只搬「家具」或只搬「庭具」 |
| 节奏 | 每件之间的间隔可调（默认 300ms，太快游戏会处理不过来） |
| 停止 | 随时中止，并给出「已搬 N 件 / 跳过 M 件 / 还剩 X 件」 |

## 使用

```
1. 站到自己家（或自己院子里）—— 必须**有房屋权限**，否则游戏会拒绝
2. 打开「家具/庭具仓库」窗口（院子/屋里的收纳箱，或房屋菜单）
3. 勾选范围 → 点「批量存入」或「批量取出」
4. 中途想停就点「停止」
```

也可以走命令：

```
/omni LKami9HousingHelper store   批量存入
/omni LKami9HousingHelper take    批量取出
```

## 实现要点

```
搬运指令用的是宿主自带的 OmenTools 公开 API：
  OmenTools.Interop.Game.ExecuteCommand.Implementations.HousingCommand
    Store(container, slot)                          存入仓库
    Restore(container, slot, toStoreRoom: false)    从仓库取回
容器号（FFXIVClientStructs.InventoryType）：
  背包            Inventory1..4
  庭具置场        27000 / 27200
  家具仓库        27001 ~ 27011（11 页）
「是不是家具/庭具」用 Lumina 的两张表判断：
  HousingFurniture（家具） / HousingYardObject（庭具）→ 取 row.Item.RowId 建索引
节奏：Framework.Update 每 N 帧只发一条指令；同一个槽位连续 3 次没搬动就跳过，
      连续 10 件都搬不动就停下并提示（仓库满 / 没开仓库窗口 / 权限不足）
```
