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
        }

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
                try
                {
                    itemName = data.GetItemCfg(cfg.itemId)?.GetName ?? cfg.itemId;
                }
                catch (Exception)
                {
                }
                list.Add(new RecipeInfo
                {
                    Cfg = cfg,
                    Name = cfg.GetName,
                    ItemName = itemName,
                    AttrText = AttrText(Attr.FromDic(cfg.AttrLimitDic)),
                    Unlocked = unlocked.Contains(cfg.id) || crafted.Contains(cfg.itemId)
                });
            }
            return list;
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
