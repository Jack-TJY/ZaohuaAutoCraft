# 自动炼丹炼器（ZaohuaAutoCraft）

《造化仙缘》的 BepInEx 插件：在炼丹界面选好丹方，自动算出灵草怎么摆进阴阳两边的格子，并替你摆上。炼制按钮仍由你自己点。

> 当前进度：阶段 1（炼丹原型，「最少材料」模式）。品质 / 成丹数优化、炼器在后续阶段加入。

## 安装

1. 游戏需已装好 BepInEx 5（x64）。
2. 把 `ZaohuaAutoCraft.dll` 放进 `游戏目录\BepInEx\plugins\`。
3. 进游戏打开炼丹界面，右上角会出现「自动摆放 (F8)」按钮。

## 使用

1. 装备好丹炉，打开炼丹界面，点「自动摆放」。
2. 在列表里选丹方（默认只列已学的丹方和自己炼过的配方；勾「显示全部丹方」可列出全部）。
3. 点「计算摆法」，确认材料清单后点「摆上」。
4. 插件会读取游戏自己的结算结果核对丹方是否识别正确，然后由你点「炼制」。

配置文件 `BepInEx\config\zaohua.autocraft.cfg`：

| 项 | 默认 | 说明 |
| --- | --- | --- |
| ShowAllRecipes | false | 是否列出全部丹方 |
| ToggleKey | F8 | 打开/关闭窗口 |
| TimeLimitMs | 2000 | 每次求解的时间上限 |

## 原理

- 游戏规则（反编译所得）：阳格加属性、阴格减属性，加上丹炉/天赋的固定加成后，必须与丹方要求**完全相等**才出丹。
- 选材：按件数从少到多逐层搜索；同属性的材料归为一类；用单维可达性表和势函数下界剪枝。
- 装箱：异形块回溯（每次覆盖第一个空格或在预算内留空），先做棋盘与按行列模 k 染色快筛。
- 摆放：调用游戏自带的 `CraftingDrugCell.LoadCraftingTemplateSto`，与「配方」页一键摆放走同一逻辑。

## 构建

`lib/` 下放以下 dll（不随仓库分发）：游戏 `Zaohua_Data\Managed` 里的 `Assembly-CSharp`、`UnityEngine`、`UnityEngine.CoreModule`、`UnityEngine.IMGUIModule`、`UnityEngine.InputLegacyModule`、`UnityEngine.TextRenderingModule`、`UnityEngine.UI`，以及 `BepInEx\core` 里的 `BepInEx`、`0Harmony`。

```
dotnet build src -c Release
```

离线测试（需先用 [zaohua-guixu-shop 的 tools/extract.py](https://github.com/Jack-TJY/zaohua-guixu-shop) 解出配置表）：

```
dotnet run --project tests -c Release -- unpacked/data
```

## 许可

MIT
