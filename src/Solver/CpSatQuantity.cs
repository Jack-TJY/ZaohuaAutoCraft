using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.Sat;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// CP-SAT 数量主问题：求「满足 level-1 需求、总价最小」的材料件数（阳/阴）。
    /// 这是精确求解，只做数量（面积/库存/属性门槛），几何装填由 Packer 单独完成。
    /// </summary>
    public static class CpSatQuantity
    {
        /// <summary>形状切割：某侧的某个形状多重集装不下，需要至少一个形状件数减少。</summary>
        public class ShapeCut
        {
            public bool YinSide;                 // true=阴侧，false=阳侧
            public Dictionary<string, int> Counts = new Dictionary<string, int>();  // 形状键 -> 件数
        }

        /// <summary>返回阳/阴件数方案（材料引用 + 侧）；无解或超时返回 null。</summary>
        public static List<(Material mat, Side side)> Solve(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec,
            List<ShapeCut> cuts = null, bool minPieces = false)
        {
            var need = new int[Attr.Count];
            var required = new bool[Attr.Count];
            foreach (var r in runes)
            {
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (r.Requirement[d] > 0)
                    {
                        required[d] = true;
                        need[d] = Math.Max(need[d], r.Requirement[d]);
                    }
                }
            }
            for (int d = 0; d < Attr.Count; d++)
            {
                need[d] -= bonus[d];
            }

            var model = new CpModel();
            var yangVar = new IntVar[materials.Count];
            var yinVar = new IntVar[materials.Count];
            var active = new List<int>();
            for (int i = 0; i < materials.Count; i++)
            {
                var m = materials[i];
                if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                {
                    continue;
                }
                bool helps = false;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (required[d] && m.Attrs[d] != 0)
                    {
                        helps = true;
                        break;
                    }
                }
                if (!helps)
                {
                    continue;
                }
                int stock = (int)Math.Min(m.Stock, 10000L);
                yangVar[i] = model.NewIntVar(0, stock, $"y{i}");
                yinVar[i] = model.NewIntVar(0, stock, $"n{i}");
                active.Add(i);
                model.Add(yangVar[i] + yinVar[i] <= stock);
            }

            for (int d = 0; d < Attr.Count; d++)
            {
                if (!required[d])
                {
                    continue;
                }
                var terms = new List<LinearExpr>();
                foreach (int i in active)
                {
                    int a = materials[i].Attrs[d];
                    if (a == 0)
                    {
                        continue;
                    }
                    terms.Add(LinearExpr.Term(yangVar[i], a));
                    terms.Add(LinearExpr.Term(yinVar[i], -a));
                }
                if (terms.Count == 0)
                {
                    if (need[d] > 0)
                    {
                        return null;
                    }
                    continue;
                }
                model.Add(LinearExpr.Sum(terms) >= need[d]);
            }

            if (yang != null && active.Count > 0)
            {
                model.Add(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yangVar[i], materials[i].Shape.Count))) <= yang.Area);
            }
            if (yin != null && active.Count > 0)
            {
                model.Add(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yinVar[i], materials[i].Shape.Count))) <= yin.Area);
            }

            // 形状切割：排除已知装不下的形状多重集
            if (cuts != null)
            {
                foreach (var cut in cuts)
                {
                    AddShapeCut(model, cut.YinSide ? yinVar : yangVar, active, materials, cut.Counts);
                }
            }

            if (minPieces)
            {
                // 目标：min 总件数（最少材料模式）
                model.Minimize(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yangVar[i], 1) + LinearExpr.Term(yinVar[i], 1))));
            }
            else
            {
                // 目标：min 总价（主），同价下 min 件数（次，权重放大价格使价格优先）
                model.Minimize(LinearExpr.Sum(active.Select(i =>
                    LinearExpr.Term(yangVar[i], 1000L * materials[i].Price + 1) +
                    LinearExpr.Term(yinVar[i], 1000L * materials[i].Price + 1))));
            }

            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
            var status = solver.Solve(model);
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                return null;
            }
            var items = new List<(Material mat, Side side)>();
            foreach (int i in active)
            {
                int vy = (int)solver.Value(yangVar[i]);
                int vn = (int)solver.Value(yinVar[i]);
                for (int k = 0; k < vy; k++)
                {
                    items.Add((materials[i], Side.Yang));
                }
                for (int k = 0; k < vn; k++)
                {
                    items.Add((materials[i], Side.Yin));
                }
            }
            return items;
        }

        /// <summary>等级建模：求各模式的等级最优（最高品阶/指定属性最大/指定符文优先）。返回材料件数方案。</summary>
        public static List<(Material mat, Side side)> SolveLeveled(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec,
            EquipmentMode mode, int targetAttr = 0, bool[] priority = null, List<ShapeCut> cuts = null)
        {
            var model = new CpModel();
            var yangVar = new IntVar[materials.Count];
            var yinVar = new IntVar[materials.Count];
            var active = new List<int>();
            for (int i = 0; i < materials.Count; i++)
            {
                var m = materials[i];
                if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                {
                    continue;
                }
                bool helps = false;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (m.Attrs[d] != 0)
                    {
                        helps = true;
                        break;
                    }
                }
                if (!helps)
                {
                    continue;
                }
                int stock = (int)Math.Min(m.Stock, 10000L);
                yangVar[i] = model.NewIntVar(0, stock, $"y{i}");
                yinVar[i] = model.NewIntVar(0, stock, $"n{i}");
                active.Add(i);
                model.Add(yangVar[i] + yinVar[i] <= stock);
            }
            if (yang != null && active.Count > 0)
            {
                model.Add(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yangVar[i], materials[i].Shape.Count))) <= yang.Area);
            }
            if (yin != null && active.Count > 0)
            {
                model.Add(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yinVar[i], materials[i].Shape.Count))) <= yin.Area);
            }
            if (cuts != null)
            {
                foreach (var cut in cuts)
                {
                    AddShapeCut(model, cut.YinSide ? yinVar : yangVar, active, materials, cut.Counts);
                }
            }
            var levelVar = new IntVar[runes.Count];
            var objTerms = new List<LinearExpr>();
            for (int r = 0; r < runes.Count; r++)
            {
                var rune = runes[r];
                levelVar[r] = model.NewIntVar(1, rune.MaxStacks, $"lv{r}");
                for (int d = 0; d < Attr.Count; d++)
                {
                    int req = rune.Requirement[d];
                    if (req <= 0)
                    {
                        continue;
                    }
                    var terms = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        int a = materials[i].Attrs[d];
                        if (a == 0)
                        {
                            continue;
                        }
                        terms.Add(LinearExpr.Term(yangVar[i], a));
                        terms.Add(LinearExpr.Term(yinVar[i], -a));
                    }
                    terms.Add(LinearExpr.Term(levelVar[r], -req));
                    model.Add(LinearExpr.Sum(terms) >= -bonus[d]);
                }
                if (mode == EquipmentMode.MaxBatches)
                {
                    objTerms.Add(LinearExpr.Term(levelVar[r], 10000));
                }
                else if (mode == EquipmentMode.MaxRuneLevel)
                {
                    bool pri = priority != null && r < priority.Length && priority[r];
                    objTerms.Add(LinearExpr.Term(levelVar[r], pri ? 10000 : 1));
                }
            }
            if (mode == EquipmentMode.MaxAttr)
            {
                var terms = new List<LinearExpr>();
                foreach (int i in active)
                {
                    int a = materials[i].Attrs[targetAttr];
                    if (a == 0)
                    {
                        continue;
                    }
                    terms.Add(LinearExpr.Term(yangVar[i], a));
                    terms.Add(LinearExpr.Term(yinVar[i], -a));
                }
                model.Maximize(LinearExpr.Sum(terms));
            }
            else if (mode == EquipmentMode.MinPieces)
            {
                // 目标：max 等级（权重 10000），同等级 min 件数（权重 -1）
                var minTerms = new List<LinearExpr>();
                for (int r = 0; r < runes.Count; r++)
                {
                    minTerms.Add(LinearExpr.Term(levelVar[r], 10000));
                }
                foreach (int i in active)
                {
                    minTerms.Add(LinearExpr.Term(yangVar[i], -1));
                    minTerms.Add(LinearExpr.Term(yinVar[i], -1));
                }
                model.Maximize(LinearExpr.Sum(minTerms));
            }
            else if (mode == EquipmentMode.MaxBatches)
            {
                // 目标：max 等级（权重 10000），同等级 max 炼制次数（B）
                var bVar = model.NewIntVar(1, 10000, "batches");
                foreach (int i in active)
                {
                    var prod = model.NewIntVar(0, 100000000L, $"p{i}");
                    model.AddMultiplicationEquality(prod, bVar, yangVar[i] + yinVar[i]);
                    model.Add(prod <= materials[i].Stock);
                }
                var obj = new List<LinearExpr>(objTerms);
                obj.Add(LinearExpr.Term(bVar, 1));
                model.Maximize(LinearExpr.Sum(obj));
            }
            else
            {
                model.Maximize(LinearExpr.Sum(objTerms));
            }
            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
            var status = solver.Solve(model);
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                return null;
            }
            var items = new List<(Material mat, Side side)>();
            foreach (int i in active)
            {
                int vy = (int)solver.Value(yangVar[i]);
                int vn = (int)solver.Value(yinVar[i]);
                for (int k = 0; k < vy; k++)
                {
                    items.Add((materials[i], Side.Yang));
                }
                for (int k = 0; k < vn; k++)
                {
                    items.Add((materials[i], Side.Yin));
                }
            }
            return items;
        }

        /// <summary>形状切割：至少一个形状的件数比 cut 里少一件。</summary>
        static void AddShapeCut(CpModel model, IntVar[] sideVar, List<int> active, List<Material> materials, Dictionary<string, int> counts)
        {
            const int BIG = 10001;
            var bs = new List<IntVar>();
            int id = 0;
            foreach (var kv in counts)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }
                var terms = new List<LinearExpr>();
                foreach (int i in active)
                {
                    if (Packer.GetType(materials[i].Shape).Key == kv.Key)
                    {
                        terms.Add(LinearExpr.Term(sideVar[i], 1));
                    }
                }
                var sumExpr = LinearExpr.Sum(terms);
                var b = model.NewBoolVar($"cut{id++}");
                bs.Add(b);
                // b=1 时该形状件数 <= kv.Value-1；b=0 时约束不生效
                model.Add(sumExpr + LinearExpr.Term(b, BIG) <= kv.Value - 1 + BIG);
            }
            if (bs.Count > 0)
            {
                model.Add(LinearExpr.Sum(bs) >= 1);
            }
        }
    }
}
