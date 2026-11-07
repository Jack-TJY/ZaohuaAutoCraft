using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Google.OrTools.Sat;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft.Tests
{
    /// <summary>
    /// CP-SAT 数量主问题求解器（测试专用，跑在 net8.0）：
    /// 先求「满足 level-1 需求、总价最小」的材料件数（阳/阴），再交给几何装填子问题。
    /// </summary>
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

        /// <summary>求数量主问题：min 总价，s.t. 属性门槛 + 面积 + 库存。</summary>
        public static Result Solve(List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec)
        {
            var sw = Stopwatch.StartNew();
            var res = new Result();

            // need = max(requirement) - bonus；required = 有符文要求该维
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

            // 属性门槛（只约束被要求的维度）
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
                        res.Message = $"维度 {d} 无可用材料";
                        return res;
                    }
                    continue;
                }
                model.Add(LinearExpr.Sum(terms) >= need[d]);
            }

            // 面积必要条件
            if (yang != null && active.Count > 0)
            {
                var t = active.Select(i => LinearExpr.Term(yangVar[i], materials[i].Shape.Count)).ToList();
                model.Add(LinearExpr.Sum(t) <= yang.Area);
            }
            if (yin != null && active.Count > 0)
            {
                var t = active.Select(i => LinearExpr.Term(yinVar[i], materials[i].Shape.Count)).ToList();
                model.Add(LinearExpr.Sum(t) <= yin.Area);
            }

            // 目标：min 总价
            var obj = active.Select(i => LinearExpr.Term(yangVar[i], materials[i].Price) + LinearExpr.Term(yinVar[i], materials[i].Price)).ToList();
            model.Minimize(LinearExpr.Sum(obj));

            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
            var status = solver.Solve(model);
            res.Status = status;
            res.QuantityMs = sw.ElapsedMilliseconds;
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                res.Message = $"CP-SAT 无解/超时: {status}";
                return res;
            }
            res.Message = status == CpSolverStatus.Optimal ? "最优" : "可行(限时)";
            foreach (int i in active)
            {
                int vy = (int)solver.Value(yangVar[i]);
                int vn = (int)solver.Value(yinVar[i]);
                for (int k = 0; k < vy; k++)
                {
                    res.Items.Add((materials[i], Side.Yang));
                    res.TotalPrice += materials[i].Price;
                }
                for (int k = 0; k < vn; k++)
                {
                    res.Items.Add((materials[i], Side.Yin));
                    res.TotalPrice += materials[i].Price;
                }
            }

            // 几何装填子问题（一次尝试；装不下时 Message 注明，后续可加切割）
            sw.Restart();
            var sol = PackItems(materials, yang, yin, res.Items, 500);
            res.PackMs = sw.ElapsedMilliseconds;
            if (sol == null)
            {
                res.Ok = false;
                res.Message = "数量解装不下（需加切割重解）";
                return res;
            }
            res.Ok = true;
            res.Message = (status == CpSolverStatus.Optimal ? "最优" : "可行") + "+装填成功";
            return res;
        }

        /// <summary>把数量方案装填成 Placements；失败返回 null。</summary>
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
