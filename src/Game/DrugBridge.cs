using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PlayEditor;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft.Game
{
    /// <summary>炼丹界面与求解器之间的读写：读丹炉、背包、丹方，写回摆放。只在主线程调用。</summary>
    public static class DrugBridge
    {
        /// <summary>CraftingDrugCell.Awake 时记下，界面关掉后 gameObject 不活跃。</summary>
        public static CraftingDrugCell Cell;

        static float nextFind;

        public static bool IsOpen
        {
            get
            {
                // Awake 补丁万一没抓到（比如对象池早于插件加载），就定时在场景里找一次
                if (Cell == null && UnityEngine.Time.unscaledTime >= nextFind)
                {
                    nextFind = UnityEngine.Time.unscaledTime + 0.5f;
                    Cell = UnityEngine.Object.FindObjectOfType<CraftingDrugCell>();
                    if (Cell != null)
                    {
                        Plugin.Log.LogInfo("找到炼丹界面（场景查找）");
                    }
                }
                return Cell != null && Cell.gameObject.activeInHierarchy && BsSaveDataImpl.nowActor != null;
            }
        }

        public class RecipeInfo
        {
            public TbDrugRecipeCfg Cfg;

            public string Name;

            public string ItemName;

            public string AttrText;

            public bool Unlocked;

            /// <summary>主属性分类（产物 useEff 的第一个效果命令；没有可用效果时按物品子类）。只用于显示。</summary>
            public string Category;

            /// <summary>该丹方涉及的全部属性分类（多效果丹方每个效果都算），用于筛选；数据读不到时是「未分类」。</summary>
            public string[] Categories;

            /// <summary>耐药性显示文本（已用/上限，如 "0/15"；不受耐药限制时为「无」），读不到时为「?」。</summary>
            public string Resistance;

            public bool HasCategory(string category)
            {
                if (Categories == null || category == null)
                {
                    return false;
                }
                for (int i = 0; i < Categories.Length; i++)
                {
                    if (Categories[i] == category)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>属性筛选里的「全部」，不做任何过滤。</summary>
        public const string CategoryAll = "全部";

        /// <summary>分类数据缺失时的兜底分类。</summary>
        public const string CategoryUnknown = "未分类";

        /// <summary>
        /// 属性分类的固定显示顺序（分类本身由产物 TbItemCfg.useEff 的效果命令 / typeId 推出，不放丹方名单）。
        /// 顺序按玩家常见关注度排，筛选栏只显示当前丹方里实际出现的分类。
        /// </summary>
        public static readonly string[] CategoryOrder =
        {
            "气血上限", "回血", "速度", "护甲", "护罩", "物攻", "物防", "术攻", "术防", "灵根",
            "丹田", "神魂", "气运", "寿元", "骨龄", "修炼", "突破", "辅助"
        };

        /// <summary>
        /// TbItemCfg.useEff 里的效果命令（游戏 EventEnum 名）→ 玩家看得懂的属性分类。
        /// 游戏没有「气血/速度/护甲」这种分类表：丹药提升什么属性只能从产物物品的效果命令看出来，
        /// 中文效果描述在 TbItemCfgLocal（如「护甲上限+4」），这里把命令归并成筛选用的短分类名。
        /// </summary>
        static readonly Dictionary<string, string> EffectCategories = new Dictionary<string, string>
        {
            { "UpdateHp", "回血" },              // 恢复 N 点气血
            { "UpdateHpLimit", "气血上限" },     // 气血上限 +N（与「回血」分开：一次性回复 vs 永久上限）
            { "UpdateAdvance", "速度" },         // 速度上限 +N
            { "UpdateArmor", "护甲" },           // 护甲上限 +N
            { "UpdateShield", "护罩" },          // 护罩上限 +N
            { "UpdatePhyAtt", "物攻" },          // 物攻上限 +N
            { "UpdatePhyDef", "物防" },          // 物防上限 +N
            { "UpdateSorAtt", "术攻" },          // 术攻上限 +N
            { "UpdateSorDef", "术防" },          // 术防上限 +N
            { "UpdateGold", "灵根" },            // 金/水/木/火/土/冰/风/雷灵根 +N（以下 8 条）
            { "UpdateWater", "灵根" },
            { "UpdateWood", "灵根" },
            { "UpdateFire", "灵根" },
            { "UpdateSoil", "灵根" },
            { "UpdateIce", "灵根" },
            { "UpdateWind", "灵根" },
            { "UpdateThunder", "灵根" },
            { "UpdateDantian", "丹田" },         // 丹田上限 +N
            { "UpdateMind", "神魂" },            // 神魂上限 +N
            { "UpdateFate", "气运" },            // 气运上限 +N
            { "UpdateYear", "寿元" },            // 寿元 +N
            { "UpdateAge", "骨龄" },             // 骨龄 -N
            { "UpdatePlayerPracBoost", "修炼" }, // 修炼提升 +N%
            { "UpgradeMinorLevel", "突破" },     // 突破一个小境界
        };

        /// <summary>已经警告过的未知效果命令，避免每开一次窗口刷一屏日志。</summary>
        static readonly HashSet<string> WarnedEffectCommands = new HashSet<string>();

        public static string AttrText(int[] v)
        {
            var parts = new List<string>();
            for (int d = 0; d < Attr.Count; d++)
            {
                if (v[d] != 0)
                {
                    parts.Add(Attr.Names[d] + (v[d] > 0 ? "+" : "") + v[d]);
                }
            }
            return parts.Count == 0 ? "无" : string.Join(" ", parts);
        }

        public static List<RecipeInfo> GetRecipes()
        {
            var data = Singleton<TbDataImpl>.Instance;
            var actor = BsSaveDataImpl.nowActor;
            var unlocked = new HashSet<string>(actor.drugRecipeStoList.Select(s => s.recipeId));
            // 玩家自己炼过的配方：配方页里每条记录对应一种丹，找出产出这种丹的丹方
            var crafted = new HashSet<string>(actor.craftingTemplateStoList.Where(t => t.type == 0)
                .Select(t => Singleton<TbItemImpl>.Instance.GetGroupId(t.itemId).sedId));
            var list = new List<RecipeInfo>();
            foreach (var cfg in data.drugRecipeList)
            {
                if (cfg.AttrLimitDic == null || cfg.AttrLimitDic.Count == 0)
                {
                    continue;
                }
                string itemName = cfg.itemId;
                TbItemCfg item = null;
                try
                {
                    item = data.GetItemCfg(cfg.itemId);
                    itemName = item?.GetName ?? cfg.itemId;
                }
                catch (Exception)
                {
                    itemName = cfg.itemId;
                }
                string[] categories = GetCategories(item);
                list.Add(new RecipeInfo
                {
                    Cfg = cfg,
                    Name = cfg.GetName,
                    ItemName = itemName,
                    AttrText = AttrText(Attr.FromDic(cfg.AttrLimitDic)),
                    Unlocked = unlocked.Contains(cfg.id) || crafted.Contains(cfg.itemId),
                    Category = categories[0],
                    Categories = categories,
                    Resistance = GetResistanceText(cfg, item)
                });
            }
            return list;
        }

        /// <summary>
        /// 按产物物品的效果命令推出属性分类。数据缺失或命令不认识时，再按物品子类（修炼/辅助/突破）兜底，
        /// 仍推不出就是「未分类」。全程不抛异常。
        /// </summary>
        static string[] GetCategories(TbItemCfg item)
        {
            var result = new List<string>();
            try
            {
                string useEff = item?.useEff;
                if (!string.IsNullOrEmpty(useEff))
                {
                    // useEff 形如 "UpdateHpLimit,20&UpdateYear,-5"：'&' 分隔多条效果，',' 后是参数
                    foreach (string raw in useEff.Split('&'))
                    {
                        string command = raw;
                        int comma = command.IndexOf(',');
                        if (comma >= 0)
                        {
                            command = command.Substring(0, comma);
                        }
                        command = command.Trim();
                        if (command.Length == 0)
                        {
                            continue;
                        }
                        if (EffectCategories.TryGetValue(command, out string category))
                        {
                            if (!result.Contains(category))
                            {
                                result.Add(category);
                            }
                        }
                        else if (WarnedEffectCommands.Add(command))
                        {
                            Plugin.Log.LogWarning($"未知丹药效果命令「{command}」，对应的丹方按物品子类或「{CategoryUnknown}」归类");
                        }
                    }
                }
                if (result.Count == 0)
                {
                    // 修炼丹/辅助丹/部分突破丹没有 useEff，只能按 TbItemCfg.typeId → TbTypeCfg 的丹药子类归类：
                    // 242 修炼、244 辅助、246 突破（与 TbTypeCfg 的 1_242/1_244/1_246 一致）
                    switch (item?.typeId ?? 0)
                    {
                        case 242:
                            result.Add("修炼");
                            break;
                        case 244:
                            result.Add("辅助");
                            break;
                        case 246:
                            result.Add("突破");
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("推断丹药属性分类失败，按未分类处理: " + e.Message);
                result.Clear();
            }
            if (result.Count == 0)
            {
                result.Add(CategoryUnknown);
            }
            return result.ToArray();
        }

        /// <summary>
        /// 丹方属性筛选列表：「全部」在最前，中间按 CategoryOrder 固定顺序，配置里新增的未知分类排后面，最后是「未分类」。
        /// 只列当前丹方列表里实际出现的分类；列表为空时只有「全部」。
        /// </summary>
        public static List<string> GetCategoryFilters(List<RecipeInfo> recipes)
        {
            var present = new HashSet<string>();
            if (recipes != null)
            {
                foreach (var r in recipes)
                {
                    if (r?.Categories == null)
                    {
                        continue;
                    }
                    foreach (string c in r.Categories)
                    {
                        if (!string.IsNullOrEmpty(c))
                        {
                            present.Add(c);
                        }
                    }
                }
            }
            var filters = new List<string> { CategoryAll };
            foreach (string c in CategoryOrder)
            {
                if (present.Remove(c))
                {
                    filters.Add(c);
                }
            }
            bool hasUnknown = present.Remove(CategoryUnknown);
            foreach (string c in present.OrderBy(x => x, StringComparer.Ordinal))
            {
                filters.Add(c);
            }
            if (hasUnknown)
            {
                filters.Add(CategoryUnknown);
            }
            return filters;
        }

        /// <summary>
        /// 耐药性显示文本：优先用游戏自己的 TbDrugMaxImpl.GetDrugTextShow（含「耐药」天赋翻倍、当前已用值，
        /// 无耐药上限时返回本地化的「无」）；游戏接口异常时退回 TbItemCfg.drugMax + 存档 drugUse，仍失败返回「?」。
        /// </summary>
        public static string GetResistanceText(TbDrugRecipeCfg recipe, TbItemCfg item = null)
        {
            if (recipe == null)
            {
                return "?";
            }
            try
            {
                string text = Singleton<TbDrugMaxImpl>.Instance.GetDrugTextShow(recipe.blendId);
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("读取耐药性失败，改用配置数值: " + e.Message);
            }
            try
            {
                var data = Singleton<TbDataImpl>.Instance;
                if (item == null)
                {
                    item = data.GetItemCfg(recipe.itemId);
                }
                if (item == null)
                {
                    return "?";
                }
                string groupId = item.groupId.IsId() ? item.groupId : item.id;
                var group = data.GetItemCfg(groupId) ?? item;
                int max = group.drugMax;
                if (max <= 0)
                {
                    return "无";
                }
                int used = 0;
                var actor = BsSaveDataImpl.nowActor;
                var sto = actor?.drugMaxStoList?.Find(s => s.id == group.blendId);
                if (sto != null)
                {
                    used = sto.drugUse;
                }
                return used + "/" + max;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        public static TbCraftingItemCfg GetFurnaceCfg()
        {
            var furnace = Traverse.Create(Cell).Field("furnace").GetValue<TbPackSto>();
            return furnace == null ? null : Singleton<TbDataImpl>.Instance.GetCraftingItemCfg(furnace.itemId.sedId);
        }

        public class FurnaceBonus
        {
            public int[] Attrs = new int[Attr.Count];

            public int Count;

            public int Quality;

            public List<int> StateIds = new List<int>();
        }

        /// <summary>
        /// 丹炉和炼丹天赋的固定加成（属性、丹数、品质、附加药性）：用一个空的 CraftingDrugTmp 按 RefreshDrugInfo 的顺序跑一遍游戏自己的效果。
        /// </summary>
        public static FurnaceBonus GetFixedBonus(TbCraftingItemCfg furnaceCfg)
        {
            var tmp = new CraftingDrugTmp
            {
                attrDic = new Dictionary<string, int>(),
                poolDic = Traverse.Create(Cell).Field("poolDic").GetValue<List<Dictionary<MyVector2Int, CraftingDragItemCell>>>()
            };
            try
            {
                var data = Singleton<TbDataImpl>.Instance;
                foreach (int sideId in Singleton<TbTreeImpl>.Instance.GetSideTalentSto(SideTypeEnum.LianDan))
                {
                    var side = data.sideCfgList.Find(s => s.id == sideId);
                    if (side != null)
                    {
                        MonoSingleton<BsPlayEffectImpl>.Instance.AllDOEvent(GenericMethods.GetEffectListByStr(side.craftingEff), tmp);
                    }
                }
                if (furnaceCfg != null)
                {
                    MonoSingleton<BsPlayEffectImpl>.Instance.AllDOEvent(Singleton<PlayEditorManager>.Instance.GetAllActiveDoBaseEffect(furnaceCfg.creaftingEffect, isJud: true, tmp), tmp);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("读取丹炉加成失败，按无加成计算: " + e.Message);
            }
            return new FurnaceBonus { Attrs = Attr.FromDic(tmp.attrDic), Count = tmp.count, Quality = tmp.quality, StateIds = tmp.stateIds.ToList() };
        }

        public static DrugState ToState(TbDrugRecipeStateCfg c)
        {
            return c == null ? null : new DrugState
            {
                Id = c.id,
                Name = c.name,
                PoolType = c.poolType,
                StateType = c.stateType,
                Area = c.area,
                Target1 = c.target1,
                Target2 = c.target2,
                Relation = c.relation,
                CalculateType = c.calculateType,
                BaseEff = c.baseEff
            };
        }

        /// <summary>丹方自带 + 丹炉/天赋附加的药性规则。</summary>
        public static List<DrugState> GetStates(TbDrugRecipeCfg recipe, FurnaceBonus bonus)
        {
            var data = Singleton<TbDataImpl>.Instance;
            return bonus.StateIds.Concat(recipe.StateIds).Select(id => ToState(data.GetDrugRecipeStateCfg(id))).Where(s => s != null).ToList();
        }

        static Material MakeMaterial(string id, long stock)
        {
            var data = Singleton<TbDataImpl>.Instance;
            var item = data.GetItemCfg(id);
            var craft = data.GetCraftingItemCfg(id);
            if (item == null || craft == null || craft.attrDic == null || craft.attrDic.Count == 0 || Singleton<TbItemImpl>.Instance.GetParentByTypeId(item.typeId) != 10)
            {
                return null;
            }
            var draw = data.GetDrawCfg(craft.drawId);
            if (draw == null)
            {
                return null;
            }
            return new Material
            {
                Id = id,
                Name = item.GetName,
                Attrs = Attr.FromDic(craft.attrDic),
                Shape = draw.Coordinates.Select(c => new Cell(c.x, c.y)).ToList(),
                Stock = stock,
                Grade = data.GetGradeCfg(item.gradeId)?.weight ?? 0,
                Price = item.price,
                Element = item.attribute,
                Buyable = item.IsShop == 1 || item.IsTrader == 1
            };
        }

        /// <param name="catalog">true 时把背包里没有、但商店/商人能买到的灵草也列进来（库存 0），用于补料建议。</param>
        public static List<Material> GetMaterials(bool catalog = false)
        {
            var stock = new Dictionary<string, long>();
            foreach (var pack in Singleton<BsBagImpl>.Instance.GetPackListByNpcStoId(10000L, -1))
            {
                if (pack.itemId.blendEnum != BlendEnum.ItemId || pack.haveCount <= 0)
                {
                    continue;
                }
                stock[pack.itemId.sedId] = (stock.TryGetValue(pack.itemId.sedId, out long n) ? n : 0) + pack.haveCount;
            }
            var list = new List<Material>();
            foreach (var kv in stock)
            {
                var m = MakeMaterial(kv.Key, kv.Value);
                if (m != null)
                {
                    list.Add(m);
                }
            }
            if (catalog)
            {
                foreach (var c in Singleton<TbDataImpl>.Instance.craftingItemList)
                {
                    if (c?.id == null || stock.ContainsKey(c.id))
                    {
                        continue;
                    }
                    var m = MakeMaterial(c.id, 0);
                    if (m != null && m.Buyable)
                    {
                        list.Add(m);
                    }
                }
            }
            return list;
        }

        public static void Apply(Solution sol)
        {
            var template = new TbCraftingTemplateSto
            {
                itemLogStoList = sol.Placements.Select(p => new TbCraftingItemLogSto(new BlendId(BlendEnum.ItemId, p.Material.Id), (int)p.Side, new MyVector2Int(p.X, p.Y), p.Rotate)).ToList()
            };
            Cell.LoadCraftingTemplateSto(template);
        }

        /// <summary>摆完后读游戏自己的结算结果。</summary>
        public static (int placed, TbDrugRecipeCfg recipe, int count, int quality) ReadGameResult()
        {
            Cell.RefreshDrugInfo();
            var tmp = Traverse.Create(Cell).Field("drugTmp").GetValue<CraftingDrugTmp>();
            var items = Traverse.Create(Cell).Field("dragItemList").GetValue<List<CraftingDragItemCell>>();
            return (items?.Count ?? 0, tmp?.recipeCfg, tmp?.count ?? 0, tmp?.quality ?? 0);
        }
    }

    [HarmonyPatch(typeof(CraftingDrugCell), "Awake")]
    internal static class CraftingDrugCellAwakePatch
    {
        private static void Postfix(CraftingDrugCell __instance)
        {
            DrugBridge.Cell = __instance;
            Plugin.Log.LogInfo("找到炼丹界面");
        }
    }
}
