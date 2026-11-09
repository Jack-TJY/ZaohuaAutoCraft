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
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},num_search_workers:1,random_seed:0";
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
            EquipmentMode mode, int targetAttr = 0, bool[] priority = null, List<ShapeCut> cuts = null, bool dominant = true)
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
                    // 等级权重须 > 次数权重(200) × 次数上界(10000)=2000000
                    objTerms.Add(LinearExpr.Term(levelVar[r], 2000001));
                }
                else if (mode == EquipmentMode.MaxRuneLevel)
                {
                    bool pri = priority != null && r < priority.Length && priority[r];
                    objTerms.Add(LinearExpr.Term(levelVar[r], pri ? 10000 : 1));
                }
            }
            if (mode == EquipmentMode.MaxAttr)
            {
                int ta = Math.Max(0, targetAttr);
                int wTa = (ta == 5 || ta == 6 || ta == 7) ? 10 : 1;
                if (dominant)
                {
                    // 目标属性领先：scaled_ta >= scaled_d + (ta != 0 ? 1 : 0)，并列归金
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        if (d == ta)
                        {
                            continue;
                        }
                        int wD = (d == 5 || d == 6 || d == 7) ? 10 : 1;
                        var domTerms = new List<LinearExpr>();
                        foreach (int i in active)
                        {
                            int aTa = materials[i].Attrs[ta] * wTa;
                            if (aTa != 0)
                            {
                                domTerms.Add(LinearExpr.Term(yangVar[i], aTa));
                                domTerms.Add(LinearExpr.Term(yinVar[i], -aTa));
                            }
                            int aD = materials[i].Attrs[d] * wD;
                            if (aD != 0)
                            {
                                domTerms.Add(LinearExpr.Term(yangVar[i], -aD));
                                domTerms.Add(LinearExpr.Term(yinVar[i], aD));
                            }
                        }
                        long delta = 0;
                        if (ta != 0)
                        {
                            // 贴近游戏归属：d<目标 需严格领先（否则目标压不过前面的最大值）；
                            // d>目标且是加权维度(冰/风/雷×10)时允许并列（并列不归金）；其余（非加权）并列归金，需严格
                            bool weightedD = d >= 5;
                            delta = (d < ta || !weightedD) ? 1L : 0L;
                        }
                        long rhsConst = delta + (long)bonus[d] * wD - (long)bonus[ta] * wTa;
                        model.Add(LinearExpr.Sum(domTerms) >= rhsConst);
                    }
                }
                // 目标：先最大化总等级，再最小化材料价值（价 → 件数）；不再堆目标属性数值
                long maxPrice = active.Count > 0 ? active.Max(i => (long)materials[i].Price) : 0;
                long maxPieces = (long)(yang?.Area ?? 0) + (yin?.Area ?? 0);
                long levelWeight = maxPieces * (200L * maxPrice + 1) + 1;
                var objAttr = new List<LinearExpr>();
                for (int r = 0; r < runes.Count; r++)
                {
                    objAttr.Add(LinearExpr.Term(levelVar[r], levelWeight));
                }
                foreach (int i in active)
                {
                    long w = 200L * materials[i].Price + 1;
                    objAttr.Add(LinearExpr.Term(yangVar[i], -w));
                    objAttr.Add(LinearExpr.Term(yinVar[i], -w));
                }
                model.Maximize(LinearExpr.Sum(objAttr));
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
                // 目标：max 等级(2000001) > max 次数(200) > min 件数(1)
                // 批次数上界用「最大库存」而非「最小库存」：未使用材料的库存不应压低次数（报告§6.2）
                long maxStock = active.Count > 0 ? active.Max(i => Math.Min(materials[i].Stock, 100000L)) : 1;
                if (maxStock < 1)
                {
                    maxStock = 1;
                }
                var bVar = model.NewIntVar(1, maxStock, "batches");
                foreach (int i in active)
                {
                    long cap = Math.Min(materials[i].Stock, 10000L);
                    var usageVar = model.NewIntVar(0, cap, $"u{i}");
                    model.Add(usageVar == yangVar[i] + yinVar[i]);
                    var prod = model.NewIntVar(0, maxStock, $"p{i}");
                    model.AddMultiplicationEquality(prod, bVar, usageVar);
                    model.Add(prod <= materials[i].Stock);
                }
                var obj = new List<LinearExpr>(objTerms);
                obj.Add(LinearExpr.Term(bVar, 200));
                foreach (int i in active)
                {
                    obj.Add(LinearExpr.Term(yangVar[i], -1));
                    obj.Add(LinearExpr.Term(yinVar[i], -1));
                }
                model.Maximize(LinearExpr.Sum(obj));
            }
            else
            {
                model.Maximize(LinearExpr.Sum(objTerms));
            }
            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},num_search_workers:1,random_seed:0";
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

        /// <summary>统一流程求解：优先级组先覆盖再升级，可选成品属性硬约束，材料策略，最后省料。</summary>
        public static List<(Material mat, Side side)> SolveUnified(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec,
            int[] priorityGroup, int desiredAttr, MaterialPolicy policy, List<ShapeCut> cuts = null, int[] fixedLevel = null)
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
            // 层数变量：允许 0（逐层覆盖语义）
            var levelVar = new IntVar[runes.Count];
            var hasVar = new IntVar[runes.Count];
            var has2Var = new IntVar[runes.Count];
            var has3Var = new IntVar[runes.Count];
            var has4Var = new IntVar[runes.Count];
            var has5Var = new IntVar[runes.Count];
            var has6Var = new IntVar[runes.Count];
            for (int r = 0; r < runes.Count; r++)
            {
                var rune = runes[r];
                levelVar[r] = model.NewIntVar(0, rune.MaxStacks, $"lv{r}");
                hasVar[r] = model.NewBoolVar($"h{r}");
                has2Var[r] = model.NewBoolVar($"h2{r}");
                has3Var[r] = model.NewBoolVar($"h3{r}");
                has4Var[r] = model.NewBoolVar($"h4{r}");
                has5Var[r] = model.NewBoolVar($"h5{r}");
                has6Var[r] = model.NewBoolVar($"h6{r}");
                model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(hasVar[r], rune.MaxStacks), LinearExpr.Term(levelVar[r], -1) }) >= 0);
                model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1), LinearExpr.Term(hasVar[r], -1) }) >= 0);
                if (rune.MaxStacks >= 2)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has2Var[r], rune.MaxStacks - 1), LinearExpr.Term(levelVar[r], -1) }) >= -1);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1), LinearExpr.Term(has2Var[r], -1) }) >= 1);
                }
                else
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has2Var[r], 1) }) == 0);
                }
                if (rune.MaxStacks >= 3)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has3Var[r], rune.MaxStacks - 2), LinearExpr.Term(levelVar[r], -1) }) >= -2);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1), LinearExpr.Term(has3Var[r], -3) }) >= 0);
                }
                else
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has3Var[r], 1) }) == 0);
                }
                if (rune.MaxStacks >= 4)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has4Var[r], rune.MaxStacks - 3), LinearExpr.Term(levelVar[r], -1) }) >= -3);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1), LinearExpr.Term(has4Var[r], -4) }) >= 0);
                }
                else
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has4Var[r], 1) }) == 0);
                }
                if (rune.MaxStacks >= 5)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has5Var[r], rune.MaxStacks - 4), LinearExpr.Term(levelVar[r], -1) }) >= -4);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1), LinearExpr.Term(has5Var[r], -5) }) >= 0);
                }
                else
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has5Var[r], 1) }) == 0);
                }
                if (rune.MaxStacks >= 6)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has6Var[r], rune.MaxStacks - 5), LinearExpr.Term(levelVar[r], -1) }) >= -5);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1), LinearExpr.Term(has6Var[r], -6) }) >= 0);
                }
                else
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(has6Var[r], 1) }) == 0);
                }
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
            }
            // 阶段2：固定第一阶段已选层数向量，再单独优化材料策略/省料
            if (fixedLevel != null)
            {
                for (int r = 0; r < runes.Count && r < fixedLevel.Length; r++)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1) }) == fixedLevel[r]);
                }
            }
            // 可选成品属性：硬约束
            if (desiredAttr >= 0)
            {
                int ta = Math.Min(desiredAttr, Attr.Count - 1);
                int wTa = (ta == 5 || ta == 6 || ta == 7) ? 10 : 1;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (d == ta)
                    {
                        continue;
                    }
                    int wD = (d == 5 || d == 6 || d == 7) ? 10 : 1;
                    var domTerms = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        int aTa = materials[i].Attrs[ta] * wTa;
                        if (aTa != 0)
                        {
                            domTerms.Add(LinearExpr.Term(yangVar[i], aTa));
                            domTerms.Add(LinearExpr.Term(yinVar[i], -aTa));
                        }
                        int aD = materials[i].Attrs[d] * wD;
                        if (aD != 0)
                        {
                            domTerms.Add(LinearExpr.Term(yangVar[i], -aD));
                            domTerms.Add(LinearExpr.Term(yinVar[i], aD));
                        }
                    }
                    long delta = 0;
                    if (ta != 0)
                    {
                        bool weightedD = d >= 5;
                        delta = (d < ta || !weightedD) ? 1L : 0L;
                    }
                    long rhsConst = delta + (long)bonus[d] * wD - (long)bonus[ta] * wTa;
                    model.Add(LinearExpr.Sum(domTerms) >= rhsConst);
                }
            }
            // 优先级组：组间降权，组内先覆盖(每符文一层)再升级
            int maxGroup = 0;
            for (int r = 0; r < runes.Count; r++)
            {
                int g = priorityGroup != null && r < priorityGroup.Length ? Math.Max(0, priorityGroup[r]) : 0;
                maxGroup = Math.Max(maxGroup, g);
            }
            var obj = new List<LinearExpr>();
            if (fixedLevel == null)
            {
                // 阶段1：符文质量（先覆盖1/2/3层，再逐层升级）
                for (int g = 0; g <= maxGroup; g++)
                {
                    long gw = 1;
                    for (int k = 0; k < maxGroup - g; k++)
                    {
                        gw *= 1000;  // 组间权重：高优先组严格压过低优先组
                    }
                    var grp = new List<LinearExpr>();
                    for (int r = 0; r < runes.Count; r++)
                    {
                        int rg = priorityGroup != null && r < priorityGroup.Length ? Math.Max(0, priorityGroup[r]) : 0;
                        if (rg != g)
                        {
                            continue;
                        }
                        grp.Add(LinearExpr.Term(hasVar[r], gw * 100000L * 1000000L));
                        grp.Add(LinearExpr.Term(has2Var[r], gw * 1000L * 1000000L));
                        grp.Add(LinearExpr.Term(has3Var[r], gw * 10L * 1000000L));
                        grp.Add(LinearExpr.Term(has4Var[r], gw * 5L * 1000000L));
                        grp.Add(LinearExpr.Term(has5Var[r], gw * 3L * 1000000L));
                        grp.Add(LinearExpr.Term(has6Var[r], gw * 2L * 1000000L));
                        grp.Add(LinearExpr.Term(levelVar[r], gw * 1000000L));
                    }
                    obj.Add(LinearExpr.Sum(grp));
                }
            }
            // 材料策略
            if (policy == MaterialPolicy.MaxRepeatCount)
            {
                long maxStock = active.Count > 0 ? active.Max(i => Math.Min(materials[i].Stock, 100000L)) : 1;
                if (maxStock < 1)
                {
                    maxStock = 1;
                }
                var bVar = model.NewIntVar(1, maxStock, "batches");
                foreach (int i in active)
                {
                    long cap = Math.Min(materials[i].Stock, 10000L);
                    var usageVar = model.NewIntVar(0, cap, $"u{i}");
                    model.Add(usageVar == yangVar[i] + yinVar[i]);
                    var prod = model.NewIntVar(0, maxStock, $"p{i}");
                    model.AddMultiplicationEquality(prod, bVar, usageVar);
                    model.Add(prod <= materials[i].Stock);
                }
                // 阶段2：次数权重放大到压过品阶/件数，确保「次数优先、同次数低阶」
                obj.Add(LinearExpr.Term(bVar, fixedLevel != null ? 100000000L : 1L));
            }
            else if (fixedLevel != null)
            {
                // 阶段2：精确品阶数量向量 D（从最高阶到最低阶字典序最小；指数权重，无符文质量竞争）
                var grades = active.Select(i => materials[i].Grade).Distinct().OrderByDescending(g => g).ToList();
                long baseW = Math.Max(2L, (long)((yang?.Area ?? 0) + (yin?.Area ?? 0)) + 1L);
                var gw = new Dictionary<int, long>();
                long wt = 1;
                for (int gi = grades.Count - 1; gi >= 0; gi--)
                {
                    gw[grades[gi]] = wt;
                    if (gi > 0)
                    {
                        wt = wt > long.MaxValue / baseW ? long.MaxValue / 2 : wt * baseW;
                    }
                }
                foreach (int i in active)
                {
                    long weight = gw.TryGetValue(materials[i].Grade, out var val) ? val : 1;
                    obj.Add(LinearExpr.Term(yangVar[i], -weight));
                    obj.Add(LinearExpr.Term(yinVar[i], -weight));
                }
            }
            else
            {
                // 注：精确「品阶数量向量」词典序需指数级权重，会压过符文质量，故用秩加权代理。
                var grades = active.Select(i => materials[i].Grade).Distinct().OrderBy(g => g).ToList();
                var rank = new Dictionary<int, int>();
                for (int gi = 0; gi < grades.Count; gi++)
                {
                    rank[grades[gi]] = gi;
                }
                foreach (int i in active)
                {
                    // 品阶秩主导(×2000)，同阶再最少件数(+1)
                    long w = (rank.TryGetValue(materials[i].Grade, out var r2) ? r2 : 0) * 2000L + 1;
                    obj.Add(LinearExpr.Term(yangVar[i], -w));
                    obj.Add(LinearExpr.Term(yinVar[i], -w));
                }
            }
            if (fixedLevel != null && policy == MaterialPolicy.MaxRepeatCount)
            {
                // 阶段2：次数优先；同次数低阶优先(品阶秩)，再最少件数
                var grades = active.Select(i => materials[i].Grade).Distinct().OrderBy(g => g).ToList();
                var rank = new Dictionary<int, int>();
                for (int gi = 0; gi < grades.Count; gi++)
                {
                    rank[grades[gi]] = gi;
                }
                foreach (int i in active)
                {
                    long w = (rank.TryGetValue(materials[i].Grade, out var r2) ? r2 : 0) * 2000L + 1;
                    obj.Add(LinearExpr.Term(yangVar[i], -w));
                    obj.Add(LinearExpr.Term(yinVar[i], -w));
                }
            }
            model.Maximize(LinearExpr.Sum(obj));
            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},num_search_workers:1,random_seed:0";
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
