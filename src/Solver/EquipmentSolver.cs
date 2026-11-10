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

        /// <summary>本次求解走的主路径（CP-SAT / 启发式），用于状态报告。</summary>
        string solvePath = "";

        bool anyNoFit;

        /// <summary>是否为「原始问题确有不可行证明」（报告 §4.2）：只有无 cuts 的 CP-SAT 返回 Infeasible 才置位。</summary>
        bool provenInfeasible;

        /// <summary>当前方案未证明最优（CP-SAT 返回 Feasible 而非 Optimal，报告 §4.2）。</summary>
        bool notProvenOptimal;

        /// <summary>装填结果（结构化）：报告 §4.1，避免 null 混淆所有失败。</summary>
        class PackOutcome
        {
            public EquipmentSolution Solution;
            public bool Failed;
            public bool ProvenImpossible;
            public Side FailedSide;
            public Dictionary<string, int> FailCounts;
        }

        /// <summary>同形状多重集 → 装填结果缓存（报告 §6.2）。</summary>
        readonly Dictionary<string, Packer.Result> packResultCache = new Dictionary<string, Packer.Result>();

        int packResultHits;

        int packResultMisses;

        /// <summary>每轮搜索记录（报告 §15）。</summary>
        readonly List<string> roundLog = new List<string>();

        int capYang => req.Yang?.Area ?? 0;

        int capYin => req.Yin?.Area ?? 0;

        public int MaxCombosPerDepth = 1200;

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
            sols = SolveSatisfy(req.Mode);
            foreach (var s in sols)
            {
                Finalize(s);
            }
            result.Solutions = sols;
            result.TimedOut = timedOut;
            if (sols.Count == 0)
            {
                result.Message = (anyNoFit ? "属性能凑出，但形状放不进当前锻造锤" : (provenInfeasible ? "当前条件下无可用摆法" : "限时内未找到可用摆法（未证明无解）")) + $"\n[CP-SAT诊断] {CpSatQuantity.LastLog}";
                result.FoundComboButNoFit = anyNoFit;
            }
            result.Status = sols.Count > 0
                ? ((string.IsNullOrEmpty(solvePath) ? "启发式" : solvePath) + (notProvenOptimal ? "（尚未证明最优）" : ""))
                : (provenInfeasible ? "已证无解" : "未定（限时内未找到/未证明）");
            result.CombosTried = combosTried;
            result.RoundLog = string.Join(" | ", roundLog) + $" | 装填缓存命中{packResultHits}/未命中{packResultMisses}";
            return Finish(result);
        }

        EquipmentSolveResult Finish(EquipmentSolveResult r)
        {
            r.ElapsedMs = clock.ElapsedMilliseconds;
            return r;
        }

        /// <summary>凑不出时的缺口诊断：理论最大属性（正放阳负放阴，忽略面积）不足以满足哪个符文的哪一维。</summary>
        string ShortfallText()
        {
            try
            {
                var max = (int[])req.Bonus.Clone();
                foreach (var m in req.Materials)
                {
                    if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                    {
                        continue;
                    }
                    long stock = Math.Min(m.Stock, 100000L);
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        int a = m.Attrs[d];
                        if (a > 0)
                        {
                            max[d] += (int)(a * stock);
                        }
                        else if (a < 0)
                        {
                            max[d] += (int)(-a * stock);
                        }
                    }
                }
                var shorts = new List<string>();
                foreach (var r in req.Runes)
                {
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        int need = r.Requirement[d];
                        if (need <= 0)
                        {
                            continue;
                        }
                        if (max[d] < need)
                        {
                            shorts.Add($"{Attr.Names[d]}缺{need - max[d]}");
                            break;
                        }
                    }
                }
                return shorts.Count > 0 ? "；缺口：" + string.Join("、", shorts) : "";
            }
            catch
            {
                return "";
            }
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

        void BuildUnits(int gradeCap = int.MaxValue, int gradeFloor = int.MinValue)
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
                if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0) || m.Grade > gradeCap || m.Grade < gradeFloor)
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
                need[d] = target[d] - req.Bonus[d];
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

        List<int[]> FindCovers(bool[] required, int[] need, int k, int cap)
        {
            var found = new List<int[]>();
            var usage = new int[units.Count];
            var seen = new HashSet<string>();
            CoverDfs(required, need, k, usage, 0, 0, found, seen, cap);
            combosTried += found.Count;
            Debug?.Invoke($"  k={k} found={found.Count} t={clock.ElapsedMilliseconds}");
            return found;
        }

        void CoverDfs(bool[] required, int[] rem, int left, int[] usage, int areaYang, int areaYin, List<int[]> found, HashSet<string> seen, int cap)
        {
            if (found.Count >= cap || Late())
            {
                return;
            }
            bool covered = true;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (required[d] && rem[d] > 0)
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
                if (!required[d] || rem[d] <= 0)
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
                CoverDfs(required, rem, left - 1, usage, ay, an, found, seen, cap);
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
            var required = new bool[Attr.Count];
            for (int d = 0; d < Attr.Count; d++)
            {
                required[d] = target[d] > 0;
            }
            // 快速判无解：即使把所有可用材料都用上（每件取对第 d 维最有利的一侧）也到不了目标
            for (int d = 0; d < Attr.Count; d++)
            {
                if (required[d] && need[d] > 0 && maxReachable[d] < need[d])
                {
                    return new List<EquipmentSolution>();
                }
            }
            bool allCovered = true;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (required[d] && need[d] > 0)
                {
                    allCovered = false;
                    break;
                }
            }
            if (allCovered)
            {
                // 炉/天赋加成已满足，无需材料
                return new List<EquipmentSolution> { new EquipmentSolution() };
            }
            var sols = new List<EquipmentSolution>();
            var seen = new HashSet<string>();
            for (int k = 1; k <= MaxDepth() && clock.ElapsedMilliseconds < deadline; k++)
            {
                var combos = FindCovers(required, need, k, MaxCombosPerDepth);
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

        /// <summary>模式专属次要收益（越大越好），用于提级选材的平局裁决。</summary>
        long TieOf(EquipmentMode mode, int[] oldAttrs, int[] newAttrs, RuneResult oldR, RuneResult newR, Material m)
        {
            switch (mode)
            {
                case EquipmentMode.MaxAttr:
                {
                    int ta = Math.Max(0, req.TargetAttr);
                    return newAttrs[ta] - oldAttrs[ta];
                }
                case EquipmentMode.MaxBatches:
                    return -m.Shape.Count;
                case EquipmentMode.LowGrade:
                    return -m.Grade;
                default:
                    return -m.Shape.Count;
            }
        }

        /// <summary>从基座贪心加料提级（数量>等级>模式收益），生成基座与逐步提级的候选解。</summary>
        List<EquipmentSolution> RaiseLevels(EquipmentSolution baseSol, EquipmentMode mode)
        {
            var baseItems = baseSol.Placements.Select(p => (mat: p.Material, side: p.Side)).ToList();
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
            var attrs = eval.SumAttrs(baseSol.Placements);
            var cur = eval.Evaluate(attrs);

            var additions = new List<(Material mat, Side side, int dc, int dl, int dp, long tie)>();
            while (clock.ElapsedMilliseconds < deadline)
            {
                Material bestMat = null;
                Side bestSide = Side.Yang;
                int bdc = 0, bdl = 0, bdp = 0;
                long btie = 0;
                bool hasBest = false;
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
                        var na = (int[])attrs.Clone();
                        for (int d = 0; d < Attr.Count; d++)
                        {
                            na[d] += sgn * m.Attrs[d];
                        }
                        var nr = eval.Evaluate(na);
                        int dc = nr.ActiveCount - cur.ActiveCount;
                        int dl = nr.TotalLevel - cur.TotalLevel;
                        int dp = 0;
                        if (mode == EquipmentMode.MaxRuneLevel)
                        {
                            for (int i = 0; i < req.Runes.Count; i++)
                            {
                                if (req.Priority != null && i < req.Priority.Length && req.Priority[i])
                                {
                                    dp += nr.Level[i] - cur.Level[i];
                                }
                            }
                        }
                        if (mode == EquipmentMode.MaxRuneLevel)
                        {
                            if (dp < 0 || (dp == 0 && (dc < 0 || (dc == 0 && dl <= 0))))
                            {
                                continue;
                            }
                        }
                        else if (dc < 0 || (dc == 0 && dl <= 0))
                        {
                            continue;
                        }
                        long tie = TieOf(mode, attrs, na, cur, nr, m);
                        bool better;
                        if (!hasBest)
                        {
                            better = true;
                        }
                        else if (mode == EquipmentMode.MaxRuneLevel)
                        {
                            better = dp > bdp || (dp == bdp && (dc > bdc || (dc == bdc && (dl > bdl || (dl == bdl && tie > btie)))));
                        }
                        else
                        {
                            better = dc > bdc || (dc == bdc && (dl > bdl || (dl == bdl && tie > btie)));
                        }
                        if (better)
                        {
                            hasBest = true;
                            bdc = dc;
                            bdl = dl;
                            bdp = dp;
                            btie = tie;
                            bestMat = m;
                            bestSide = side;
                        }
                    }
                }
                if (!hasBest)
                {
                    break;
                }
                additions.Add((bestMat, bestSide, bdc, bdl, bdp, btie));
                stockLeft[bestMat]--;
                area[(int)bestSide] += bestMat.Shape.Count;
                int s2 = bestSide == Side.Yang ? 1 : -1;
                for (int d = 0; d < Attr.Count; d++)
                {
                    attrs[d] += s2 * bestMat.Attrs[d];
                }
                cur = eval.Evaluate(attrs);
            }

            var results = new List<EquipmentSolution>();
            var items = new List<(Material mat, Side side)>(baseItems);
            var pb = PackItems(items);
            if (pb == null)
            {
                return results;
            }
            results.Add(pb);
            foreach (var a in additions.OrderByDescending(a => a.dp).ThenByDescending(a => a.dc).ThenByDescending(a => a.dl).ThenByDescending(a => a.tie))
            {
                items.Add((a.mat, a.side));
                var p = PackItems(items);
                if (p == null)
                {
                    items.RemoveAt(items.Count - 1);
                    continue;
                }
                results.Add(p);
            }
            return results;
        }

        /// <summary>给属性向量加上一件材料（阳加阴减）后的新向量。</summary>
        int[] AddTo(int[] attrs, Material m, Side side)
        {
            var na = (int[])attrs.Clone();
            int sgn = side == Side.Yang ? 1 : -1;
            for (int d = 0; d < Attr.Count; d++)
            {
                na[d] += sgn * m.Attrs[d];
            }
            return na;
        }

        /// <summary>找一件能提升目标属性、且放得下的材料（收益最高）。</summary>
        (Material mat, Side side) FindTargetMat(int ta, int[] attrs, Dictionary<Material, long> stock, int[] area)
        {
            Material best = null;
            Side bestSide = Side.Yang;
            int bestGain = 0;
            foreach (var m in req.Materials)
            {
                if (stock.TryGetValue(m, out long s) && s <= 0)
                {
                    continue;
                }
                if (m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
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
                    int gain = sgn * m.Attrs[ta];
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        best = m;
                        bestSide = side;
                    }
                }
            }
            return (best, bestSide);
        }

        /// <summary>找一件能提升指定符文等级、且放得下的材料（优先小块）。</summary>
        (Material mat, Side side) FindRaiseMat(int runeIdx, int[] attrs, Dictionary<Material, long> stock, int[] area)
        {
            var cur = eval.Evaluate(attrs);
            int curLv = cur.Level[runeIdx];
            Material best = null;
            Side bestSide = Side.Yang;
            int bestSize = int.MaxValue;
            foreach (var m in req.Materials)
            {
                if (stock.TryGetValue(m, out long s) && s <= 0)
                {
                    continue;
                }
                if (m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
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
                    var na = AddTo(attrs, m, side);
                    if (eval.Evaluate(na).Level[runeIdx] > curLv && m.Shape.Count < bestSize)
                    {
                        bestSize = m.Shape.Count;
                        best = m;
                        bestSide = side;
                    }
                }
            }
            return (best, bestSide);
        }

        /// <summary>由材料+侧列表算属性总值（含炉加成）。</summary>
        int[] AttrsOf(List<(Material mat, Side side)> items)
        {
            var attrs = (int[])req.Bonus.Clone();
            foreach (var it in items)
            {
                int sgn = it.side == Side.Yang ? 1 : -1;
                for (int d = 0; d < Attr.Count; d++)
                {
                    attrs[d] += sgn * it.mat.Attrs[d];
                }
            }
            return attrs;
        }

        /// <summary>最大属性提级一步：先满足「目标属性最大」，再逐级提等级；每提一级确认目标仍最大，否则补目标材料，补不了则回退。返回是否推进。</summary>
        bool StepMaxAttr(List<(Material mat, Side side)> items, Dictionary<Material, long> stockLeft, int[] area, ref int[] attrs, int ta)
        {
            var cur = eval.Evaluate(attrs);
            if (RuneEvaluator.ItemAttrOf(attrs) != ta)
            {
                var t = FindTargetMat(ta, attrs, stockLeft, area);
                if (t.mat != null)
                {
                    items.Add((t.mat, t.side));
                    stockLeft[t.mat]--;
                    area[(int)t.side] += t.mat.Shape.Count;
                    attrs = AddTo(attrs, t.mat, t.side);
                    return true;
                }
            }
            else
            {
                for (int i = 0; i < req.Runes.Count; i++)
                {
                    if (cur.Level[i] >= req.Runes[i].MaxStacks)
                    {
                        continue;
                    }
                    var r = FindRaiseMat(i, attrs, stockLeft, area);
                    if (r.mat == null)
                    {
                        continue;
                    }
                    var na = AddTo(attrs, r.mat, r.side);
                    if (RuneEvaluator.ItemAttrOf(na) == ta)
                    {
                        items.Add((r.mat, r.side));
                        stockLeft[r.mat]--;
                        area[(int)r.side] += r.mat.Shape.Count;
                        attrs = na;
                        return true;
                    }
                    var stockCopy = new Dictionary<Material, long>(stockLeft);
                    stockCopy[r.mat]--;
                    var areaCopy = (int[])area.Clone();
                    areaCopy[(int)r.side] += r.mat.Shape.Count;
                    var c = FindTargetMat(ta, na, stockCopy, areaCopy);
                    if (c.mat != null)
                    {
                        items.Add((r.mat, r.side));
                        stockLeft[r.mat]--;
                        area[(int)r.side] += r.mat.Shape.Count;
                        items.Add((c.mat, c.side));
                        stockLeft[c.mat]--;
                        area[(int)c.side] += c.mat.Shape.Count;
                        attrs = AddTo(AddTo(attrs, r.mat, r.side), c.mat, c.side);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>最大属性专用提级 + 结果复查。</summary>
        List<EquipmentSolution> RaiseMaxAttr(EquipmentSolution baseSol)
        {
            int ta = Math.Max(0, req.TargetAttr);
            var items = baseSol.Placements.Select(p => (mat: p.Material, side: p.Side)).ToList();
            var stockLeft = req.Materials.ToDictionary(m => m, m => m.Stock);
            foreach (var it in items)
            {
                stockLeft[it.mat]--;
            }
            var area = new int[3];
            foreach (var it in items)
            {
                area[(int)it.side] += it.mat.Shape.Count;
            }
            var attrs = eval.SumAttrs(baseSol.Placements);

            var results = new List<EquipmentSolution>();
            void Record()
            {
                var p = PackItems(items);
                if (p != null && RuneEvaluator.ItemAttrOf(eval.SumAttrs(p.Placements)) == ta)
                {
                    results.Add(p);
                }
            }
            Record(); // 基座只有达到目标属性才算有效解

            while (clock.ElapsedMilliseconds < deadline && StepMaxAttr(items, stockLeft, area, ref attrs, ta))
            {
                Record();
            }

            // 结果复查：移除冗余材料（尤其多余的目标属性材料），再尝试继续提级
            var rechecked = RecheckMaxAttr(items, ta);
            if (rechecked != null && RuneEvaluator.ItemAttrOf(eval.SumAttrs(rechecked.Placements)) == ta)
            {
                results.Add(rechecked);
            }
            return results;
        }

        /// <summary>复查：反向贪心移除冗余材料（不降低数量/等级、不破坏目标属性最大），再尝试继续提级。</summary>
        EquipmentSolution RecheckMaxAttr(List<(Material mat, Side side)> items, int ta)
        {
            bool changed = true;
            while (changed && clock.ElapsedMilliseconds < deadline)
            {
                changed = false;
                var baseAttrs = AttrsOf(items);
                var baseR = eval.Evaluate(baseAttrs);
                bool targetWasOk = RuneEvaluator.ItemAttrOf(baseAttrs) == ta;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var tmp = new List<(Material mat, Side side)>(items);
                    tmp.RemoveAt(i);
                    if (PackItems(tmp) == null)
                    {
                        continue;
                    }
                    var na = AttrsOf(tmp);
                    var nr = eval.Evaluate(na);
                    bool targetOk = RuneEvaluator.ItemAttrOf(na) == ta;
                    if (nr.ActiveCount >= baseR.ActiveCount && nr.TotalLevel >= baseR.TotalLevel && (targetOk || !targetWasOk))
                    {
                        items = tmp;
                        changed = true;
                        break;
                    }
                }
            }
            var stockLeft = req.Materials.ToDictionary(m => m, m => m.Stock);
            foreach (var it in items)
            {
                stockLeft[it.mat]--;
            }
            var area = new int[3];
            foreach (var it in items)
            {
                area[(int)it.side] += it.mat.Shape.Count;
            }
            var attrs = AttrsOf(items);
            while (clock.ElapsedMilliseconds < deadline && StepMaxAttr(items, stockLeft, area, ref attrs, ta))
            {
            }
            return PackItems(items);
        }

        /// <summary>统一流程排序键（越大越好）：优先级组覆盖(先覆盖再逐层升级) › 材料策略(次数/低阶) › −总价 › −件数。</summary>
        IComparable[] UnifiedRankKey(EquipmentSolution s)
        {
            int maxGroup = 0;
            var pg = req.PriorityGroup;
            for (int r = 0; r < req.Runes.Count; r++)
            {
                maxGroup = Math.Max(maxGroup, pg != null && r < pg.Length ? Math.Max(0, pg[r]) : 0);
            }
            var lv = s.Runes.Level;
            long score = 0;
            for (int g = 0; g <= maxGroup; g++)
            {
                long gw = 1;
                for (int k = 0; k < maxGroup - g; k++)
                {
                    gw *= 1000;
                }
                long cov = 0, dep = 0;
                for (int r = 0; r < req.Runes.Count; r++)
                {
                    int rg = pg != null && r < pg.Length ? Math.Max(0, pg[r]) : 0;
                    if (rg != g)
                    {
                        continue;
                    }
                    cov += lv[r] >= 1 ? 1 : 0;
                    dep += lv[r];
                }
                score += gw * (1000 * cov + dep);
            }
            long pol;
            if (req.MaterialPolicy == MaterialPolicy.MaxRepeatCount)
            {
                pol = s.Batches;
            }
            else
            {
                pol = -GradeRankSum(s);
            }
            return new IComparable[] { score, pol, -s.Price, -s.PieceCount };
        }

        long GradeRankSum(EquipmentSolution s)
        {
            var grades = req.Materials.Where(m => m.Stock > 0 && m.Shape.Count > 0).Select(m => m.Grade).Distinct().OrderBy(g => g).ToList();
            var rank = new Dictionary<int, int>();
            for (int i = 0; i < grades.Count; i++)
            {
                rank[grades[i]] = i;
            }
            long sum = 0;
            foreach (var p in s.Placements)
            {
                sum += rank.TryGetValue(p.Material.Grade, out var r) ? r : 0;
            }
            return sum;
        }

        /// <summary>统一排序键（越大越好）。比较顺序：最少材料/最多炼制次数=符文数量&gt;符文等级&gt;件数/次数；指定符文优先=优先等级&gt;符文数量&gt;其它等级&gt;件数；最高属性=目标属性&gt;符文数量&gt;等级&gt;次数。</summary>
        IComparable[] RankKey(EquipmentSolution s, EquipmentMode mode)
        {
            if (req.Unified)
            {
                return UnifiedRankKey(s);
            }
            int active = s.Runes.ActiveCount;
            int level = s.Runes.TotalLevel;
            switch (mode)
            {
                case EquipmentMode.MinPieces:
                    return new IComparable[] { active, level, -s.PieceCount };
                case EquipmentMode.LowGrade:
                    return new IComparable[] { active, -s.MaxGrade, -s.GradeSum, -s.Price, -s.PieceCount };
                case EquipmentMode.MaxBatches:
                    return new IComparable[] { active, level, s.Batches, -s.PieceCount };
                case EquipmentMode.MaxAttr:
                    // 锁定属性（硬约束已保证归属）→ 等级优先 → 省料（价 → 件数）
                    return new IComparable[] { active, level, -s.Price, -s.PieceCount };
                case EquipmentMode.MaxRuneLevel:
                {
                    int pri = 0, other = 0;
                    for (int i = 0; i < req.Runes.Count; i++)
                    {
                        if (req.Priority != null && i < req.Priority.Length && req.Priority[i])
                        {
                            pri += s.Runes.Level[i];
                        }
                        else
                        {
                            other += s.Runes.Level[i];
                        }
                    }
                    return new IComparable[] { pri, active, other, -s.PieceCount };
                }
                default:
                    return new IComparable[] { active, level, -s.PieceCount };
            }
        }

        static int CompareRank(IComparable[] a, IComparable[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = b[i].CompareTo(a[i]);
                if (c != 0)
                {
                    return c;
                }
            }
            return 0;
        }

        static string SolKey(EquipmentSolution s)
        {
            return string.Join(",", s.Placements.Select(p => p.Material.Id + "@" + (int)p.Side).OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>满级目标：每维 = max(要求 × maxStacks)。</summary>
        int[] MaxLevelTarget()
        {
            var t = new int[Attr.Count];
            foreach (var r in req.Runes)
            {
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (r.Requirement[d] > 0)
                    {
                        t[d] = Math.Max(t[d], r.Requirement[d] * r.MaxStacks);
                    }
                }
            }
            return t;
        }

        /// <summary>估算最低可用材料品阶：低于此品阶的材料，对任何需求维度，即使填满格子也到不了该维度的目标。</summary>
        int MinGrade(int[] target)
        {
            int capacity = capYang + capYin;
            if (capacity <= 0)
            {
                return int.MinValue;
            }
            var need = new double[Attr.Count];
            bool any = false;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (target[d] > 0)
                {
                    need[d] = (double)target[d] / capacity;
                    any = true;
                }
            }
            if (!any)
            {
                return int.MinValue;
            }
            int min = int.MaxValue;
            foreach (var m in req.Materials)
            {
                if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                {
                    continue;
                }
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (target[d] > 0 && (double)Math.Abs(m.Attrs[d]) / m.Shape.Count >= need[d])
                    {
                        min = Math.Min(min, m.Grade);
                        break;
                    }
                }
            }
            return min == int.MaxValue ? int.MinValue : min;
        }

        /// <summary>CP-SAT 求最低价；失败/无解返回空（回退品阶门槛）。装不下时加形状切割重求。</summary>
        List<EquipmentSolution> SolveCpSat()
        {
            try
            {
                var cuts = new List<CpSatQuantity.ShapeCut>();
                for (int iter = 0; iter < 3; iter++)
                {
                    double remainSec = Math.Max(0.1, (deadline - clock.ElapsedMilliseconds) / 1000.0);
                    var items = CpSatQuantity.Solve(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, remainSec, cuts);
                    if (items == null || items.Count == 0)
                    {
                        return new List<EquipmentSolution>();
                    }
                    var sol = PackItems(items);
                    if (sol != null)
                    {
                        return new List<EquipmentSolution> { sol };
                    }
                    var cut = FailingShapeCut(items);
                    if (cut == null)
                    {
                        return new List<EquipmentSolution>();
                    }
                    cuts.Add(cut);
                }
                return new List<EquipmentSolution>();
            }
            catch
            {
                return new List<EquipmentSolution>();
            }
        }

        /// <summary>装填失败时，找出装不下的那侧，形成形状切割；两侧都装得下则返回 null。</summary>
        CpSatQuantity.ShapeCut FailingShapeCut(List<(Material mat, Side side)> items)
        {
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var pieces = items.Where(x => x.side == side).Select(x => x.mat).ToList();
                if (pieces.Count == 0)
                {
                    continue;
                }
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                // 局部预算（当前时间 + 300ms），而非绝对 300ms；并区分「超时」与「已证装不下」
                var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), clock, (int)(clock.ElapsedMilliseconds + 300));
                if (packed.Ok || packed.TimedOut)
                {
                    continue;
                }
                {
                    var counts = new Dictionary<string, int>();
                    foreach (var m in pieces)
                    {
                        string key = Packer.GetType(m.Shape).Key;
                        counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                    return new CpSatQuantity.ShapeCut { YinSide = side == Side.Yin, Counts = counts };
                }
            }
            return null;
        }

        /// <summary>CP-SAT 等级建模求最高品阶/指定符文优先；失败返回空（回退启发式）。装不下时加形状切割重求。</summary>
        List<EquipmentSolution> SolveCpSatLeveled(EquipmentMode mode, bool dominant = true)
        {
            try
            {
                int ta = Math.Max(0, req.TargetAttr);
                bool[] pri = req.Priority != null && req.Priority.Length == req.Runes.Count ? req.Priority : new bool[req.Runes.Count];
                var cuts = new List<CpSatQuantity.ShapeCut>();
                for (int iter = 0; iter < 3; iter++)
                {
                    double remainSec = Math.Max(0.1, (deadline - clock.ElapsedMilliseconds) / 1000.0);
                    var items = CpSatQuantity.SolveLeveled(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, remainSec, mode, ta, pri, cuts, dominant);
                    if (items == null || items.Count == 0)
                    {
                        return new List<EquipmentSolution>();
                    }
                    var sol = PackItems(items);
                    if (sol != null)
                    {
                        return new List<EquipmentSolution> { sol };
                    }
                    var cut = FailingShapeCut(items);
                    if (cut == null)
                    {
                        return new List<EquipmentSolution>();
                    }
                    cuts.Add(cut);
                }
                return new List<EquipmentSolution>();
            }
            catch
            {
                return new List<EquipmentSolution>();
            }
        }

        /// <summary>统一流程：CP-SAT 求解（优先级组+可选属性+材料策略），装不下时加形状切割重求。</summary>
        /// <summary>质量覆盖键（报告 §5.3）：逐层「达到该层的符文数」按字典序编码，越大越好。</summary>
        long CoverageKey(int[] level)
        {
            if (level == null || level.Length == 0)
            {
                return long.MinValue;
            }
            long key = 0;
            int maxL = 0;
            foreach (var r in req.Runes)
            {
                maxL = Math.Max(maxL, r.MaxStacks);
            }
            for (int l = 1; l <= maxL; l++)
            {
                int h = 0;
                foreach (var lv in level)
                {
                    if (lv >= l)
                    {
                        h++;
                    }
                }
                key = key * 100 + h;
            }
            return key;
        }

        /// <summary>按形状类型联合建模（报告 §6）：几何与数量在同一模型，用于分解路径反复装不下时。</summary>
        List<EquipmentSolution> SolveJointUnified()
        {
            try
            {
                long remain = deadline - clock.ElapsedMilliseconds;
                if (remain < 200)
                {
                    return new List<EquipmentSolution>();
                }
                // 质量前沿第一步（报告 §7.1）：先试「满级可行性」。满级可行则质量已是上限，直接进材料阶段；
                // 满级不可行才做质量优化，并用其合法方案作为材料阶段提示。
                var fullTarget = new int[req.Runes.Count];
                for (int i = 0; i < fullTarget.Length; i++)
                {
                    fullTarget[i] = req.Runes[i].MaxStacks;
                }
                var qFull = JointGeometrySolver.Solve(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, Math.Min(1.5, remain / 1000.0 * 0.15), req.TargetAttr, Math.Max(1, CpSatQuantity.Workers), req.PriorityGroup, req.MaterialPolicy, fullTarget);
                roundLog.Add($"联合几何·满级检查 {qFull.Status} {qFull.ElapsedMs}ms 件{qFull.Items.Count}");
                JointGeometrySolver.Candidate cand;
                int[] targetLevel;
                if (qFull.Items.Count > 0)
                {
                    cand = qFull;
                    targetLevel = fullTarget;
                }
                else
                {
                    // 满级不可行 → 质量优化。跑 2 次短切片按覆盖向量取更好，降低多线程随机性导致的质量波动；
                    // 选中的合法方案同时作为材料阶段热启动提示。
                    JointGeometrySolver.Candidate q1 = null;
                    long bestKey = long.MinValue;
                    for (int k = 0; k < 2; k++)
                    {
                        long rem = deadline - clock.ElapsedMilliseconds;
                        if (rem < 300)
                        {
                            break;
                        }
                        long budgetMs = k == 0 ? rem * 55 / 100 : rem * 95 / 100;
                        var c = JointGeometrySolver.Solve(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, Math.Max(0.3, budgetMs / 1000.0), req.TargetAttr, Math.Max(1, CpSatQuantity.Workers), req.PriorityGroup, req.MaterialPolicy, null);
                        long key = CoverageKey(c.Level);
                        roundLog.Add($"联合几何·质量{k + 1} {c.Status} {c.ElapsedMs}ms 等级[{string.Join(",", c.Level)}] 覆盖键{key}");
                        if (q1 == null || key > bestKey)
                        {
                            q1 = c;
                            bestKey = key;
                        }
                    }
                    cand = q1;
                    targetLevel = q1 != null ? q1.Level : null;
                }
                // 阶段2：固定质量/覆盖，优化材料策略（用上阶段方案做热启动提示）
                if (cand != null && cand.Items.Count > 0)
                {
                    long remain2 = deadline - clock.ElapsedMilliseconds;
                    if (remain2 > 300)
                    {
                        var q2 = JointGeometrySolver.Solve(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, remain2 / 1000.0, req.TargetAttr, Math.Max(1, CpSatQuantity.Workers), req.PriorityGroup, req.MaterialPolicy, targetLevel, cand);
                        roundLog.Add($"联合几何·材料 {q2.Status} {q2.ElapsedMs}ms 件{q2.Items.Count} 等级[{string.Join(",", q2.Level)}]");
                        if (q2.Items.Count > 0)
                        {
                            cand = q2;
                        }
                    }
                }
                if (cand == null || cand.Items.Count == 0)
                {
                    roundLog.Add("联合几何 无解");
                    return new List<EquipmentSolution>();
                }
                var sol = new EquipmentSolution();
                foreach (var it in cand.Items)
                {
                    sol.Placements.Add(new Placement { Material = it.mat, Side = it.side, X = it.x, Y = it.y, Rotate = it.rot });
                }
                Finalize(sol);
                string err = Verify(req, sol);
                roundLog.Add($"联合几何 {cand.Status} {cand.ElapsedMs}ms {cand.Items.Count}件 等级[{string.Join(",", cand.Level)}] 复核{(err ?? "OK")}");
                if (err != null)
                {
                    return new List<EquipmentSolution>();
                }
                if (cand.Status == "Feasible")
                {
                    notProvenOptimal = true;
                }
                return new List<EquipmentSolution> { sol };
            }
            catch
            {
                return new List<EquipmentSolution>();
            }
        }

        List<EquipmentSolution> SolveCpSatUnified(long budgetMs)
        {
            // 报告 §6.4：分解路径只用一个受限预算，剩下的留给联合几何生成器
            long solveDeadline = Math.Min(deadline, clock.ElapsedMilliseconds + Math.Max(100L, budgetMs));
            try
            {
                // 阶段1：符文质量（先覆盖再升级）；装不下时加形状切割重求（报告 P1-3）
                var cuts = new List<CpSatQuantity.ShapeCut>();
                EquipmentSolution sol1 = null;
                for (int iter = 0; iter < 4; iter++)
                {
                    long remainMs = solveDeadline - clock.ElapsedMilliseconds;
                    if (remainMs <= 50)
                    {
                        break;
                    }
                    // 首轮短切片快速反馈；后续轮次用剩余预算（报告 §5.2）
                    double sliceSec = iter == 0 ? Math.Min(0.4, remainMs / 1000.0) : (remainMs / 1000.0) * 0.9;
                    var items1 = CpSatQuantity.SolveUnified(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, sliceSec, req.PriorityGroup, req.TargetAttr, req.MaterialPolicy, cuts, null);
                    roundLog.Add($"阶段1轮{iter} 切片{sliceSec:0.00}s {CpSatQuantity.LastLog}");
                    if (items1 == null || items1.Count == 0)
                    {
                        // 只有原始问题（无 cuts）被证明 Infeasible 才算已证无解（报告 §4.2）
                        if (cuts.Count == 0 && CpSatQuantity.LastStatus == "Infeasible")
                        {
                            provenInfeasible = true;
                            return new List<EquipmentSolution>();
                        }
                        // 超时(Unknown)：下一轮用更长切片继续（报告 §5.2）
                        continue;
                    }
                    var outcome1 = PackItemsOutcome(items1);
                    sol1 = outcome1.Solution;
                    if (sol1 != null)
                    {
                        if (CpSatQuantity.LastStatus == "Feasible")
                        {
                            notProvenOptimal = true;
                        }
                        break;
                    }
                    if (!outcome1.ProvenImpossible || outcome1.FailCounts == null)
                    {
                        // 装填未知（超时）：未证明装不下，不能用它生成排除条件；用更长切片重试
                        continue;
                    }
                    cuts.Add(new CpSatQuantity.ShapeCut { YinSide = outcome1.FailedSide == Side.Yin, Counts = outcome1.FailCounts });
                }
                if (sol1 == null)
                {
                    return new List<EquipmentSolution>();
                }
                // 阶段2：固定阶段1的层数向量，单独优化材料策略 + 省料；继承阶段1已证几何排除（报告 §6.3）
                int[] lv = LevelsOf(sol1.Placements);
                double s2 = Math.Max(0.1, (solveDeadline - clock.ElapsedMilliseconds) / 1000.0);
                var items2 = CpSatQuantity.SolveUnified(req.Materials, req.Runes, req.Bonus, req.Yang, req.Yin, s2, req.PriorityGroup, req.TargetAttr, req.MaterialPolicy, cuts, lv);
                if (items2 != null && items2.Count > 0)
                {
                    var sol2 = PackItems(items2);
                    if (sol2 != null)
                    {
                        // 多方案：阶段2（省料优先）排第一，阶段1（质量优先、可能不同材料）作为备选
                        var list = new List<EquipmentSolution> { sol2 };
                        if (!SameUsage(sol1, sol2))
                        {
                            list.Add(sol1);
                        }
                        return list;
                    }
                }
                return new List<EquipmentSolution> { sol1 };
            }
            catch
            {
                return new List<EquipmentSolution>();
            }
        }

        /// <summary>由摆法算每个符文的实际层数向量（阶段2固定用）。</summary>
        int[] LevelsOf(List<Placement> placements)
        {
            var attrs = eval.SumAttrs(placements);
            return eval.Evaluate(attrs).Level;
        }

        /// <summary>两个方案的阴阳材料用量是否完全相同（多方案去重用）。</summary>
        static bool SameUsage(EquipmentSolution a, EquipmentSolution b)
        {
            string Key(EquipmentSolution s) => string.Join(",", s.Placements.GroupBy(p => p.Material.Id + "@" + (int)p.Side).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "x" + g.Count()));
            return Key(a) == Key(b);
        }

        /// <summary>按模式找基座：最低品阶从低到高试品阶门槛；其余用全材料最少材料基座。</summary>
        List<EquipmentSolution> FindBase(EquipmentMode mode)
        {
            if (mode == EquipmentMode.LowGrade)
            {
                var cp = SolveCpSat();
                if (cp.Count > 0)
                {
                    return cp;
                }
                var grades = req.Materials.Where(m => m.Stock > 0 && m.Shape.Count > 0 && m.Attrs.Any(v => v != 0)).Select(m => m.Grade).Distinct().OrderBy(g => g).ToList();
                var target = TargetOf(req.Runes, _ => 1);
                int floor1 = MinGrade(target);
                if (floor1 > int.MinValue)
                {
                    grades = grades.Where(g => g >= floor1).ToList();
                }
                for (int i = 0; i < grades.Count; i++)
                {
                    int g = grades[i];
                    anyNoFit = false;
                    timedOut = false;
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
            int floor;
            if (mode == EquipmentMode.MinPieces || mode == EquipmentMode.MaxActiveRunes)
            {
                // 这两种模式只需满足一层，用一层目标估算品阶下限，不能用满层目标（否则会误删低阶的一件解）
                floor = MinGrade(TargetOf(req.Runes, _ => 1));
            }
            else
            {
                floor = MinGrade(MaxLevelTarget());
            }
            if (floor > int.MinValue)
            {
                BuildUnits(int.MaxValue, floor);
                var sols = SolveMaxActive();
                if (sols.Count > 0)
                {
                    return sols;
                }
                anyNoFit = false;
                timedOut = false;
            }
            BuildUnits();
            return SolveMaxActive();
        }

        /// <summary>判断两个形状的原始坐标是否完全一致（可原位互换，无需重装填）。</summary>
        static bool SameShape(List<Cell> a, List<Cell> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].X != b[i].X || a[i].Y != b[i].Y)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>去浪费：把方案里每件材料尝试原位替换成「同形状、仍满足目标、但品阶/价值更低」的材料。</summary>
        EquipmentSolution ReduceWaste(EquipmentSolution sol, EquipmentMode mode)
        {
            int ta = Math.Max(0, req.TargetAttr);
            var baseKey = RankKey(sol, mode);
            var placements = sol.Placements.ToList();
            var stockLeft = req.Materials.ToDictionary(m => m, m => m.Stock);
            foreach (var p in placements)
            {
                stockLeft[p.Material]--;
            }
            bool changed = true;
            while (changed && clock.ElapsedMilliseconds < deadline)
            {
                changed = false;
                for (int i = 0; i < placements.Count; i++)
                {
                    var p = placements[i];
                    // 删除冗余单件（报告 §9.4）：不降低统一评分、且不破坏指定属性才删
                    if (placements.Count > 1)
                    {
                        var del = placements.ToList();
                        del.RemoveAt(i);
                        bool attrOk = req.TargetAttr < 0 || RuneEvaluator.ItemAttrOf(eval.SumAttrs(del)) == req.TargetAttr;
                        if (attrOk)
                        {
                            var dc = new EquipmentSolution { Placements = del };
                            Finalize(dc);
                            if (CompareRank(RankKey(dc, mode), baseKey) <= 0)
                            {
                                placements = del;
                                baseKey = RankKey(dc, mode);
                                changed = true;
                                break;
                            }
                        }
                    }
                    foreach (var m in req.Materials)
                    {
                        if (clock.ElapsedMilliseconds > deadline)
                        {
                            break;
                        }
                        if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                        {
                            continue;
                        }
                        if (m.Grade > p.Material.Grade)
                        {
                            continue;
                        }
                        if (m.Grade == p.Material.Grade && m.Price >= p.Material.Price)
                        {
                            continue;
                        }
                        if (ReferenceEquals(m, p.Material) || stockLeft[m] <= 0)
                        {
                            continue;
                        }
                        if (!SameShape(m.Shape, p.Material.Shape))
                        {
                            continue;
                        }
                        var tmp = placements.ToList();
                        tmp[i] = new Placement { Material = m, Side = p.Side, X = p.X, Y = p.Y, Rotate = p.Rotate };
                        var cand = new EquipmentSolution { Placements = tmp };
                        Finalize(cand);
                        if (CompareRank(RankKey(cand, mode), baseKey) > 0)
                        {
                            continue; // 替换后整体目标变差（含指定符文优先）
                        }
                        if (mode == EquipmentMode.MaxAttr)
                        {
                            bool was = RuneEvaluator.ItemAttrOf(eval.SumAttrs(placements)) == ta;
                            bool now = RuneEvaluator.ItemAttrOf(eval.SumAttrs(tmp)) == ta;
                            if (was && !now)
                            {
                                continue;
                            }
                        }
                        stockLeft[p.Material]++;
                        stockLeft[m]--;
                        placements = tmp;
                        // 报告 §9.2：接受替换后必须更新比较基准，否则会相对旧基准越换越差
                        baseKey = RankKey(cand, mode);
                        changed = true;
                        break;
                    }
                    if (changed)
                    {
                        break;
                    }
                }
            }
            return new EquipmentSolution { Placements = placements };
        }

        /// <summary>最低价局部改善：删除冗余材料 + 把材料替换成更便宜的（不同形状需重装填），保持符文等级不降。</summary>
        EquipmentSolution ImproveCheaper(EquipmentSolution sol)
        {
            var baseR = sol.Runes ?? eval.Evaluate(sol.Placements);
            var items = sol.Placements.Select(p => (mat: p.Material, side: p.Side)).ToList();
            var stockLeft = req.Materials.ToDictionary(m => m, m => m.Stock);
            foreach (var it in items)
            {
                stockLeft[it.mat]--;
            }
            long budget = Math.Min(deadline, clock.ElapsedMilliseconds + 800);
            bool changed = true;
            while (changed && clock.ElapsedMilliseconds < budget)
            {
                changed = false;
                // 1) 删冗余：去掉某件材料后目标仍满足
                for (int i = 0; i < items.Count && clock.ElapsedMilliseconds < budget; i++)
                {
                    var tmp = new List<(Material mat, Side side)>(items);
                    tmp.RemoveAt(i);
                    var r = eval.Evaluate(AttrsOf(tmp));
                    if (r.ActiveCount < baseR.ActiveCount || r.TotalLevel < baseR.TotalLevel)
                    {
                        continue;
                    }
                    if (PackItems(tmp) == null)
                    {
                        continue;
                    }
                    stockLeft[items[i].mat]++;
                    items = tmp;
                    changed = true;
                    break;
                }
                if (changed)
                {
                    continue;
                }
                // 2) 换更便宜：同件数替换成更低品阶/更低价格的材料
                for (int i = 0; i < items.Count && clock.ElapsedMilliseconds < budget; i++)
                {
                    var cur = items[i];
                    foreach (var m in req.Materials)
                    {
                        if (clock.ElapsedMilliseconds > budget)
                        {
                            break;
                        }
                        if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                        {
                            continue;
                        }
                        if (m.Grade > cur.mat.Grade)
                        {
                            continue;
                        }
                        if (m.Grade == cur.mat.Grade && m.Price >= cur.mat.Price)
                        {
                            continue;
                        }
                        if (ReferenceEquals(m, cur.mat) || stockLeft[m] <= 0)
                        {
                            continue;
                        }
                        var tmp = new List<(Material mat, Side side)>(items);
                        tmp[i] = (m, cur.side);
                        var r = eval.Evaluate(AttrsOf(tmp));
                        if (r.ActiveCount < baseR.ActiveCount || r.TotalLevel < baseR.TotalLevel)
                        {
                            continue;
                        }
                        if (PackItems(tmp) == null)
                        {
                            continue;
                        }
                        stockLeft[cur.mat]++;
                        stockLeft[m]--;
                        items = tmp;
                        changed = true;
                        break;
                    }
                    if (changed)
                    {
                        break;
                    }
                }
            }
            return PackItems(items);
        }

        /// <summary>统一搜索：基座（满足最多符文）→ 提级 → 按模式排序取前 K。</summary>
        List<EquipmentSolution> SolveSatisfy(EquipmentMode mode)
        {
            if (req.Unified)
            {
                // 阶段1：分解模型（限制预算，避免吃光全部时间，给联合几何留机会）
                var cu = SolveCpSatUnified((long)(deadline * 0.4));
                if (cu.Count > 0)
                {
                    solvePath = "CP-SAT(统一)";
                    foreach (var s in cu)
                    {
                        Finalize(s);
                    }
                    // 统一节省：CP 成功结果也走 ReduceWaste（保持统一排序键不下降）
                    var reduced = ReduceWaste(cu[0], EquipmentMode.MaxAttr);
                    if (reduced != null)
                    {
                        Finalize(reduced);
                        cu[0] = reduced;
                    }
                    return cu;
                }
                // 分解路径失败（数量装不下/超时）：尝试按形状类型的联合几何模型（报告 §6.4）
                var joint = SolveJointUnified();
                if (joint.Count > 0)
                {
                    solvePath = "联合几何";
                    foreach (var s in joint)
                    {
                        Finalize(s);
                    }
                    // 报告 P0：联合路径也走统一节省（与分解路径同一偏好）
                    var jr = ReduceWaste(joint[0], EquipmentMode.MaxAttr);
                    if (jr != null)
                    {
                        Finalize(jr);
                        joint[0] = jr;
                    }
                    return joint;
                }
                // 指定了法宝属性时，不能回退到忽略属性的启发式（否则会得到错误属性，如要火出金）
                if (req.TargetAttr >= 0)
                {
                    return new List<EquipmentSolution>();
                }
                // 未指定属性：回退到启发式（按优先级组提级）
                mode = EquipmentMode.MaxRuneLevel;
                if (req.Priority == null || req.Priority.Length != req.Runes.Count)
                {
                    var pri = new bool[req.Runes.Count];
                    for (int i = 0; i < pri.Length; i++)
                    {
                        pri[i] = req.PriorityGroup == null || i >= req.PriorityGroup.Length || req.PriorityGroup[i] == 0;
                    }
                    req.Priority = pri;
                }
            }
            // 最少材料 / 指定符文优先 / 最多炼制次数 / 最大属性：先试 CP-SAT 等级建模（已证实启发式非最优）
            if (mode == EquipmentMode.MinPieces || mode == EquipmentMode.MaxRuneLevel || mode == EquipmentMode.MaxBatches || mode == EquipmentMode.MaxAttr)
            {
                var cp = SolveCpSatLeveled(mode);
                if (cp.Count > 0)
                {
                    solvePath = "CP-SAT";
                    return cp;
                }
            }
            solvePath = "启发式";
            var bases = FindBase(mode);
            if (bases.Count == 0)
            {
                return bases;
            }
            var candidates = new List<EquipmentSolution>();
            var seen = new HashSet<string>();
            foreach (var b in bases.Take(2))
            {
                List<EquipmentSolution> raised;
                if (mode == EquipmentMode.MaxAttr)
                {
                    raised = RaiseMaxAttr(b);
                }
                else if (mode == EquipmentMode.LowGrade)
                {
                    var improved = ImproveCheaper(b);
                    raised = new List<EquipmentSolution> { improved ?? b };
                }
                else
                {
                    raised = RaiseLevels(b, mode);
                }
                foreach (var s in raised)
                {
                    Finalize(s);
                    if (seen.Add(SolKey(s)))
                    {
                        candidates.Add(s);
                    }
                }
            }
            if (mode == EquipmentMode.MaxAttr && !req.AllowPartial)
            {
                // 固定符文组合：不接受回退静默放弃部分符文的方案（须全部符文≥1级）；
                // 用户主动开启「部分达成备选」时允许放弃
                candidates = candidates.Where(s => s.Runes.ActiveCount == req.Runes.Count).ToList();
            }
            if (candidates.Count == 0)
            {
                // 最大属性：目标属性领先不可行时，退化为不强制领先（最大化目标属性值，避免整体无解）
                // 仅当目标属性值 > 0（确有目标属性材料/符文）才返回，否则保持无解（避免 P1「无火返回金」回归）
                if (mode == EquipmentMode.MaxAttr)
                {
                    var cp2 = SolveCpSatLeveled(mode, dominant: false);
                    if (cp2.Count > 0)
                    {
                        int ta = Math.Max(0, req.TargetAttr);
                        if (eval.SumAttrs(cp2[0].Placements)[ta] > 0)
                        {
                            return cp2;
                        }
                    }
                }
                return candidates;
            }
            candidates.Sort((x, y) => CompareRank(RankKey(x, mode), RankKey(y, mode)));
            var top = candidates.Take(req.TopK).ToList();
            for (int i = 0; i < top.Count; i++)
            {
                var reduced = ReduceWaste(top[i], mode);
                if (reduced != null)
                {
                    Finalize(reduced);
                    top[i] = reduced;
                }
            }
            top.Sort((x, y) => CompareRank(RankKey(x, mode), RankKey(y, mode)));
            return top;
        }

        /// <summary>带缓存的装填（报告 §6.2）：键=盘尺寸+侧+按序的规范形状类型序列（同形不同材料可复用，且 Spot 顺序对齐）。</summary>
        Packer.Result PackShapes(Grid grid, Side side, List<Material> pieces)
        {
            var key = $"{(side == Side.Yang ? "Y" : "N")}|{grid.Width}x{grid.Height}|" + string.Join(",", pieces.Select(p => Packer.GetType(p.Shape).Key));
            if (packResultCache.TryGetValue(key, out var hit))
            {
                packResultHits++;
                return hit;
            }
            packResultMisses++;
            // 报告 §4.2：Packer 内部用共享 clock 与【绝对截止】比较，这里必须传绝对时间，不能传相对切片
            long remain = Math.Max(0L, deadline - clock.ElapsedMilliseconds);
            long sliceMs = Math.Min(200L, remain);
            int absDeadline = (int)Math.Min(deadline, clock.ElapsedMilliseconds + sliceMs);
            var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), clock, absDeadline);
            // 报告 §4.3：Unknown(超时) 不能当结论缓存，下次可用更多时间重试
            if (!packed.TimedOut)
            {
                packResultCache[key] = packed;
            }
            return packed;
        }

        /// <summary>装填：失败时带失败侧/形状计数/是否已证不可行（报告 §6.1，供切割直接用，不再重复装填）。</summary>
        PackOutcome PackItemsOutcome(List<(Material mat, Side side)> items)
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
                if (grid == null)
                {
                    continue;
                }
                var packed = PackShapes(grid, side, pieces);
                if (!packed.Ok)
                {
                    var counts = new Dictionary<string, int>();
                    foreach (var p in pieces)
                    {
                        // 报告 §4.1：键必须是规范形状类型（与 AddShapeCut 的匹配口径一致），不能用材料 ID
                        var shapeKey = Packer.GetType(p.Shape).Key;
                        counts.TryGetValue(shapeKey, out var c);
                        counts[shapeKey] = c + 1;
                    }
                    return new PackOutcome { Failed = true, ProvenImpossible = !packed.TimedOut, FailedSide = side, FailCounts = counts };
                }
                for (int i = 0; i < pieces.Count; i++)
                {
                    var s = packed.Spots[i];
                    sol.Placements.Add(new Placement { Material = pieces[i], Side = side, X = s.x, Y = s.y, Rotate = s.rotate });
                }
            }
            return new PackOutcome { Solution = sol };
        }

        EquipmentSolution PackItems(List<(Material mat, Side side)> items)
        {
            return PackItemsOutcome(items).Solution;
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
            if (req.TargetAttr >= 0 && RuneEvaluator.ItemAttrOf(expect.FinalAttrs) != req.TargetAttr)
            {
                return $"目标属性未达成（{Attr.Names[req.TargetAttr]}）";
            }
            return null;
        }
    }
}
