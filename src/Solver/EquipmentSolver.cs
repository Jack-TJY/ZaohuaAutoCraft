using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 炼器：选材（每件材料放阳或阴）+ 装箱。
    /// 与炼丹不同：符文等级只看属性总值、不看摆位，所以没有摆位优化阶段；
    /// 约束是「≥」（多余属性无害），比炼丹的精确凑数宽松。
    /// 阶段 3a：实现「最少材料」「满足最多符文」两种满足型模式；其余模式阶段 3c 补。
    /// </summary>
    public class EquipmentSolver
    {
        class Unit
        {
            public List<int> Mats = new List<int>();

            public Side Side;

            public int[] Contrib;

            public int Size;

            public int Type;

            public int Grade;
        }

        readonly EquipmentRequest req;

        readonly List<Unit> units = new List<Unit>();

        readonly List<List<Cell>> typeShapes = new List<List<Cell>>();

        readonly Dictionary<string, int> typeIds = new Dictionary<string, int>();

        readonly Dictionary<string, bool> packCache = new Dictionary<string, bool>();

        readonly Stopwatch clock = new Stopwatch();

        readonly List<int>[] posUnitsByDim = new List<int>[Attr.Count];

        readonly int[] maxPos = new int[Attr.Count];

        readonly long[] maxReachable = new long[Attr.Count];

        int[] twin;

        long[] caps;

        int minSize;

        RuneEvaluator eval;

        bool timedOut;

        long deadline;

        int combosTried;

        bool anyNoFit;

        int capYang => req.Yang?.Area ?? 0;

        int capYin => req.Yin?.Area ?? 0;

        public int MaxCombosPerDepth = 300;

        public static Action<string> Debug;

        public EquipmentSolver(EquipmentRequest req)
        {
            this.req = req;
        }

        // ───────────────────────── 入口 ─────────────────────────

        public EquipmentSolveResult Solve()
        {
            clock.Restart();
            deadline = req.TimeLimitMs;
            var result = new EquipmentSolveResult();
            eval = new RuneEvaluator(req.Runes, req.Bonus);
            if (req.Runes.Count == 0)
            {
                result.Message = "没有选择符文";
                return Finish(result);
            }
            List<EquipmentSolution> sols;
            switch (req.Mode)
            {
                case EquipmentMode.MinPieces:
                    BuildUnits();
                    sols = SolveCover(TargetOf(req.Runes, _ => 1));
                    break;
                case EquipmentMode.MaxActiveRunes:
                    BuildUnits();
                    sols = SolveMaxActive();
                    break;
                case EquipmentMode.MaxRuneLevel:
                    BuildUnits();
                    sols = SolveCover(MaxLevelTarget());
                    break;
                case EquipmentMode.LowGrade:
                    sols = SolveLowGrade();
                    break;
                case EquipmentMode.MaxGrade:
                case EquipmentMode.MaxAttr:
                    BuildUnits();
                    sols = SolveGreedy(req.Mode);
                    break;
                default:
                    result.Message = "未知模式";
                    return Finish(result);
            }
            foreach (var s in sols)
            {
                Finalize(s);
            }
            result.Solutions = sols;
            result.TimedOut = timedOut;
            if (sols.Count == 0)
            {
                result.Message = anyNoFit ? "属性能凑出，但形状放不进当前锻造锤" : (timedOut ? "超时，未找到摆法" : "现有材料凑不出所选符文");
                result.FoundComboButNoFit = anyNoFit;
            }
            result.CombosTried = combosTried;
            return Finish(result);
        }

        EquipmentSolveResult Finish(EquipmentSolveResult r)
        {
            r.ElapsedMs = clock.ElapsedMilliseconds;
            return r;
        }

        void Finalize(EquipmentSolution s)
        {
            s.MaxGrade = s.Placements.Count == 0 ? 0 : s.Placements.Max(p => p.Material.Grade);
            s.GradeSum = s.Placements.Sum(p => p.Material.Grade);
            s.Price = s.Placements.Sum(p => (long)p.Material.Price);
            s.Runes = eval.Evaluate(s.Placements);
            long b = long.MaxValue;
            foreach (var g in s.Placements.GroupBy(p => p.Material))
            {
                b = Math.Min(b, g.Key.Stock / g.Count());
            }
            s.Batches = s.Placements.Count == 0 ? 0 : (int)Math.Min(int.MaxValue, b);
        }

        // ───────────────────────── 建模 ─────────────────────────

        int TypeOf(List<Cell> shape)
        {
            string key = Packer.GetType(shape).Key;
            if (!typeIds.TryGetValue(key, out int id))
            {
                id = typeShapes.Count;
                typeIds[key] = id;
                typeShapes.Add(shape);
            }
            return id;
        }

        void BuildUnits(int gradeCap = int.MaxValue)
        {
            var mats = req.Materials;
            units.Clear();
            typeShapes.Clear();
            typeIds.Clear();
            packCache.Clear();
            Array.Clear(maxReachable, 0, maxReachable.Length);
            var byKey = new Dictionary<string, Unit>();
            for (int i = 0; i < mats.Count; i++)
            {
                var m = mats[i];
                if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0) || m.Grade > gradeCap)
                {
                    continue;
                }
                for (int d = 0; d < Attr.Count; d++)
                {
                    maxReachable[d] += (long)Math.Abs(m.Attrs[d]) * m.Stock;
                }
                int type = TypeOf(m.Shape);
                foreach (Side side in new[] { Side.Yang, Side.Yin })
                {
                    var grid = side == Side.Yang ? req.Yang : req.Yin;
                    if (grid == null || !Packer.CanFit(grid, m.Shape))
                    {
                        continue;
                    }
                    var contrib = side == Side.Yang ? m.Attrs.ToArray() : m.Attrs.Select(v => -v).ToArray();
                    string key = (int)side + ":" + string.Join(",", contrib) + ":" + type + ":" + m.Grade;
                    if (!byKey.TryGetValue(key, out var unit))
                    {
                        unit = new Unit { Side = side, Contrib = contrib, Size = m.Shape.Count, Type = type, Grade = m.Grade };
                        byKey[key] = unit;
                        units.Add(unit);
                    }
                    unit.Mats.Add(i);
                }
            }
            foreach (var u in units)
            {
                u.Mats = u.Mats.OrderBy(i => mats[i].Price).ToList();
            }
            // 阴阳两侧共用库存（Twin），与炼丹一致
            int n = units.Count;
            twin = Enumerable.Repeat(-1, n).ToArray();
            caps = new long[n];
            for (int u = 0; u < n; u++)
            {
                caps[u] = units[u].Mats.Sum(i => (long)mats[i].Stock);
            }
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++)
                {
                    if (units[a].Side != units[b].Side && units[a].Mats.Intersect(units[b].Mats).Any())
                    {
                        twin[a] = b;
                        caps[a] = Math.Max(caps[a], caps[b]);
                        break;
                    }
                }
            }
            minSize = n == 0 ? 1 : units.Min(u => u.Size);
            Array.Clear(maxPos, 0, maxPos.Length);
            for (int d = 0; d < Attr.Count; d++)
            {
                posUnitsByDim[d] = new List<int>();
                for (int u = 0; u < n; u++)
                {
                    if (units[u].Contrib[d] > 0)
                    {
                        posUnitsByDim[d].Add(u);
                        maxPos[d] = Math.Max(maxPos[d], units[u].Contrib[d]);
                    }
                }
                int dd = d;
                posUnitsByDim[d] = posUnitsByDim[d].OrderByDescending(u => units[u].Contrib[dd]).ThenBy(u => units[u].Size).ToList();
            }
        }

        /// <summary>给定每符文目标等级，算「每个属性至少要到多少」：max(要求 × 等级)。</summary>
        int[] TargetOf(IEnumerable<Rune> runes, Func<Rune, int> levelOf)
        {
            var t = new int[Attr.Count];
            foreach (var r in runes)
            {
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (r.Requirement[d] > 0)
                    {
                        t[d] = Math.Max(t[d], r.Requirement[d] * levelOf(r));
                    }
                }
            }
            return t;
        }

        int[] NeedOf(int[] target)
        {
            var need = new int[Attr.Count];
            for (int d = 0; d < Attr.Count; d++)
            {
                need[d] = Math.Max(0, target[d] - req.Bonus[d]);
            }
            return need;
        }

        long Cap(int u)
        {
            return caps[u];
        }

        bool Over(int[] usage, int u)
        {
            int t = twin[u];
            return usage[u] + (t >= 0 ? usage[t] : 0) > Cap(u);
        }

        int MaxDepth()
        {
            return Math.Min(req.MaxPieces, (capYang + capYin) / Math.Max(1, minSize));
        }

        // ───────────────────────── 组合搜索（覆盖） ─────────────────────────

        string UsageKey(int[] usage)
        {
            return string.Join(",", Enumerable.Range(0, usage.Length).Where(u => usage[u] > 0).Select(u => u + "x" + usage[u]).OrderBy(s => s, StringComparer.Ordinal));
        }

        bool Late()
        {
            if (clock.ElapsedMilliseconds > deadline)
            {
                timedOut = true;
            }
            return timedOut;
        }

        List<int[]> FindCovers(int[] need, int k, int cap)
        {
            var found = new List<int[]>();
            var usage = new int[units.Count];
            var seen = new HashSet<string>();
            CoverDfs(need, k, usage, 0, 0, found, seen, cap);
            combosTried += found.Count;
            Debug?.Invoke($"  k={k} found={found.Count} t={clock.ElapsedMilliseconds}");
            return found;
        }

        void CoverDfs(int[] rem, int left, int[] usage, int areaYang, int areaYin, List<int[]> found, HashSet<string> seen, int cap)
        {
            if (found.Count >= cap || Late())
            {
                return;
            }
            bool covered = true;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (rem[d] > 0)
                {
                    covered = false;
                    break;
                }
            }
            if (covered)
            {
                if (seen.Add(UsageKey(usage)))
                {
                    found.Add((int[])usage.Clone());
                }
                return;
            }
            if (left == 0)
            {
                return;
            }
            // 剩余格子不够再放 left 件最小材料
            if ((long)left * minSize > (capYang - areaYang) + (capYin - areaYin))
            {
                return;
            }
            // 挑候选最少的维度分支（和炼丹一致），并做该维度的件数下界
            int best = -1;
            int bestCount = int.MaxValue;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (rem[d] <= 0)
                {
                    continue;
                }
                if ((long)maxPos[d] * left < rem[d])
                {
                    return;
                }
                if (posUnitsByDim[d].Count < bestCount)
                {
                    best = d;
                    bestCount = posUnitsByDim[d].Count;
                }
            }
            if (best < 0)
            {
                return;
            }
            foreach (int u in posUnitsByDim[best])
            {
                var unit = units[u];
                if (Over(usage, u))
                {
                    continue;
                }
                int ay = areaYang + (unit.Side == Side.Yang ? unit.Size : 0);
                int an = areaYin + (unit.Side == Side.Yin ? unit.Size : 0);
                if (ay > capYang || an > capYin)
                {
                    continue;
                }
                usage[u]++;
                for (int d = 0; d < Attr.Count; d++)
                {
                    rem[d] -= unit.Contrib[d];
                }
                CoverDfs(rem, left - 1, usage, ay, an, found, seen, cap);
                for (int d = 0; d < Attr.Count; d++)
                {
                    rem[d] += unit.Contrib[d];
                }
                usage[u]--;
                if (found.Count >= cap || timedOut)
                {
                    return;
                }
            }
        }

        IComparable[] GradeKey(int[] usage)
        {
            int maxGrade = 0, gradeSum = 0;
            long price = 0;
            for (int u = 0; u < usage.Length; u++)
            {
                if (usage[u] == 0)
                {
                    continue;
                }
                var unit = units[u];
                maxGrade = Math.Max(maxGrade, unit.Grade);
                gradeSum += unit.Grade * usage[u];
                price += (long)req.Materials[unit.Mats[0]].Price * usage[u];
            }
            return new IComparable[] { maxGrade, gradeSum, price };
        }

        static int CompareKeys(IComparable[] a, IComparable[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0)
                {
                    return c;
                }
            }
            return 0;
        }

        // ───────────────────────── 各模式 ─────────────────────────

        /// <summary>覆盖给定目标阈值的最小件数方案；找不到返回空。</summary>
        List<EquipmentSolution> SolveCover(int[] target)
        {
            var need = NeedOf(target);
            // 快速判无解：即使把所有可用材料都用上（每件取对第 d 维最有利的一侧）也到不了目标
            for (int d = 0; d < Attr.Count; d++)
            {
                if (need[d] > 0 && maxReachable[d] < need[d])
                {
                    return new List<EquipmentSolution>();
                }
            }
            if (need.All(v => v == 0))
            {
                // 炉/天赋加成已满足，无需材料
                return new List<EquipmentSolution> { new EquipmentSolution() };
            }
            var sols = new List<EquipmentSolution>();
            var seen = new HashSet<string>();
            for (int k = 1; k <= MaxDepth() && clock.ElapsedMilliseconds < deadline; k++)
            {
                var combos = FindCovers(need, k, MaxCombosPerDepth);
                foreach (var c in combos.OrderBy(c => GradeKey(c), Comparer<IComparable[]>.Create(CompareKeys)))
                {
                    if (sols.Count >= req.TopK || clock.ElapsedMilliseconds > deadline)
                    {
                        break;
                    }
                    var sol = TryPack(c);
                    if (sol == null)
                    {
                        anyNoFit = true;
                        continue;
                    }
                    if (seen.Add(UsageKey(c)))
                    {
                        sols.Add(sol);
                    }
                }
                if (sols.Count > 0)
                {
                    break;
                }
            }
            return sols;
        }

        /// <summary>满足最多符文：先试全部激活，装不下/凑不出就按「难度」从高到低逐个放弃。</summary>
        List<EquipmentSolution> SolveMaxActive()
        {
            var sols = SolveCover(TargetOf(req.Runes, _ => 1));
            if (sols.Count > 0)
            {
                return sols;
            }
            anyNoFit = false;
            var order = req.Runes
                .Select((r, i) => (rune: r, idx: i, hard: r.Requirement.Sum()))
                .OrderByDescending(x => x.hard)
                .ToList();
            for (int drop = 1; drop < order.Count && clock.ElapsedMilliseconds < deadline; drop++)
            {
                anyNoFit = false;
                timedOut = false;
                var active = order.Skip(drop).Select(x => x.rune).ToList();
                if (active.Count == 0)
                {
                    break;
                }
                var s = SolveCover(TargetOf(active, _ => 1));
                if (s.Count > 0)
                {
                    return s;
                }
            }
            return new List<EquipmentSolution>();
        }

        // ───────────────────────── 装箱 ─────────────────────────

        EquipmentSolution TryPack(int[] usage)
        {
            var sol = new EquipmentSolution();
            var left = req.Materials.Select(m => m.Stock).ToArray();
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
                    if (need > 0)
                    {
                        return null; // 阴阳两侧共用库存不够分
                    }
                }
                if (pieces.Count == 0)
                {
                    continue;
                }
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                string key = (int)side + ":" + string.Join(",", pieces.Select(p => TypeOf(p.Shape)).OrderBy(t => t));
                if (packCache.TryGetValue(key, out bool ok) && !ok)
                {
                    return null;
                }
                var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), clock, (int)Math.Max(deadline, clock.ElapsedMilliseconds + 100));
                if (!packed.TimedOut)
                {
                    packCache[key] = packed.Ok;
                }
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

        // ───────────────────────── 其余模式（阶段 3c） ─────────────────────────

        /// <summary>指定符文满级：目标 = 每符文 targetLevel（默认 maxStacks）对应的要求上限。</summary>
        int[] MaxLevelTarget()
        {
            var t = new int[Attr.Count];
            for (int i = 0; i < req.Runes.Count; i++)
            {
                int lv = req.TargetLevel != null && i < req.TargetLevel.Length ? Math.Max(1, req.TargetLevel[i]) : req.Runes[i].MaxStacks;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (req.Runes[i].Requirement[d] > 0)
                    {
                        t[d] = Math.Max(t[d], req.Runes[i].Requirement[d] * lv);
                    }
                }
            }
            return t;
        }

        /// <summary>低阶优先：材料品阶上限从低往高试，第一个能覆盖目标的门槛即解。</summary>
        List<EquipmentSolution> SolveLowGrade()
        {
            var grades = req.Materials.Where(m => m.Stock > 0 && m.Shape.Count > 0 && m.Attrs.Any(v => v != 0)).Select(m => m.Grade).Distinct().OrderBy(g => g).ToList();
            var target = TargetOf(req.Runes, _ => 1);
            for (int i = 0; i < grades.Count; i++)
            {
                int g = grades[i];
                anyNoFit = false;
                timedOut = false;
                // 时间切片：每档分到均等时间，最后一档用剩余全部
                long remain = deadline - clock.ElapsedMilliseconds;
                long until = i == grades.Count - 1 ? deadline : clock.ElapsedMilliseconds + Math.Max(50, remain / (grades.Count - i));
                long saved = deadline;
                deadline = until;
                BuildUnits(g);
                var sols = SolveCover(target);
                deadline = saved;
                if (sols.Count > 0)
                {
                    return sols;
                }
                if (clock.ElapsedMilliseconds > deadline)
                {
                    break;
                }
            }
            return new List<EquipmentSolution>();
        }

        /// <summary>最高品阶 / 指定属性最大：贪心按「每格收益」选材料，选满后装箱，装不下就丢最低收益的再试。</summary>
        List<EquipmentSolution> SolveGreedy(EquipmentMode mode)
        {
            // 基座：最高品阶先要一个「全符文至少 1 级」的最少材料解，再往上加料冲等级；
            // 指定属性最大从空开始（只要对目标属性有正贡献的材料）。
            var baseItems = new List<(Material mat, Side side)>();
            var attrs = (int[])req.Bonus.Clone();
            if (mode == EquipmentMode.MaxGrade)
            {
                var baseSols = SolveCover(TargetOf(req.Runes, _ => 1));
                if (baseSols.Count == 0)
                {
                    return baseSols;
                }
                foreach (var p in baseSols[0].Placements)
                {
                    baseItems.Add((p.Material, p.Side));
                    int sgn = p.Side == Side.Yang ? 1 : -1;
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        attrs[d] += sgn * p.Material.Attrs[d];
                    }
                }
            }
            var stockLeft = req.Materials.ToDictionary(m => m, m => m.Stock);
            foreach (var it in baseItems)
            {
                stockLeft[it.mat]--;
            }
            var area = new int[3];
            foreach (var it in baseItems)
            {
                area[(int)it.side] += it.mat.Shape.Count;
            }

            var additions = new List<(Material mat, Side side, double gain)>();
            int targetAttr = mode == EquipmentMode.MaxAttr ? Math.Max(0, req.TargetAttr) : 0;
            while (clock.ElapsedMilliseconds < deadline)
            {
                double baseScore = mode == EquipmentMode.MaxGrade ? eval.Evaluate(attrs).GradeScore : 0;
                double bestGain = 0;
                Material bestMat = null;
                Side bestSide = Side.Yang;
                foreach (var m in req.Materials)
                {
                    if (stockLeft[m] <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                    {
                        continue;
                    }
                    foreach (Side side in new[] { Side.Yang, Side.Yin })
                    {
                        var grid = side == Side.Yang ? req.Yang : req.Yin;
                        if (grid == null || !Packer.CanFit(grid, m.Shape) || area[(int)side] + m.Shape.Count > grid.Area)
                        {
                            continue;
                        }
                        int sgn = side == Side.Yang ? 1 : -1;
                        double gain;
                        if (mode == EquipmentMode.MaxAttr)
                        {
                            gain = sgn * m.Attrs[targetAttr];
                        }
                        else
                        {
                            var na = (int[])attrs.Clone();
                            for (int d = 0; d < Attr.Count; d++)
                            {
                                na[d] += sgn * m.Attrs[d];
                            }
                            gain = eval.Evaluate(na).GradeScore - baseScore;
                        }
                        if (gain > bestGain)
                        {
                            bestGain = gain;
                            bestMat = m;
                            bestSide = side;
                        }
                    }
                }
                if (bestMat == null || bestGain <= 0)
                {
                    break;
                }
                additions.Add((bestMat, bestSide, bestGain));
                stockLeft[bestMat]--;
                area[(int)bestSide] += bestMat.Shape.Count;
                int s = bestSide == Side.Yang ? 1 : -1;
                for (int d = 0; d < Attr.Count; d++)
                {
                    attrs[d] += s * bestMat.Attrs[d];
                }
            }

            // 装箱：基座 + 按收益降序逐个试加增量，装不下的放弃
            var finalItems = new List<(Material mat, Side side)>(baseItems);
            foreach (var a in additions.OrderByDescending(a => a.gain))
            {
                finalItems.Add((a.mat, a.side));
                if (PackItems(finalItems) == null)
                {
                    finalItems.RemoveAt(finalItems.Count - 1);
                }
            }
            var packed = PackItems(finalItems);
            var result = new List<EquipmentSolution>();
            if (packed != null)
            {
                result.Add(packed);
            }
            else
            {
                anyNoFit = true;
            }
            return result;
        }

        EquipmentSolution PackItems(List<(Material mat, Side side)> items)
        {
            var sol = new EquipmentSolution();
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var pieces = items.Where(x => x.side == side).Select(x => x.mat).ToList();
                if (pieces.Count == 0)
                {
                    continue;
                }
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), clock, (int)Math.Max(deadline, clock.ElapsedMilliseconds + 200));
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

        /// <summary>独立复核：按游戏规则重算属性和符文等级，与方案里的结算对比；同时查越界/重叠/库存。</summary>
        public static string Verify(EquipmentRequest req, EquipmentSolution sol)
        {
            var eval = new RuneEvaluator(req.Runes, req.Bonus);
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                if (grid == null)
                {
                    continue;
                }
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
                }
            }
            foreach (var g in sol.Placements.GroupBy(p => p.Material))
            {
                if (g.LongCount() > g.Key.Stock)
                {
                    return $"{g.Key.Name} 库存不足";
                }
            }
            var expect = eval.Evaluate(sol.Placements);
            if (sol.Runes == null || sol.Runes.ActiveCount != expect.ActiveCount || sol.Runes.GradeScore != expect.GradeScore)
            {
                return "符文结算与复核不一致";
            }
            for (int i = 0; i < expect.Level.Length; i++)
            {
                if (sol.Runes.Level[i] != expect.Level[i])
                {
                    return $"符文 {req.Runes[i].Id} 等级不一致";
                }
            }
            return null;
        }
    }
}
