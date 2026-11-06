using System;
using System.Collections.Generic;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 符文结算（移植 CraftingEquipmentCell.RefreshCraftingInfo / GetAttrDic）：
    /// 炼器只看属性总值、不看摆位。等级 = min(floor(属性/要求)) 封顶 maxStacks；
    /// 品阶分 = Σ 权重×等级；法器属性 = 冰风雷×10 取最大，并列或全零为 0。
    /// </summary>
    public class RuneEvaluator
    {
        readonly List<Rune> runes;

        readonly int[] bonus;

        /// <summary>品阶阈值（CommonConst.CraftingItemGradeRequirementWeight），可选。</summary>
        readonly int[] gradeThresholds;

        public int RuneCount => runes.Count;

        public RuneEvaluator(List<Rune> runes, int[] bonus, int[] gradeThresholds = null)
        {
            this.runes = runes ?? new List<Rune>();
            this.bonus = bonus ?? new int[Attr.Count];
            this.gradeThresholds = gradeThresholds;
        }

        /// <summary>由摆法算属性总值（阳加阴减 + 炉/天赋加成）。</summary>
        public int[] SumAttrs(IEnumerable<Placement> placements)
        {
            var sum = (int[])bonus.Clone();
            if (placements != null)
            {
                foreach (var p in placements)
                {
                    int sign = p.Side == Side.Yang ? 1 : -1;
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        sum[d] += sign * p.Material.Attrs[d];
                    }
                }
            }
            return sum;
        }

        /// <summary>由摆法直接算符文等级/品阶分/法器属性。</summary>
        public RuneResult Evaluate(IEnumerable<Placement> placements)
        {
            return Evaluate(SumAttrs(placements));
        }

        /// <summary>由属性总值算符文等级/品阶分/法器属性。</summary>
        public RuneResult Evaluate(int[] attrs)
        {
            var result = new RuneResult
            {
                Level = new int[runes.Count],
                FinalAttrs = (int[])(attrs ?? new int[Attr.Count]).Clone()
            };
            for (int i = 0; i < runes.Count; i++)
            {
                var rune = runes[i];
                int level = int.MaxValue;
                for (int d = 0; d < Attr.Count; d++)
                {
                    int req = rune.Requirement[d];
                    if (req <= 0)
                    {
                        continue;
                    }
                    // C# 整数除法向零截断，与游戏 value / requirement 一致
                    level = Math.Min(level, attrs[d] / req);
                }
                level = Math.Min(rune.MaxStacks, level);
                if (level <= 0)
                {
                    continue;
                }
                result.Level[i] = level;
                result.ActiveCount++;
                result.TotalLevel += level;
                int w = rune.Weight != 0 ? rune.Weight : rune.DefaultWeight;
                result.GradeScore += (long)w * level;
            }
            result.ItemAttr = ItemAttrOf(attrs);
            return result;
        }

        /// <summary>法器属性：冰(5)/风(6)/雷(7)×10、其余×1 取最大；并列或全零为 0。取值 0–7（游戏枚举 +1）。</summary>
        public static int ItemAttrOf(int[] attrs)
        {
            int attr = 0;
            int best = 0;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (attrs[d] == 0)
                {
                    continue;
                }
                int scaled = attrs[d] * (d == 5 || d == 6 || d == 7 ? 10 : 1);
                if (scaled > best)
                {
                    attr = d;
                    best = scaled;
                }
                else if (attrs[d] == best)
                {
                    attr = 0; // 与游戏一致：并列判无属性
                }
            }
            return attr;
        }

        /// <summary>品阶档位（0-based，对应游戏 gradeId 200 + tier）；无阈值表或无生效符文返回 -1。</summary>
        public int GradeTier(long gradeScore)
        {
            if (gradeThresholds == null || gradeScore <= 0)
            {
                return -1;
            }
            int tier = -1;
            for (int i = 0; i < gradeThresholds.Length; i++)
            {
                if (gradeScore >= gradeThresholds[i])
                {
                    tier = i;
                }
            }
            return tier;
        }
    }
}
