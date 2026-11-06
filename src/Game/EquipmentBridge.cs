using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PlayEditor;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft.Game
{
    /// <summary>炼器界面与求解器之间的读写：读锻造锤、符文、材料，写回摆放。只在主线程调用。</summary>
    public static class EquipmentBridge
    {
        /// <summary>CraftingEquipmentCell.Awake 时记下，界面关掉后 gameObject 不活跃。</summary>
        public static CraftingEquipmentCell Cell;

        static float nextFind;

        public static bool IsOpen
        {
            get
            {
                if (Cell == null && UnityEngine.Time.unscaledTime >= nextFind)
                {
                    nextFind = UnityEngine.Time.unscaledTime + 0.5f;
                    Cell = UnityEngine.Object.FindObjectOfType<CraftingEquipmentCell>();
                    if (Cell != null)
                    {
                        Plugin.Log.LogInfo("找到炼器界面（场景查找）");
                    }
                }
                return Cell != null && Cell.gameObject.activeInHierarchy && BsSaveDataImpl.nowActor != null;
            }
        }

        public static TbCraftingItemCfg GetFurnaceCfg()
        {
            var furnace = Traverse.Create(Cell).Field("furnace").GetValue<TbPackSto>();
            return furnace == null ? null : Singleton<TbDataImpl>.Instance.GetCraftingItemCfg(furnace.itemId.sedId);
        }

        /// <summary>锻造锤与炼器天赋的固定属性加成（属性加值参与符文等级计算）。</summary>
        public static int[] GetFixedBonus(TbCraftingItemCfg furnaceCfg)
        {
            var tmp = new CraftingEquipmentTmp { attrDic = new Dictionary<string, int>() };
            try
            {
                var data = Singleton<TbDataImpl>.Instance;
                foreach (int sideId in Singleton<TbTreeImpl>.Instance.GetSideTalentSto(SideTypeEnum.LianQi))
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
                Plugin.Log.LogWarning("读取锻造锤加成失败，按无加成计算: " + e.Message);
            }
            return Attr.FromDic(tmp.attrDic);
        }

        static Rune MakeRune(TbCraftingRuneCfg c)
        {
            var data = Singleton<TbDataImpl>.Instance;
            int defWeight = 0;
            if (c.weight == 0)
            {
                var g = data.GetGradeCfg(c.grade);
                if (g != null && g.weight >= 0 && g.weight < CommonConst.CraftingRuneWeight.Count)
                {
                    defWeight = CommonConst.CraftingRuneWeight[g.weight];
                }
            }
            return new Rune
            {
                Id = c.id,
                Name = c.GetName,
                Requirement = Attr.FromDic(c.AttrLimitDic),
                MaxStacks = c.maxStacks,
                Weight = c.weight,
                DefaultWeight = defWeight
            };
        }

        /// <summary>玩家已选的符文（在原界面勾选）。</summary>
        public static List<Rune> GetSelectedRunes()
        {
            var selected = Traverse.Create(Cell).Field("selectedRunes").GetValue<HashSet<TbCraftingRuneCfg>>();
            return selected == null ? new List<Rune>() : selected.Select(MakeRune).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>玩家已选的法器类型（typeId）。</summary>
        public static int GetSelectedType()
        {
            return Traverse.Create(Cell).Field("selectedType").GetValue<int>();
        }

        static Material MakeMaterial(string id, long stock)
        {
            var data = Singleton<TbDataImpl>.Instance;
            var item = data.GetItemCfg(id);
            var craft = data.GetCraftingItemCfg(id);
            // 炼器材料：typeId == 361（炼丹是灵草 parent type 10）
            if (item == null || craft == null || craft.attrDic == null || craft.attrDic.Count == 0 || item.typeId != 361)
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

        public static List<Material> GetMaterials()
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
            return list;
        }

        /// <summary>把方案摆上去。LoadCraftingTemplateSto 会读 typeId/runes 并覆盖选中态，必须回填当前选择。</summary>
        public static void Apply(EquipmentSolution sol)
        {
            var template = new TbCraftingTemplateSto
            {
                typeId = GetSelectedType(),
                runes = GetSelectedRunes().Select(r => r.Id).ToArray(),
                itemLogStoList = sol.Placements.Select(p => new TbCraftingItemLogSto(new BlendId(BlendEnum.ItemId, p.Material.Id), (int)p.Side, new MyVector2Int(p.X, p.Y), p.Rotate)).ToList()
            };
            Cell.LoadCraftingTemplateSto(template);
        }

        /// <summary>摆完后读游戏自己的结算：runeDic = 符文id→等级；attr = 法器属性（0 或 1–8）。</summary>
        public static (int placed, Dictionary<string, int> runeDic, int attr) ReadGameResult()
        {
            Cell.RefreshCraftingInfo();
            var tmp = Traverse.Create(Cell).Field("equipmentTmp").GetValue<CraftingEquipmentTmp>();
            var items = Traverse.Create(Cell).Field("dragItemList").GetValue<List<CraftingDragItemCell>>();
            return (items?.Count ?? 0, tmp?.runeDic, tmp?.attr ?? 0);
        }
    }

    [HarmonyPatch(typeof(CraftingEquipmentCell), "Awake")]
    internal static class CraftingEquipmentCellAwakePatch
    {
        private static void Postfix(CraftingEquipmentCell __instance)
        {
            EquipmentBridge.Cell = __instance;
            Plugin.Log.LogInfo("找到炼器界面");
        }
    }
}
