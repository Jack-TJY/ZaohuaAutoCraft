using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 炼丹：选材（阳加阴减后正好等于 Need）+ 装箱 + 摆位优化。
    /// 选材按件数从少到多逐层找组合：少件数用折半查表（一次查出所有 ≤2h 件的组合），更多件用带剪枝的 DFS。
    /// 不同模式只是「找哪些层、怎么排序、要不要调摆位」不同。
    /// </summary>
    public class DrugSolver
    {
        // 同一侧、属性完全相同、形状相同、品阶相同的材料归成一类，搜索时只看类，装箱时再挑具体材料
        class Unit
        {
            public List<int> Mats = new List<int>();

            public Side Side;

            public int[] Contrib;

            public int Size;

            public int Type;

            public int Grade;

            public bool Pure;

            /// <summary>补料模式：类内最便宜的可买单价；不可买为 -1。</summary>
            public long BuyPrice = -1;

            public int Affinity;
        }

        readonly DrugRequest req;

        readonly List<Unit> allUnits = new List<Unit>();

        readonly Stopwatch clock = new Stopwatch();

        readonly List<List<Cell>> typeShapes = new List<List<Cell>>();

        readonly Dictionary<string, int> typeIds = new Dictionary<string, int>();

        DrugStateEvaluator eval;

        bool timedOut;

        long deadline;

        public int MaxCombosPerDepth = 300;

        public static Action<string> Debug;

        public DrugSolver(DrugRequest req)
        {
            this.req = req;
        }

        int capYang => req.Yang?.Area ?? 0;

        int capYin => req.Yin?.Area ?? 0;

        // ───────────────────────── 入口 ─────────────────────────

        public SolveResult Solve()
        {
            clock.Restart();
            deadline = req.TimeLimitMs;
            var result = new SolveResult();
            eval = req.States != null && req.States.Count > 0 ? new DrugStateEvaluator(req.States, req.Yang, req.Yin) : null;
            BuildUnits();
            if (req.Need.All(v => v == 0))
            {
                var empty = new Solution();
                Finalize(empty);
                result.Solutions.Add(empty);
                result.Message = "丹炉加成已经满足丹方，无需材料";
                return Finish(result);
            }
            var mode = req.Mode;
            if (eval == null && (mode == DrugMode.MaxCount || mode == DrugMode.MaxQuality))
            {
                mode = DrugMode.MinPieces;
            }
            List<Solution> sols;
            if (req.TopUp)
            {
                sols = SolveTopUp();
            }
            else
            {
                switch (mode)
                {
                    case DrugMode.LowGrade:
                        sols = SolveLowGrade();
                        break;
                    case DrugMode.MaxCount:
                    case DrugMode.MaxQuality:
                        sols = SolvePlacementModes(mode);
                        break;
                    case DrugMode.MaxBatches:
                        sols = SolveMaxBatches();
                        break;
                    default:
                        sols = SolveMinPieces(Passes(allUnits), 1, deadline);
                        break;
                }
            }
            // 剩余时间给每个方案调摆位（模式 3/4 已经调过）
            if (eval != null && mode != DrugMode.MaxCount && mode != DrugMode.MaxQuality)
            {
                long budget = Math.Max(30, (req.TimeLimitMs + 400 - clock.ElapsedMilliseconds) / Math.Max(1, sols.Count));
                foreach (var s in sols)
                {
                    Optimize(s, Math.Min(clock.ElapsedMilliseconds + budget, req.TimeLimitMs + 400), DrugMode.MaxQuality);
                }
            }
            foreach (var s in sols)
            {
                Finalize(s);
            }
            result.Solutions = sols;
            result.TimedOut = timedOut;
            if (sols.Count == 0)
            {
                result.Message = result.FoundComboButNoFit || anyNoFit ? "属性能凑出，但形状放不进当前丹炉" : (timedOut ? "超时，未找到摆法" : "现有材料凑不出该丹方");
                result.FoundComboButNoFit = anyNoFit;
            }
            result.CombosTried = combosTried;
            return Finish(result);
        }

        SolveResult Finish(SolveResult r)
        {
            r.ElapsedMs = clock.ElapsedMilliseconds;
            return r;
        }

        void Finalize(Solution s)
        {
            s.MaxGrade = s.Placements.Count == 0 ? 0 : s.Placements.Max(p => p.Material.Grade);
            s.GradeSum = s.Placements.Sum(p => p.Material.Grade);
            s.Price = s.Placements.Sum(p => (long)p.Material.Price);
            if (eval != null)
            {
                (s.Count, s.Quality) = eval.Evaluate(eval.BuildBoard(s.Placements), req.BaseCount, req.BaseQuality);
            }
            else
            {
                s.Count = Math.Max(0, req.BaseCount);
                s.Quality = Math.Max(0, Math.Min(3, req.BaseQuality));
            }
            if (!req.TopUp)
            {
                // 这套摆法按库存能连续炼几次
                long b = long.MaxValue;
                foreach (var g in s.Placements.GroupBy(p => p.Material))
                {
                    b = Math.Min(b, g.Key.Stock / g.Count());
                }
                s.Batches = s.Placements.Count == 0 ? 0 : (int)Math.Min(int.MaxValue, b);
            }
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

        void BuildUnits()
        {
            var mats = req.Materials;
            var needDims = Enumerable.Range(0, Attr.Count).Where(d => req.Need[d] != 0).ToList();
            var byKey = new Dictionary<string, Unit>();
            for (int i = 0; i < mats.Count; i++)
            {
                var m = mats[i];
                bool usable = m.Stock > 0 || (req.TopUp && m.Buyable);
                if (!usable || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                {
                    continue;
                }
                bool pure = Enumerable.Range(0, Attr.Count).All(d => m.Attrs[d] == 0 || needDims.Contains(d));
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
                        unit = new Unit { Side = side, Contrib = contrib, Size = m.Shape.Count, Type = type, Grade = m.Grade, Pure = pure };
                        byKey[key] = unit;
                        allUnits.Add(unit);
                    }
                    unit.Mats.Add(i);
                    if (req.TopUp && m.Buyable && (unit.BuyPrice < 0 || m.Price < unit.BuyPrice))
                    {
                        unit.BuyPrice = m.Price;
                    }
                }
            }
            foreach (var u in allUnits)
            {
                // 类内：便宜的先用；补料时先用已有的
                u.Mats = u.Mats.OrderByDescending(i => req.TopUp && mats[i].Stock > 0).ThenBy(i => mats[i].Price).ToList();
                if (eval != null)
                {
                    u.Affinity = eval.Affinity(mats[u.Mats[0]], req.Mode == DrugMode.MaxQuality);
                }
            }
        }

        long Cap(Unit u, int batch)
        {
            long c = 0;
            foreach (int i in u.Mats)
            {
                c += req.Materials[i].Stock / batch;
            }
            if (req.TopUp && u.BuyPrice >= 0)
            {
                c = int.MaxValue;
            }
            return c;
        }

        /// <summary>两轮：先只用属性都在丹方范围内的材料（快），再放开全部材料（可借无关属性互相抵消）。</summary>
        List<List<Unit>> Passes(IEnumerable<Unit> source)
        {
            var all = source.ToList();
            var pure = all.Where(u => u.Pure).ToList();
            // 更窄的第一轮：数值不超过丹方最大要求两倍的材料，类数少到可以用 3+3 折半，6 维丹方靠它
            int maxNeed = req.Need.Max(v => Math.Abs(v));
            var near = pure.Where(u => u.Contrib.All(v => Math.Abs(v) <= 2 * maxNeed)).ToList();
            var list = new List<List<Unit>>();
            if (near.Count > 0 && near.Count < pure.Count)
            {
                list.Add(near);
            }
            if (pure.Count > 0)
            {
                list.Add(pure);
            }
            if (all.Count > pure.Count)
            {
                list.Add(all);
            }
            return list;
        }

        // ───────────────────────── 组合搜索 ─────────────────────────

        /// <summary>一次搜索的上下文：一组材料类 + 批数，内含折半表和剪枝表。</summary>
        class Space
        {
            public List<Unit> Units;

            public int Batch;

            public long[] Caps;

            /// <summary>同一批材料在另一侧的类（阴阳共用库存），没有为 -1。</summary>
            public int[] Twin;

            public bool Over(int[] cnt, int u)
            {
                int t = Twin[u];
                return cnt[u] + (t >= 0 ? cnt[t] : 0) > Caps[u];
            }

            // 折半表：所有 ≤ H 件的多重集合，按属性和 + 件数哈希
            public int H;

            public int EntryCount;

            public int[] EntrySum;      // EntryCount × 8

            public int[] EntryUnits;    // EntryCount × H，不足补 -1

            public int[] EntrySize;

            public int[] EntryAreaYang, EntryAreaYin;

            public Dictionary<long, int> Head = new Dictionary<long, int>();

            public int[] Next;

            public List<int>[] BySize;

            // DFS 用
            public List<int>[] PosUnits, NegUnits;

            public int[] MaxPos, MaxNeg;

            public double MaxEff;

            public int MinSize;

            // [side][d]：每格最多能把第 d 维往正/负推多少（小丹炉时这个界很紧）
            public double[][] PosPerCell, NegPerCell;

            public int[][] Dist1d;

            public int[] Band;

            // 小丹炉几何先行：能放进去的形状多重集合（向下封闭），用 Zobrist 哈希表示
            public HashSet<long>[] Packable = new HashSet<long>[3];

            public long[,,] Zobrist;
        }

        int combosTried;

        bool anyNoFit;

        Space BuildSpace(List<Unit> units, int batch)
        {
            var sp = new Space { Units = units, Batch = batch, Caps = units.Select(u => Cap(u, batch)).ToArray() };
            int n = units.Count;
            sp.Twin = Enumerable.Repeat(-1, n).ToArray();
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++)
                {
                    if (units[a].Side != units[b].Side && units[a].Mats.Intersect(units[b].Mats).Any())
                    {
                        sp.Twin[a] = b;
                        sp.Caps[a] = Math.Max(sp.Caps[a], sp.Caps[b]);
                        break;
                    }
                }
            }
            sp.MinSize = n == 0 ? 1 : units.Min(u => u.Size);
            BuildDfsTables(sp);
            BuildGeometry(sp);
            BuildHalfTable(sp);
            Debug?.Invoke($"space n={n} batch={batch} H={sp.H} entries={sp.EntryCount} geo={(sp.Packable[1]?.Count.ToString() ?? "-")}/{(sp.Packable[2]?.Count.ToString() ?? "-")} t={clock.ElapsedMilliseconds}");
            return sp;
        }

        static long Hash(int[] v, int off, int size)
        {
            unchecked
            {
                long h = 1469598103934665603L ^ size;
                for (int d = 0; d < Attr.Count; d++)
                {
                    h = (h ^ v[off + d]) * 1099511628211L;
                }
                return h;
            }
        }

        void BuildHalfTable(Space sp)
        {
            int n = sp.Units.Count;
            long c1 = n + 1, c2 = (long)n * (n + 1) / 2, c3 = (long)n * (n + 1) * (n + 2) / 6;
            const long limit = 400000;
            sp.H = c1 + c2 + c3 <= limit ? 3 : (c1 + c2 <= limit ? 2 : 1);
            int h = sp.H;
            var sums = new List<int>();
            var unitsL = new List<int>();
            var sizes = new List<int>();
            var ay = new List<int>();
            var an = new List<int>();
            var cur = new int[Attr.Count];
            var pick = new int[h];
            var cnt = new int[n];
            void Emit(int size, int areaY, int areaN)
            {
                for (int d = 0; d < Attr.Count; d++)
                {
                    sums.Add(cur[d]);
                }
                for (int i = 0; i < h; i++)
                {
                    unitsL.Add(i < size ? pick[i] : -1);
                }
                sizes.Add(size);
                ay.Add(areaY);
                an.Add(areaN);
            }
            void Rec(int start, int size, int areaY, int areaN)
            {
                Emit(size, areaY, areaN);
                if (size == h)
                {
                    return;
                }
                for (int u = start; u < n; u++)
                {
                    var unit = sp.Units[u];
                    cnt[u]++;
                    if (sp.Over(cnt, u))
                    {
                        cnt[u]--;
                        continue;
                    }
                    cnt[u]--;
                    int y2 = areaY + (unit.Side == Side.Yang ? unit.Size : 0);
                    int n2 = areaN + (unit.Side == Side.Yin ? unit.Size : 0);
                    if (y2 > capYang || n2 > capYin)
                    {
                        continue;
                    }
                    cnt[u]++;
                    pick[size] = u;
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        cur[d] += unit.Contrib[d];
                    }
                    if (GeometryOk(sp, cnt, unit))
                    {
                        Rec(u, size + 1, y2, n2);
                    }
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        cur[d] -= unit.Contrib[d];
                    }
                    cnt[u]--;
                }
            }
            Rec(0, 0, 0, 0);
            sp.EntryCount = sizes.Count;
            sp.EntrySum = sums.ToArray();
            sp.EntryUnits = unitsL.ToArray();
            sp.EntrySize = sizes.ToArray();
            sp.EntryAreaYang = ay.ToArray();
            sp.EntryAreaYin = an.ToArray();
            sp.Next = new int[sp.EntryCount];
            sp.BySize = Enumerable.Range(0, h + 1).Select(_ => new List<int>()).ToArray();
            for (int e = 0; e < sp.EntryCount; e++)
            {
                long key = Hash(sp.EntrySum, e * Attr.Count, sp.EntrySize[e]);
                sp.Next[e] = sp.Head.TryGetValue(key, out int head) ? head : -1;
                sp.Head[key] = e;
                sp.BySize[sp.EntrySize[e]].Add(e);
            }
        }

        // 只按形状数一侧的多重集合是否装得下（小丹炉才有表）
        bool GeometryOk(Space sp, int[] cnt, Unit changed)
        {
            int pool = (int)changed.Side;
            var set = sp.Packable[pool];
            if (set == null)
            {
                return true;
            }
            long h = 0;
            var tc = new int[typeShapes.Count];
            for (int u = 0; u < cnt.Length; u++)
            {
                if (cnt[u] > 0 && sp.Units[u].Side == changed.Side)
                {
                    tc[sp.Units[u].Type] += cnt[u];
                }
            }
            for (int t = 0; t < tc.Length; t++)
            {
                if (tc[t] > 0)
                {
                    if (tc[t] >= sp.Zobrist.GetLength(2))
                    {
                        return false;
                    }
                    h ^= sp.Zobrist[pool, t, tc[t]];
                }
            }
            return set.Contains(h);
        }

        void BuildGeometry(Space sp)
        {
            int types = typeShapes.Count;
            var rng = new Random(12345);
            sp.Zobrist = new long[3, Math.Max(1, types), 17];
            for (int a = 0; a < 3; a++)
            {
                for (int t = 0; t < types; t++)
                {
                    for (int c = 0; c < 17; c++)
                    {
                        sp.Zobrist[a, t, c] = ((long)rng.Next() << 32) ^ rng.Next() ^ ((long)rng.Next() << 16);
                    }
                }
            }
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var grid = side == Side.Yang ? req.Yang : req.Yin;
                if (grid == null || grid.Area > 16)
                {
                    continue;
                }
                var present = sp.Units.Where(u => u.Side == side).Select(u => u.Type).Distinct().OrderByDescending(t => typeShapes[t].Count).ToList();
                var set = new HashSet<long> { 0 };
                var counts = new int[types];
                var watch = Stopwatch.StartNew();
                bool aborted = false;
                int pool = (int)side;
                void Rec(int ti, int area, long h)
                {
                    if (aborted)
                    {
                        return;
                    }
                    for (int k = ti; k < present.Count; k++)
                    {
                        int t = present[k];
                        int sz = typeShapes[t].Count;
                        if (area + sz > grid.Area || counts[t] + 1 >= 17)
                        {
                            continue;
                        }
                        long h2 = h;
                        if (counts[t] > 0)
                        {
                            h2 ^= sp.Zobrist[pool, t, counts[t]];
                        }
                        counts[t]++;
                        h2 ^= sp.Zobrist[pool, t, counts[t]];
                        var shapes = new List<List<Cell>>();
                        for (int q = 0; q < types; q++)
                        {
                            for (int r = 0; r < counts[q]; r++)
                            {
                                shapes.Add(typeShapes[q]);
                            }
                        }
                        if (set.Contains(h2) || PackFeasible(grid, side, shapes))
                        {
                            set.Add(h2);
                            Rec(k, area + sz, h2);
                        }
                        counts[t]--;
                        if (watch.ElapsedMilliseconds > 300)
                        {
                            aborted = true;
                            return;
                        }
                    }
                }
                Rec(0, 0, 0);
                if (!aborted)
                {
                    sp.Packable[pool] = set;
                }
            }
        }

        readonly Dictionary<string, bool> packCache = new Dictionary<string, bool>();

        bool PackFeasible(Grid grid, Side side, List<List<Cell>> shapes)
        {
            string key = (int)side + ":" + string.Join(",", shapes.Select(TypeOf).OrderBy(t => t));
            if (packCache.TryGetValue(key, out bool ok))
            {
                return ok;
            }
            var r = Packer.Pack(grid, shapes, clock, (int)Math.Max(clock.ElapsedMilliseconds + 200, deadline));
            if (!r.TimedOut)
            {
                packCache[key] = r.Ok;
            }
            return r.Ok;
        }

        void BuildDfsTables(Space sp)
        {
            var units = sp.Units;
            sp.PosUnits = new List<int>[Attr.Count];
            sp.NegUnits = new List<int>[Attr.Count];
            sp.MaxPos = new int[Attr.Count];
            sp.MaxNeg = new int[Attr.Count];
            for (int d = 0; d < Attr.Count; d++)
            {
                sp.PosUnits[d] = new List<int>();
                sp.NegUnits[d] = new List<int>();
                for (int u = 0; u < units.Count; u++)
                {
                    int c = units[u].Contrib[d];
                    if (c > 0)
                    {
                        sp.PosUnits[d].Add(u);
                        sp.MaxPos[d] = Math.Max(sp.MaxPos[d], c);
                    }
                    else if (c < 0)
                    {
                        sp.NegUnits[d].Add(u);
                        sp.MaxNeg[d] = Math.Max(sp.MaxNeg[d], -c);
                    }
                }
                int dd = d;
                sp.PosUnits[d] = sp.PosUnits[d].OrderByDescending(u => units[u].Contrib[dd]).ThenBy(u => units[u].Size).ToList();
                sp.NegUnits[d] = sp.NegUnits[d].OrderBy(u => units[u].Contrib[dd]).ThenBy(u => units[u].Size).ToList();
            }
            sp.PosPerCell = new double[3][];
            sp.NegPerCell = new double[3][];
            for (int side = 1; side <= 2; side++)
            {
                sp.PosPerCell[side] = new double[Attr.Count];
                sp.NegPerCell[side] = new double[Attr.Count];
                foreach (var u in units.Where(x => (int)x.Side == side))
                {
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        double v = (double)u.Contrib[d] / u.Size;
                        sp.PosPerCell[side][d] = Math.Max(sp.PosPerCell[side][d], v);
                        sp.NegPerCell[side][d] = Math.Max(sp.NegPerCell[side][d], -v);
                    }
                }
            }
            sp.MaxEff = 0;
            foreach (var u in units)
            {
                double eff = 0;
                for (int d = 0; d < Attr.Count; d++)
                {
                    int c = u.Contrib[d];
                    if (c > 0)
                    {
                        eff += (double)c / sp.MaxPos[d];
                    }
                    else if (c < 0)
                    {
                        eff += (double)-c / sp.MaxNeg[d];
                    }
                }
                sp.MaxEff = Math.Max(sp.MaxEff, eff);
            }
            // 单维下界表：只看第 d 维，凑出余量 v 至少要几件（忽略库存与其它维）。-1 表示凑不出
            sp.Dist1d = new int[Attr.Count][];
            sp.Band = new int[Attr.Count];
            for (int d = 0; d < Attr.Count; d++)
            {
                int dd = d;
                var coins = units.Select(u => u.Contrib[dd]).Where(c => c != 0).Distinct().ToArray();
                int maxCoin = Math.Max(sp.MaxPos[d], sp.MaxNeg[d]);
                int b = Math.Abs(req.Need[d]) + 2 * maxCoin;
                if (b > 200000)
                {
                    continue;
                }
                sp.Band[d] = b;
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
                sp.Dist1d[d] = dist;
            }
        }

        int MaxDepth(Space sp)
        {
            return Math.Min(req.MaxPieces, (capYang + capYin) / Math.Max(1, sp.MinSize));
        }

        /// <summary>恰好 k 件、属性正好凑齐的组合（每类用几件），最多 cap 个。</summary>
        List<int[]> Combos(Space sp, int k, int cap)
        {
            var found = new List<int[]>();
            if (k <= 2 * sp.H)
            {
                HalfLookup(sp, k, cap, found);
            }
            else
            {
                var usage = new int[sp.Units.Count];
                var seen = new HashSet<string>();
                Dfs(sp, req.Need.ToArray(), k, usage, 0, 0, found, seen, cap);
            }
            combosTried += found.Count;
            Debug?.Invoke($"  k={k} found={found.Count} t={clock.ElapsedMilliseconds}");
            return found;
        }

        void HalfLookup(Space sp, int k, int cap, List<int[]> found)
        {
            int b = k / 2, a = k - b;
            if (a > sp.H)
            {
                return;
            }
            int n = sp.Units.Count;
            var target = new int[Attr.Count];
            var seen = new HashSet<string>();
            var cnt = new int[n];
            foreach (int ea in sp.BySize[a])
            {
                if (found.Count >= cap || Late())
                {
                    return;
                }
                for (int d = 0; d < Attr.Count; d++)
                {
                    target[d] = req.Need[d] - sp.EntrySum[ea * Attr.Count + d];
                }
                if (!sp.Head.TryGetValue(Hash(target, 0, b), out int eb))
                {
                    continue;
                }
                for (; eb >= 0; eb = sp.Next[eb])
                {
                    if (sp.EntrySize[eb] != b || (a == b && eb < ea))
                    {
                        continue;
                    }
                    bool same = true;
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        if (sp.EntrySum[eb * Attr.Count + d] != target[d])
                        {
                            same = false;
                            break;
                        }
                    }
                    if (!same)
                    {
                        continue;
                    }
                    if (sp.EntryAreaYang[ea] + sp.EntryAreaYang[eb] > capYang || sp.EntryAreaYin[ea] + sp.EntryAreaYin[eb] > capYin)
                    {
                        continue;
                    }
                    var picked = new List<int>(k);
                    for (int i = 0; i < sp.H; i++)
                    {
                        int ua = sp.EntryUnits[ea * sp.H + i], ub = sp.EntryUnits[eb * sp.H + i];
                        if (ua >= 0)
                        {
                            picked.Add(ua);
                        }
                        if (ub >= 0)
                        {
                            picked.Add(ub);
                        }
                    }
                    bool ok = true;
                    foreach (int u in picked)
                    {
                        cnt[u]++;
                    }
                    foreach (int u in picked)
                    {
                        if (sp.Over(cnt, u))
                        {
                            ok = false;
                        }
                    }
                    if (ok)
                    {
                        foreach (int u in picked.Distinct())
                        {
                            if (!GeometryOk(sp, cnt, sp.Units[u]))
                            {
                                ok = false;
                                break;
                            }
                        }
                    }
                    if (ok)
                    {
                        picked.Sort();
                        if (seen.Add(string.Join(",", picked)))
                        {
                            found.Add(cnt.ToArray());
                        }
                    }
                    foreach (int u in picked)
                    {
                        cnt[u]--;
                    }
                    if (found.Count >= cap)
                    {
                        return;
                    }
                }
            }
        }

        bool Late()
        {
            if (clock.ElapsedMilliseconds > deadline)
            {
                timedOut = true;
            }
            return timedOut;
        }

        void Dfs(Space sp, int[] r, int left, int[] usage, int areaYang, int areaYin, List<int[]> found, HashSet<string> seen, int cap)
        {
            if (found.Count >= cap || Late())
            {
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
            // 最后几件直接查折半表，省掉最宽的几层
            if (left <= sp.H)
            {
                TailLookup(sp, r, left, usage, areaYang, areaYin, found, seen, cap);
                return;
            }
            // 剩余格子不够再放 left 件最小的材料
            if ((long)left * sp.MinSize > (capYang - areaYang) + (capYin - areaYin))
            {
                return;
            }
            // 面积界：剩下的格子全塞「每格最强」的材料也推不动余量就剪掉
            int freeY = capYang - areaYang, freeN = capYin - areaYin;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (r[d] > 0 ? freeY * sp.PosPerCell[1][d] + freeN * sp.PosPerCell[2][d] < r[d] - 1e-9
                             : r[d] < 0 && freeY * sp.NegPerCell[1][d] + freeN * sp.NegPerCell[2][d] < -r[d] - 1e-9)
                {
                    return;
                }
            }
            // 界：每个维度剩余件数推得动余量；同时挑候选最少的维度分支
            int best = -1, bestCount = int.MaxValue;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (r[d] > 0)
                {
                    if ((long)sp.MaxPos[d] * left < r[d])
                    {
                        return;
                    }
                    if (sp.PosUnits[d].Count < bestCount)
                    {
                        best = d;
                        bestCount = sp.PosUnits[d].Count;
                    }
                }
                else if (r[d] < 0)
                {
                    if ((long)sp.MaxNeg[d] * left < -r[d])
                    {
                        return;
                    }
                    if (sp.NegUnits[d].Count < bestCount)
                    {
                        best = d;
                        bestCount = sp.NegUnits[d].Count;
                    }
                }
            }
            // 势函数下界：Σ|余量|/单步最大值，每件材料最多减少 maxEff
            double pot = 0;
            for (int d = 0; d < Attr.Count; d++)
            {
                if (r[d] > 0)
                {
                    pot += (double)r[d] / sp.MaxPos[d];
                }
                else if (r[d] < 0)
                {
                    pot += (double)-r[d] / sp.MaxNeg[d];
                }
            }
            if (pot > sp.MaxEff * left + 1e-9)
            {
                return;
            }
            for (int d = 0; d < Attr.Count; d++)
            {
                var dist = sp.Dist1d[d];
                int b = sp.Band[d];
                if (r[d] == 0 || dist == null || Math.Abs(r[d]) > b - Math.Max(sp.MaxPos[d], sp.MaxNeg[d]))
                {
                    continue;
                }
                int need = dist[r[d] + b];
                if (need < 0 || need > left)
                {
                    return;
                }
            }
            var cands = r[best] > 0 ? sp.PosUnits[best] : sp.NegUnits[best];
            foreach (int u in cands)
            {
                var unit = sp.Units[u];
                usage[u]++;
                bool over = sp.Over(usage, u);
                usage[u]--;
                if (over)
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
                if (GeometryOk(sp, usage, unit) && seen.Add(string.Join(",", usage)))
                {
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        r[d] -= unit.Contrib[d];
                    }
                    Dfs(sp, r, left - 1, usage, ay, an, found, seen, cap);
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        r[d] += unit.Contrib[d];
                    }
                }
                usage[u]--;
                if (found.Count >= cap || timedOut)
                {
                    return;
                }
            }
        }

        void TailLookup(Space sp, int[] r, int left, int[] usage, int areaYang, int areaYin, List<int[]> found, HashSet<string> seen, int cap)
        {
            if (!sp.Head.TryGetValue(Hash(r, 0, left), out int e))
            {
                return;
            }
            for (; e >= 0 && found.Count < cap; e = sp.Next[e])
            {
                if (sp.EntrySize[e] != left || areaYang + sp.EntryAreaYang[e] > capYang || areaYin + sp.EntryAreaYin[e] > capYin)
                {
                    continue;
                }
                bool same = true;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (sp.EntrySum[e * Attr.Count + d] != r[d])
                    {
                        same = false;
                        break;
                    }
                }
                if (!same)
                {
                    continue;
                }
                int added = 0;
                bool ok = true;
                for (int i = 0; i < sp.H; i++)
                {
                    int u = sp.EntryUnits[e * sp.H + i];
                    if (u < 0)
                    {
                        break;
                    }
                    usage[u]++;
                    added = i + 1;
                    if (sp.Over(usage, u) || !GeometryOk(sp, usage, sp.Units[u]))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok && seen.Add(string.Join(",", usage)))
                {
                    found.Add(usage.ToArray());
                }
                for (int i = 0; i < added; i++)
                {
                    usage[sp.EntryUnits[e * sp.H + i]]--;
                }
            }
        }

        // ───────────────────────── 排序键 ─────────────────────────

        /// <summary>组合的偏好键（越小越好）：最高品阶、品阶和、价格。</summary>
        IComparable[] GradeKey(Space sp, int[] usage)
        {
            int maxGrade = 0, gradeSum = 0;
            long price = 0;
            for (int u = 0; u < usage.Length; u++)
            {
                if (usage[u] == 0)
                {
                    continue;
                }
                var unit = sp.Units[u];
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

        /// <summary>
        /// 件数最少：逐层找，找到第一层后在这一层（不够 TopK 再加一层）里按 key 排序取前几个能装下的。
        /// </summary>
        List<Solution> SolveMinPieces(List<List<Unit>> passes, int batch, long until, int extraLevels = 1)
        {
            var sols = new List<Solution>();
            var seen = new HashSet<string>();
            foreach (var set in passes)
            {
                var sp = BuildSpace(set, batch);
                int firstK = -1;
                for (int k = 1; k <= MaxDepth(sp) && clock.ElapsedMilliseconds < until; k++)
                {
                    if (firstK >= 0 && (k > firstK + extraLevels || sols.Count >= req.TopK))
                    {
                        break;
                    }
                    var combos = Combos(sp, k, MaxCombosPerDepth);
                    foreach (var c in combos.OrderBy(c => GradeKey(sp, c), Comparer<IComparable[]>.Create(CompareKeys)))
                    {
                        if (sols.Count >= req.TopK || clock.ElapsedMilliseconds > until)
                        {
                            break;
                        }
                        var sol = TryPack(sp, c);
                        if (sol == null)
                        {
                            anyNoFit = true;
                            continue;
                        }
                        if (seen.Add(UsageKey(sp, c)))
                        {
                            sol.Batches = batch;
                            sols.Add(sol);
                            if (firstK < 0)
                            {
                                firstK = k;
                            }
                        }
                    }
                }
                if (sols.Count > 0)
                {
                    break;
                }
            }
            return sols;
        }

        static string UsageKey(Space sp, int[] usage)
        {
            return string.Join(",", Enumerable.Range(0, usage.Length).Where(u => usage[u] > 0).Select(u => sp.Units[u].GetHashCode() + "x" + usage[u]).OrderBy(s => s));
        }

        /// <summary>低阶优先：最高品阶从低往高试，第一个有解的门槛内再按品阶和、价格排。</summary>
        List<Solution> SolveLowGrade()
        {
            var grades = allUnits.Select(u => u.Grade).Distinct().OrderBy(g => g).ToList();
            for (int i = 0; i < grades.Count; i++)
            {
                int g = grades[i];
                bool last = i == grades.Count - 1;
                long remain = deadline - clock.ElapsedMilliseconds;
                long until = last ? deadline : clock.ElapsedMilliseconds + Math.Max(50, remain / (grades.Count - i));
                var passes = Passes(allUnits.Where(u => u.Grade <= g));
                var sols = SolveMinPieces(passes, 1, until, 2);
                if (sols.Count > 0)
                {
                    return sols.OrderBy(s => KeyOf(s), Comparer<IComparable[]>.Create(CompareKeys)).ToList();
                }
                timedOut = false;
                if (clock.ElapsedMilliseconds > deadline)
                {
                    timedOut = true;
                    break;
                }
            }
            return new List<Solution>();
        }

        static IComparable[] KeyOf(Solution s)
        {
            return new IComparable[] { s.Placements.Max(p => p.Material.Grade), s.Placements.Sum(p => p.Material.Grade), s.Placements.Sum(p => (long)p.Material.Price) };
        }

        /// <summary>最多可炼次数：批数可行性是单调的，二分找最大批数。</summary>
        List<Solution> SolveMaxBatches()
        {
            var passes = Passes(allUnits);
            var first = SolveMinPieces(passes, 1, deadline / 4, 0);
            if (first.Count == 0)
            {
                return first;
            }
            int lo = 1;
            int hi = (int)Math.Min(100000, req.Materials.Where(m => m.Stock > 0).Max(m => m.Stock));
            var best = first;
            int steps = (int)Math.Ceiling(Math.Log(Math.Max(2, hi), 2)) + 1;
            while (lo < hi && clock.ElapsedMilliseconds < deadline)
            {
                int mid = lo + (hi - lo + 1) / 2;
                long slice = Math.Max(60, (deadline - clock.ElapsedMilliseconds) / Math.Max(1, steps--));
                timedOut = false;
                var sols = SolveMinPieces(passes, mid, clock.ElapsedMilliseconds + slice, 0);
                if (sols.Count > 0)
                {
                    lo = mid;
                    best = sols;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            timedOut = false;
            return best;
        }

        /// <summary>
        /// 最多成丹 / 最高品质：多收几层候选组合，按「材料本身符合规则的件数」排序，
        /// 每个能装下的都调一下摆位，取分数最高的几个。
        /// </summary>
        List<Solution> SolvePlacementModes(DrugMode mode)
        {
            long collectUntil = deadline * 35 / 100;
            var cands = new List<(Space sp, int[] usage, int k, int aff)>();
            foreach (var set in Passes(allUnits))
            {
                var sp = BuildSpace(set, 1);
                int firstK = -1;
                for (int k = 1; k <= MaxDepth(sp) && clock.ElapsedMilliseconds < collectUntil; k++)
                {
                    if (firstK >= 0 && k > firstK + 3)
                    {
                        break;
                    }
                    var combos = Combos(sp, k, MaxCombosPerDepth);
                    if (combos.Count > 0 && firstK < 0)
                    {
                        firstK = k;
                    }
                    foreach (var c in combos)
                    {
                        int aff = 0;
                        for (int u = 0; u < c.Length; u++)
                        {
                            aff += c[u] * sp.Units[u].Affinity;
                        }
                        cands.Add((sp, c, k, aff));
                    }
                }
                if (cands.Count > 0)
                {
                    break;
                }
            }
            timedOut = false;
            var scored = new List<(Solution sol, long score, string key)>();
            var seen = new HashSet<string>();
            foreach (var c in cands.OrderByDescending(c => c.aff).ThenBy(c => c.k))
            {
                if (clock.ElapsedMilliseconds > deadline * 80 / 100 && scored.Count > 0)
                {
                    break;
                }
                if (clock.ElapsedMilliseconds > deadline)
                {
                    break;
                }
                var sol = TryPack(c.sp, c.usage);
                if (sol == null)
                {
                    anyNoFit = true;
                    continue;
                }
                string key = UsageKey(c.sp, c.usage);
                if (!seen.Add(key))
                {
                    continue;
                }
                long score = Optimize(sol, clock.ElapsedMilliseconds + 25, mode);
                scored.Add((sol, score, key));
            }
            // 前几名再多调一会儿
            var top = scored.OrderByDescending(s => s.score).ThenBy(s => s.sol.PieceCount).Take(req.TopK).ToList();
            var result = new List<(Solution sol, long score)>();
            int seed = 7;
            var pairs = PadPairs(mode);
            for (int ti = 0; ti < top.Count; ti++)
            {
                var t = top[ti];
                // 第一名拿大头时间（垫料越多越慢），其余各分一点
                long left = req.TimeLimitMs + 400 - clock.ElapsedMilliseconds;
                long share = ti == 0 && top.Count > 1 ? left * 6 / 10 : left / Math.Max(1, top.Count - ti);
                long until = Math.Min(clock.ElapsedMilliseconds + Math.Max(30, share), req.TimeLimitMs + 400);
                long s = pairs.Count > 0 ? Pad(t.sol, pairs, until, mode, seed++) : Optimize(t.sol, until, mode, seed++);
                result.Add((t.sol, s));
            }
            return result.OrderByDescending(r => r.score).ThenBy(r => r.sol.PieceCount).Select(r => r.sol).ToList();
        }

        /// <summary>
        /// 垫料候选：阳格放 A、阴格放 B，A 与 B 属性完全相同，放上去属性净变化为 0，只为多满足药性规则。
        /// 只留材料本身符合规则目标的（例如通法丹的火系、金系灵草）。
        /// </summary>
        List<(Material yang, Material yin)> PadPairs(DrugMode mode)
        {
            bool forQuality = mode == DrugMode.MaxQuality;
            var mats = req.Materials.Where(m => m.Stock > 0 && m.Shape.Count > 0 && m.Attrs.Any(v => v != 0)).ToList();
            var list = new List<(Material, Material, int)>();
            foreach (var g in mats.GroupBy(m => string.Join(",", m.Attrs)))
            {
                var yang = g.Where(m => req.Yang != null && Packer.CanFit(req.Yang, m.Shape)).ToList();
                var yin = g.Where(m => req.Yin != null && Packer.CanFit(req.Yin, m.Shape)).ToList();
                foreach (var a in yang)
                {
                    foreach (var b in yin)
                    {
                        if (a == b && a.Stock < 2)
                        {
                            continue;
                        }
                        int aff = eval.Affinity(a, forQuality) + eval.Affinity(b, forQuality);
                        if (aff > 0)
                        {
                            list.Add((a, b, aff * 100 - a.Shape.Count - b.Shape.Count));
                        }
                    }
                }
            }
            return list.OrderByDescending(x => x.Item3).Take(12).Select(x => (x.Item1, x.Item2)).ToList();
        }

        long Pad(Solution sol, List<(Material yang, Material yin)> pairs, long until, DrugMode mode, int seed)
        {
            var remaining = req.Materials.ToDictionary(m => m, m => m.Stock);
            foreach (var p in sol.Placements)
            {
                remaining[p.Material]--;
            }
            var opt = new LayoutOptimizer(eval, req.Yang, req.Yin, req.BaseCount, req.BaseQuality, (c, q) => Objective(mode, c, q));
            var (cnt, qual) = opt.Pad(sol, pairs, remaining, clock, until, seed);
            return Objective(mode, cnt, qual);
        }

        long Objective(DrugMode mode, int count, int quality)
        {
            return mode == DrugMode.MaxCount ? count * 10L + quality : quality * 100000L + count;
        }

        long Optimize(Solution sol, long until, DrugMode mode, int seed = 1)
        {
            if (eval == null)
            {
                return 0;
            }
            var opt = new LayoutOptimizer(eval, req.Yang, req.Yin, req.BaseCount, req.BaseQuality, (c, q) => Objective(mode, c, q));
            var (cnt, qual) = opt.Optimize(sol, clock, until, seed);
            return Objective(mode, cnt, qual);
        }

        /// <summary>
        /// 无解时的补料建议：已有材料成本 0，可买材料按单价计，一次求「精确凑齐且购买花费最少」。
        /// </summary>
        List<Solution> SolveTopUp()
        {
            var cands = new List<(Space sp, int[] usage, long cost, int k)>();
            long collectUntil = deadline * 60 / 100;
            foreach (var set in Passes(allUnits))
            {
                var sp = BuildSpace(set, 1);
                int firstK = -1;
                for (int k = 1; k <= MaxDepth(sp) && clock.ElapsedMilliseconds < collectUntil; k++)
                {
                    if (firstK >= 0 && k > firstK + 3)
                    {
                        break;
                    }
                    var combos = Combos(sp, k, MaxCombosPerDepth * 3);
                    if (combos.Count > 0 && firstK < 0)
                    {
                        firstK = k;
                    }
                    foreach (var c in combos)
                    {
                        cands.Add((sp, c, BuyCost(sp, c), k));
                    }
                }
                if (cands.Count > 0)
                {
                    break;
                }
            }
            timedOut = false;
            var sols = new List<Solution>();
            var seen = new HashSet<string>();
            foreach (var c in cands.OrderBy(c => c.cost).ThenBy(c => c.k))
            {
                if (sols.Count >= req.TopK || clock.ElapsedMilliseconds > deadline)
                {
                    break;
                }
                var sol = TryPack(c.sp, c.usage);
                if (sol == null)
                {
                    anyNoFit = true;
                    continue;
                }
                if (seen.Add(UsageKey(c.sp, c.usage)))
                {
                    sols.Add(sol);
                }
            }
            return sols;
        }

        long BuyCost(Space sp, int[] usage)
        {
            long cost = 0;
            for (int u = 0; u < usage.Length; u++)
            {
                if (usage[u] == 0)
                {
                    continue;
                }
                var unit = sp.Units[u];
                long owned = unit.Mats.Sum(i => req.Materials[i].Stock);
                long buy = Math.Max(0, usage[u] - owned);
                if (buy > 0)
                {
                    cost += buy * Math.Max(0, unit.BuyPrice);
                }
            }
            return cost;
        }

        // ───────────────────────── 装箱 ─────────────────────────

        Solution TryPack(Space sp, int[] usage)
        {
            var sol = new Solution();
            var left = req.Materials.Select(m => m.Stock / Math.Max(1, sp.Batch)).ToArray();
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var pieces = new List<Material>();
                for (int u = 0; u < usage.Length; u++)
                {
                    if (sp.Units[u].Side != side)
                    {
                        continue;
                    }
                    int need = usage[u];
                    foreach (int mi in sp.Units[u].Mats)
                    {
                        while (need > 0 && left[mi] > 0)
                        {
                            pieces.Add(req.Materials[mi]);
                            left[mi]--;
                            need--;
                        }
                    }
                    if (need > 0 && !req.TopUp)
                    {
                        // 阴阳两侧共用的材料不够分
                        return null;
                    }
                    if (need > 0 && req.TopUp)
                    {
                        // 不够的部分买类内最便宜的可买材料
                        var m = sp.Units[u].Mats.Select(i => req.Materials[i]).Where(x => x.Buyable).OrderBy(x => x.Price).First();
                        sol.ToBuy[m] = (sol.ToBuy.TryGetValue(m, out int b) ? b : 0) + need;
                        sol.BuyCost += (long)m.Price * need;
                        for (; need > 0; need--)
                        {
                            pieces.Add(m);
                        }
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
                long bought = sol.ToBuy.TryGetValue(g.Key, out int b) ? b : 0;
                if (g.LongCount() - bought > g.Key.Stock)
                {
                    return $"{g.Key.Name} 库存不足";
                }
            }
            return null;
        }
    }
}
