using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 炼丹：选材（阳加阴减后正好等于 Need）+ 装箱。
    /// 选材按件数从少到多逐层加深，同一层内枚举所有组合，再按品阶、价格排序后依次尝试装箱。
    /// </summary>
    public class DrugSolver
    {
        // 属性完全相同（同一侧）的材料归成一类，搜索时只看类，装箱时再挑具体材料
        class Unit
        {
            public List<int> Mats = new List<int>();

            public Side Side;

            public int[] Contrib;

            /// <summary>类内最小占格，用于容量的乐观估计。</summary>
            public int Size;

            public int Stock;

            public bool Pure;
        }

        readonly DrugRequest req;

        List<Unit> units = new List<Unit>();

        List<Unit> allUnits = new List<Unit>();

        readonly Stopwatch clock = new Stopwatch();

        // 每个属性维度上，能把余量往正 / 负方向推的单元
        List<int>[] posUnits, negUnits;

        int[] maxPos, maxNeg;

        bool timedOut;

        long passLimitMs;

        double maxEff;

        int minSize;

        // 单维下界表：只看第 d 维，凑出余量 v 至少要几件（忽略库存与其它维）。-1 表示凑不出
        int[][] dist1d;

        int[] band;

        public int MaxCombosPerDepth = 200;

        public DrugSolver(DrugRequest req)
        {
            this.req = req;
        }

        public SolveResult Solve()
        {
            clock.Restart();
            var result = new SolveResult();
            BuildUnits();
            int capYang = req.Yang?.Area ?? 0, capYin = req.Yin?.Area ?? 0;
            if (req.Need.All(v => v == 0))
            {
                result.Message = "丹炉加成已经满足丹方，无需材料";
                result.Best = new Solution();
                return Finish(result);
            }
            int combos = 0;
            // 第一轮只用属性都在丹方范围内的材料（快）；凑不出再放开全部材料（可借无关属性互相抵消）
            var passes = new List<(List<Unit> set, int limitMs)>
            {
                (allUnits.Where(u => u.Pure).ToList(), req.TimeLimitMs * 6 / 10),
                (allUnits, req.TimeLimitMs)
            };
            foreach (var pass in passes)
            {
                if (pass.set.Count == 0 || (pass.set.Count == units.Count && units.Count > 0))
                {
                    continue;
                }
                SetUnits(pass.set);
                passLimitMs = pass.limitMs;
                timedOut = false;
                int maxK = Math.Min(req.MaxPieces, (capYang + capYin) / Math.Max(1, minSize));
                for (int k = 1; k <= maxK && !timedOut; k++)
                {
                    var found = new List<int[]>();
                    var usage = new int[units.Count];
                    var seen = new HashSet<string>();
                    Dfs(req.Need.ToArray(), k, usage, 0, 0, capYang, capYin, found, seen);
                    combos += found.Count;
                    foreach (var combo in found.Select(Score).OrderBy(s => s.maxGrade).ThenBy(s => s.gradeSum).ThenBy(s => s.price))
                    {
                        var sol = TryPack(combo.usage);
                        if (sol != null)
                        {
                            result.Best = sol;
                            result.CombosTried = combos;
                            return Finish(result);
                        }
                        result.FoundComboButNoFit = true;
                        if (clock.ElapsedMilliseconds > req.TimeLimitMs)
                        {
                            timedOut = true;
                            break;
                        }
                    }
                }
            }
            result.CombosTried = combos;
            result.TimedOut = timedOut;
            result.Message = result.FoundComboButNoFit ? "属性能凑出，但形状放不进当前丹炉" : (timedOut ? "超时，未找到摆法" : "现有材料凑不出该丹方");
            return Finish(result);
        }

        SolveResult Finish(SolveResult r)
        {
            r.ElapsedMs = clock.ElapsedMilliseconds;
            r.TimedOut |= timedOut;
            return r;
        }

        void BuildUnits()
        {
            var mats = req.Materials;
            var needDims = Enumerable.Range(0, Attr.Count).Where(d => req.Need[d] != 0).ToList();
            var byKey = new Dictionary<string, Unit>();
            for (int i = 0; i < mats.Count; i++)
            {
                var m = mats[i];
                long cap = m.Stock / Math.Max(1, req.Batch);
                if (cap <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                {
                    continue;
                }
                bool pure = Enumerable.Range(0, Attr.Count).All(d => m.Attrs[d] == 0 || needDims.Contains(d));
                foreach (Side side in new[] { Side.Yang, Side.Yin })
                {
                    var grid = side == Side.Yang ? req.Yang : req.Yin;
                    if (grid == null || !Packer.CanFit(grid, m.Shape))
                    {
                        continue;
                    }
                    var contrib = side == Side.Yang ? m.Attrs.ToArray() : m.Attrs.Select(v => -v).ToArray();
                    string key = (int)side + ":" + string.Join(",", contrib);
                    if (!byKey.TryGetValue(key, out var unit))
                    {
                        unit = new Unit { Side = side, Contrib = contrib, Size = int.MaxValue, Pure = pure };
                        byKey[key] = unit;
                        allUnits.Add(unit);
                    }
                    unit.Mats.Add(i);
                    unit.Size = Math.Min(unit.Size, m.Shape.Count);
                    unit.Stock = (int)Math.Min(int.MaxValue, unit.Stock + cap);
                }
            }
            // 类内材料按偏好排序：低阶、便宜、小块在前
            foreach (var u in allUnits)
            {
                u.Mats = u.Mats.OrderBy(i => mats[i].Grade).ThenBy(i => mats[i].Price).ThenBy(i => mats[i].Shape.Count).ToList();
            }
        }

        void SetUnits(List<Unit> set)
        {
            units = set;
            maxEff = 0;
            minSize = set.Count == 0 ? 1 : set.Min(u => u.Size);
            posUnits = new List<int>[Attr.Count];
            negUnits = new List<int>[Attr.Count];
            maxPos = new int[Attr.Count];
            maxNeg = new int[Attr.Count];
            for (int d = 0; d < Attr.Count; d++)
            {
                posUnits[d] = new List<int>();
                negUnits[d] = new List<int>();
                for (int u = 0; u < units.Count; u++)
                {
                    int c = units[u].Contrib[d];
                    if (c > 0)
                    {
                        posUnits[d].Add(u);
                        maxPos[d] = Math.Max(maxPos[d], c);
                    }
                    else if (c < 0)
                    {
                        negUnits[d].Add(u);
                        maxNeg[d] = Math.Max(maxNeg[d], -c);
                    }
                }
                // 大步长优先，能更快凑近（排序放在 maxEff 之后也无妨）
                posUnits[d] = posUnits[d].OrderByDescending(u => units[u].Contrib[d]).ThenBy(u => units[u].Size).ToList();
                negUnits[d] = negUnits[d].OrderBy(u => units[u].Contrib[d]).ThenBy(u => units[u].Size).ToList();
            }
            foreach (var u in units)
            {
                double eff = 0;
                for (int d = 0; d < Attr.Count; d++)
                {
                    int c = u.Contrib[d];
                    if (c > 0)
                    {
                        eff += (double)c / maxPos[d];
                    }
                    else if (c < 0)
                    {
                        eff += (double)-c / maxNeg[d];
                    }
                }
                maxEff = Math.Max(maxEff, eff);
            }
            BuildDistTables();
        }

        void BuildDistTables()
        {
            dist1d = new int[Attr.Count][];
            band = new int[Attr.Count];
            for (int d = 0; d < Attr.Count; d++)
            {
                var coins = units.Select(u => u.Contrib[d]).Where(c => c != 0).Distinct().ToArray();
                int maxCoin = Math.Max(maxPos[d], maxNeg[d]);
                int b = Math.Abs(req.Need[d]) + 2 * maxCoin;
                if (b > 200000)
                {
                    continue;
                }
                band[d] = b;
                var dist = new int[2 * b + 1];
                for (int i = 0; i < dist.Length; i++)
                {
                    dist[i] = -1;
                }
                dist[b] = 0;
                var q = new Queue<int>();
                q.Enqueue(0);
                while (q.Count > 0)
                {
                    int v = q.Dequeue();
                    int dv = dist[v + b];
                    foreach (int c in coins)
                    {
                        int w = v + c;
                        if (w < -b || w > b || dist[w + b] >= 0)
                        {
                            continue;
                        }
                        dist[w + b] = dv + 1;
                        q.Enqueue(w);
                    }
                }
                dist1d[d] = dist;
            }
        }

        void Dfs(int[] r, int left, int[] usage, int areaYang, int areaYin, int capYang, int capYin, List<int[]> found, HashSet<string> seen)
        {
            if (timedOut || found.Count >= MaxCombosPerDepth)
            {
                return;
            }
            if (clock.ElapsedMilliseconds > passLimitMs)
            {
                timedOut = true;
                return;
            }
            bool zero = true;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (r[d] != 0)
                {
                    zero = false;
                    break;
                }
            }
            if (zero)
            {
                if (left == 0)
                {
                    found.Add(usage.ToArray());
                }
                return;
            }
            if (left == 0)
            {
                return;
            }
            // 剩余格子不够再放 left 件最小的材料
            if ((long)left * minSize > (capYang - areaYang) + (capYin - areaYin))
            {
                return;
            }
            // 界：每个维度剩余件数推得动余量；同时挑候选最少的维度分支
            int best = -1, bestCount = int.MaxValue;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (r[d] > 0)
                {
                    if ((long)maxPos[d] * left < r[d])
                    {
                        return;
                    }
                    if (posUnits[d].Count < bestCount)
                    {
                        best = d;
                        bestCount = posUnits[d].Count;
                    }
                }
                else if (r[d] < 0)
                {
                    if ((long)maxNeg[d] * left < -r[d])
                    {
                        return;
                    }
                    if (negUnits[d].Count < bestCount)
                    {
                        best = d;
                        bestCount = negUnits[d].Count;
                    }
                }
            }
            // 势函数下界：Σ|余量|/单步最大值，每件材料最多减少 maxEff
            double pot = 0;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (r[d] > 0)
                {
                    pot += (double)r[d] / maxPos[d];
                }
                else if (r[d] < 0)
                {
                    pot += (double)-r[d] / maxNeg[d];
                }
            }
            if (pot > maxEff * left + 1e-9)
            {
                return;
            }
            for (int d = 0; d < Attr.Count; d++)
            {
                var dist = dist1d[d];
                int b = band[d];
                if (r[d] == 0 || dist == null || Math.Abs(r[d]) > b - Math.Max(maxPos[d], maxNeg[d]))
                {
                    continue;
                }
                int need = dist[r[d] + b];
                if (need < 0 || need > left)
                {
                    return;
                }
            }
            var cands = r[best] > 0 ? posUnits[best] : negUnits[best];
            foreach (int u in cands)
            {
                var unit = units[u];
                if (usage[u] >= unit.Stock)
                {
                    continue;
                }
                int ay = areaYang, an = areaYin;
                if (unit.Side == Side.Yang)
                {
                    ay += unit.Size;
                }
                else
                {
                    an += unit.Size;
                }
                if (ay > capYang || an > capYin)
                {
                    continue;
                }
                usage[u]++;
                string key = string.Join(",", usage);
                if (seen.Add(key))
                {
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        r[d] -= unit.Contrib[d];
                    }
                    Dfs(r, left - 1, usage, ay, an, capYang, capYin, found, seen);
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        r[d] += unit.Contrib[d];
                    }
                }
                usage[u]--;
                if (timedOut || found.Count >= MaxCombosPerDepth)
                {
                    return;
                }
            }
        }

        (int[] usage, int maxGrade, int gradeSum, long price) Score(int[] usage)
        {
            int maxGrade = 0, gradeSum = 0;
            long price = 0;
            for (int u = 0; u < usage.Length; u++)
            {
                if (usage[u] == 0)
                {
                    continue;
                }
                var m = req.Materials[units[u].Mats[0]];
                maxGrade = Math.Max(maxGrade, m.Grade);
                gradeSum += m.Grade * usage[u];
                price += (long)m.Price * usage[u];
            }
            return (usage, maxGrade, gradeSum, price);
        }

        Solution TryPack(int[] usage)
        {
            var sol = new Solution();
            var left = req.Materials.Select(m => m.Stock / Math.Max(1, req.Batch)).ToArray();
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var pieces = new List<Material>();
                for (int u = 0; u < usage.Length; u++)
                {
                    if (units[u].Side != side)
                    {
                        continue;
                    }
                    int need = usage[u];
                    foreach (int mi in units[u].Mats)
                    {
                        while (need > 0 && left[mi] > 0)
                        {
                            pieces.Add(req.Materials[mi]);
                            left[mi]--;
                            need--;
                        }
                    }
                }
                if (pieces.Count == 0)
                {
                    continue;
                }
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), clock, req.TimeLimitMs);
                if (!packed.Ok)
                {
                    timedOut |= packed.TimedOut;
                    return null;
                }
                for (int i = 0; i < pieces.Count; i++)
                {
                    var s = packed.Spots[i];
                    sol.Placements.Add(new Placement { Material = pieces[i], Side = side, X = s.x, Y = s.y, Rotate = s.rotate });
                }
            }
            sol.MaxGrade = sol.Placements.Count == 0 ? 0 : sol.Placements.Max(p => p.Material.Grade);
            sol.GradeSum = sol.Placements.Sum(p => p.Material.Grade);
            sol.Price = sol.Placements.Sum(p => (long)p.Material.Price);
            return sol;
        }

        /// <summary>独立复核：按游戏规则重算属性和占格，测试与插件都用它兜底。</summary>
        public static string Verify(DrugRequest req, Solution sol)
        {
            var sum = new int[Attr.Count];
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                var used = new HashSet<(int, int)>();
                foreach (var p in sol.Placements.Where(p => p.Side == side))
                {
                    foreach (var c in p.Material.Shape)
                    {
                        var r = Packer.Rotate(c, p.Rotate);
                        int x = r.X + p.X, y = r.Y + p.Y;
                        if (x < grid.MinX || y < grid.MinY || x >= grid.MinX + grid.Width || y >= grid.MinY + grid.Height)
                        {
                            return $"{p.Material.Name} 越界 ({x},{y})";
                        }
                        if (!used.Add((x, y)))
                        {
                            return $"{p.Material.Name} 重叠 ({x},{y})";
                        }
                    }
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        sum[d] += side == Side.Yang ? p.Material.Attrs[d] : -p.Material.Attrs[d];
                    }
                }
            }
            for (int d = 0; d < Attr.Count; d++)
            {
                if (sum[d] != req.Need[d])
                {
                    return $"属性{Attr.Names[d]}为 {sum[d]}，应为 {req.Need[d]}";
                }
            }
            foreach (var g in sol.Placements.GroupBy(p => p.Material))
            {
                if (g.LongCount() * req.Batch > g.Key.Stock)
                {
                    return $"{g.Key.Name} 库存不足";
                }
            }
            return null;
        }
    }
}
