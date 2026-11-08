using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Google.OrTools.Sat;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft.Tests
{
    /// <summary>离线测试用的 CP-SAT 包装：复用 src 的 CpSatQuantity + 装填 + 计时（不重复建模）。</summary>
    public static class CpSatSolver
    {
        public class Result
        {
            public bool Ok;
            public List<(Material mat, Side side)> Items = new();
            public long TotalPrice;
            public int PieceCount => Items.Count;
            public long QuantityMs;
            public long PackMs;
            public CpSolverStatus Status;
            public string Message;
        }

        public static Result Solve(List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec)
        {
            var sw = Stopwatch.StartNew();
            var res = new Result();
            var items = CpSatQuantity.Solve(materials, runes, bonus, yang, yin, timeLimitSec);
            res.QuantityMs = sw.ElapsedMilliseconds;
            if (items == null)
            {
                res.Message = "CP-SAT 无解/超时";
                res.Status = CpSolverStatus.Unknown;
                return res;
            }
            res.Items = items;
            res.TotalPrice = items.Sum(x => (long)x.mat.Price);
            res.Status = CpSolverStatus.Optimal;
            sw.Restart();
            var sol = PackItems(materials, yang, yin, items, 500);
            res.PackMs = sw.ElapsedMilliseconds;
            res.Ok = sol != null;
            res.Message = (res.Ok ? "最优" : "数量解装不下") + "+装填" + (res.Ok ? "成功" : "失败");
            return res;
        }

        /// <summary>等级建模 CP-SAT：求各模式的等级最优（最高品阶/指定属性最大/指定符文优先）。返回材料件数方案。</summary>
        public static List<(Material mat, Side side)> SolveLeveled(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec,
            EquipmentMode mode, int targetAttr = 0, bool[] priority = null)
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
                int w = rune.Weight != 0 ? rune.Weight : rune.DefaultWeight;
                if (mode == EquipmentMode.MaxGrade)
                {
                    objTerms.Add(LinearExpr.Term(levelVar[r], w));
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

        public static EquipmentSolution PackItems(List<Material> materials, Grid yang, Grid yin, List<(Material mat, Side side)> items, int packTimeMs)
        {
            var sol = new EquipmentSolution();
            var sw = Stopwatch.StartNew();
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var pieces = items.Where(x => x.side == side).Select(x => x.mat).ToList();
                if (pieces.Count == 0)
                {
                    continue;
                }
                var grid = side == Side.Yang ? yang : yin;
                if (grid == null)
                {
                    return null;
                }
                var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), sw, packTimeMs);
                if (!packed.Ok)
                {
                    return null;
                }
                for (int i = 0; i < pieces.Count; i++)
                {
                    var s = packed.Spots[i];
                    sol.Placements.Add(new Placement { Material = pieces[i], Side = side, X = s.x, Y = s.y, Rotate = s.rotate });
                }
            }
            return sol;
        }
    }
}
