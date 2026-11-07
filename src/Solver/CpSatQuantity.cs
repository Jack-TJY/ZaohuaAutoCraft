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
        /// <summary>返回阳/阴件数方案（材料引用 + 侧）；无解或超时返回 null。</summary>
        public static List<(Material mat, Side side)> Solve(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec)
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

            model.Minimize(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yangVar[i], materials[i].Price) + LinearExpr.Term(yinVar[i], materials[i].Price))));

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
    }
}
