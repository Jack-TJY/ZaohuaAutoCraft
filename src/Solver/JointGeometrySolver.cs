using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.Sat;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 按形状类型联合建模数量与几何位置（报告 §6）。
    /// 材料按旋转等价形状归类；位置布尔变量按「类型×盘×合法位置」枚举，而不是逐材料枚举。
    /// 这样面积可行性与真实可摆放性在同一个模型里，避免「数量解装不下」。
    /// </summary>
    public static class JointGeometrySolver
    {
        public class Candidate
        {
            /// <summary>解出的每件材料：材料、阴阳盘、相对盘内坐标(x,y)、旋转。</summary>
            public List<(Material mat, Side side, int x, int y, int rot)> Items = new List<(Material, Side, int, int, int)>();

            public int[] Level = new int[0];

            public string Status = "";

            public long ElapsedMs;
        }

        static readonly Dictionary<string, List<Plc>> placeCache = new Dictionary<string, List<Plc>>();

        class Plc
        {
            public int Type;
            public Side Side;
            public int X;
            public int Y;
            public int Rot;
            public List<Cell> Cells = new List<Cell>();
            public IntVar Var;
            public int AnchorX;
            public int AnchorY;
        }

        public static Candidate Solve(List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin,
            double timeLimitSec, int desiredAttr, int workers, int[] priorityGroup = null, MaterialPolicy policy = MaterialPolicy.MaxRepeatCount,
            int[] fixedLevel = null, Candidate hint = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new Candidate();
            var model = new CpModel();
            // 1. 激活材料：有库存、有形状、有非零属性，且属性参与某符文需求
            var needed = new bool[Attr.Count];
            foreach (var rn in runes)
            {
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (rn.Requirement[d] > 0)
                    {
                        needed[d] = true;
                    }
                }
            }
            var active = new List<int>();
            // 报告 P1：按稳定 ID 排序输入，避免同分结果因枚举顺序不同而漂移
            foreach (int i in Enumerable.Range(0, materials.Count).OrderBy(i => materials[i].Id, StringComparer.Ordinal))
            {
                var m = materials[i];
                if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                {
                    continue;
                }
                bool helps = false;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (needed[d] && m.Attrs[d] != 0)
                    {
                        helps = true;
                        break;
                    }
                }
                if (!helps && desiredAttr < 0)
                {
                    continue;
                }
                active.Add(i);
            }
            // 2. 形状类型归类
            var typeIndex = new Dictionary<string, int>();
            var typeOrients = new List<List<Packer.Orientation>>();
            var typeOf = new Dictionary<int, int>();
            foreach (int i in active)
            {
                var t = Packer.GetType(materials[i].Shape);
                if (!typeIndex.TryGetValue(t.Key, out var ti))
                {
                    ti = typeIndex.Count;
                    typeIndex[t.Key] = ti;
                    typeOrients.Add(t.Orientations);
                }
                typeOf[i] = ti;
            }
            // 3. 每类型每盘的合法位置（去重）
            var places = new List<List<Plc>>(); // 按 type*2+side 展开
            var placesByKey = new Dictionary<int, List<Plc>>();
            foreach (var kv in typeIndex.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                int ti = kv.Value;
                for (int s = 0; s < 2; s++)
                {
                    var grid = s == 0 ? yang : yin;
                    var list = new List<Plc>();
                    if (grid != null)
                    {
                        // 报告 §7.4：合法位置表按「规范形状 + 盘尺寸」缓存（不含 IntVar），跨求解复用
                        string ck = kv.Key + "|" + grid.Width + "x" + grid.Height;
                        if (placeCache.TryGetValue(ck, out var tmpl))
                        {
                            foreach (var t in tmpl)
                            {
                                list.Add(new Plc { Type = ti, Side = s == 0 ? Side.Yang : Side.Yin, X = t.X, Y = t.Y, Rot = t.Rot, Cells = t.Cells, AnchorX = t.AnchorX, AnchorY = t.AnchorY });
                            }
                        }
                        else
                        {
                            var seen = new HashSet<string>();
                            foreach (var ori in typeOrients[ti])
                            {
                                for (int ax = 0; ax < grid.Width; ax++)
                                {
                                    for (int ay = 0; ay < grid.Height; ay++)
                                    {
                                        var cells = ori.Offsets.Select(o => new Cell(ax + o.X, ay + o.Y)).ToList();
                                        if (!cells.All(c => c.X >= 0 && c.X < grid.Width && c.Y >= 0 && c.Y < grid.Height))
                                        {
                                            continue;
                                        }
                                        var key = ori.Rotate + "|" + string.Join(";", cells.Select(c => c.X + "," + c.Y).OrderBy(x => x, StringComparer.Ordinal));
                                        if (!seen.Add(key))
                                        {
                                            continue;
                                        }
                                        list.Add(new Plc { Type = ti, Side = s == 0 ? Side.Yang : Side.Yin, X = ax, Y = ay, Rot = ori.Rotate, Cells = cells, AnchorX = ori.Anchor.X, AnchorY = ori.Anchor.Y });
                                    }
                                }
                            }
                            placeCache[ck] = list.Select(p => new Plc { X = p.X, Y = p.Y, Rot = p.Rot, Cells = p.Cells, AnchorX = p.AnchorX, AnchorY = p.AnchorY }).ToList();
                        }
                    }
                    placesByKey[ti * 2 + s] = list;
                }
            }
            // 4. 数量变量 + 位置变量 + 链接
            var q = new IntVar[materials.Count, 2];
            foreach (int i in active)
            {
                int stock = (int)Math.Min(materials[i].Stock, 10000L);
                q[i, 0] = model.NewIntVar(0, stock, $"qy{i}");
                q[i, 1] = model.NewIntVar(0, stock, $"qn{i}");
                model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(q[i, 0], 1), LinearExpr.Term(q[i, 1], 1) }) <= stock);
                // 报告 §7.2：用已有合法方案做热启动提示（不是强制约束）
                if (hint != null && hint.Items.Count > 0)
                {
                    int hy = 0, hn = 0;
                    foreach (var it in hint.Items)
                    {
                        if (ReferenceEquals(it.mat, materials[i]))
                        {
                            if (it.side == Side.Yang) { hy++; } else { hn++; }
                        }
                    }
                    model.AddHint(q[i, 0], Math.Min(hy, stock));
                    model.AddHint(q[i, 1], Math.Min(hn, stock));
                }
            }
            var byTypeSide = new Dictionary<int, List<int>>(); // type*2+side -> active material indices
            foreach (int i in active)
            {
                for (int s = 0; s < 2; s++)
                {
                    int k = typeOf[i] * 2 + s;
                    if (!byTypeSide.TryGetValue(k, out var l0))
                    {
                        byTypeSide[k] = l0 = new List<int>();
                    }
                    l0.Add(i);
                }
            }
            var allPlaces = new List<Plc>();
            foreach (var kv in placesByKey.OrderBy(k => k.Key))
            {
                var list = kv.Value;
                var qList = new List<LinearExpr>();
                int side = kv.Key % 2;
                if (byTypeSide.TryGetValue(kv.Key, out var mats))
                {
                    foreach (int i in mats)
                    {
                        qList.Add(LinearExpr.Term(q[i, side], 1));
                    }
                }
                var zList = new List<LinearExpr>();
                foreach (var p in list)
                {
                    p.Var = model.NewBoolVar($"z{p.Type}_{side}_{p.X}_{p.Y}_{p.Rot}");
                    zList.Add(LinearExpr.Term(p.Var, 1));
                    allPlaces.Add(p);
                }
                model.Add(LinearExpr.Sum(qList) == LinearExpr.Sum(zList));
            }
            // 5. 每个格子最多一个占用（允许留空）
            for (int s = 0; s < 2; s++)
            {
                var grid = s == 0 ? yang : yin;
                if (grid == null)
                {
                    continue;
                }
                var byCell = new Dictionary<int, List<LinearExpr>>();
                foreach (var p in allPlaces)
                {
                    if ((p.Side == Side.Yang ? 0 : 1) != s)
                    {
                        continue;
                    }
                    foreach (var c in p.Cells)
                    {
                        int key = c.X * 1000 + c.Y;
                        if (!byCell.TryGetValue(key, out var l))
                        {
                            byCell[key] = l = new List<LinearExpr>();
                        }
                        l.Add(LinearExpr.Term(p.Var, 1));
                    }
                }
                foreach (var kv in byCell)
                {
                    model.Add(LinearExpr.Sum(kv.Value) <= 1);
                }
            }
            // 6. 属性 + 符文层数（逐层覆盖 + 阈值）
            var levelVar = new IntVar[runes.Count];
            var hasL = new IntVar[runes.Count][];
            for (int r = 0; r < runes.Count; r++)
            {
                var rune = runes[r];
                levelVar[r] = model.NewIntVar(0, rune.MaxStacks, $"lv{r}");
                if (hint != null && hint.Level.Length > r)
                {
                    model.AddHint(levelVar[r], hint.Level[r]);
                }
                hasL[r] = new IntVar[rune.MaxStacks + 1];
                for (int l = 1; l <= rune.MaxStacks; l++)
                {
                    hasL[r][l] = model.NewBoolVar($"h{r}_{l}");
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(hasL[r][l], -l), LinearExpr.Term(levelVar[r], 1) }) >= 0);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(hasL[r][l], -(rune.MaxStacks - l + 1)), LinearExpr.Term(levelVar[r], 1) }) <= l - 1);
                }
                // 阶段2：固定阶段1的质量（报告 §7.1 目标与阶段解耦）
                if (fixedLevel != null && r < fixedLevel.Length)
                {
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(levelVar[r], 1) }) == fixedLevel[r]);
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
                        if (a != 0)
                        {
                            terms.Add(LinearExpr.Term(q[i, 0], a));
                            terms.Add(LinearExpr.Term(q[i, 1], -a));
                        }
                    }
                    terms.Add(LinearExpr.Term(levelVar[r], -req));
                    terms.Add(LinearExpr.Term(hasL[r][1], -10000000L));
                    model.Add(LinearExpr.Sum(terms) >= -bonus[d] - 10000000L);
                }
            }
            // 7. 阶段目标（报告 §7.1）：质量阶段只优化优先组/逐层覆盖/等级；材料阶段在质量固定后优化策略/件数
            var obj = new List<LinearExpr>();
            if (fixedLevel == null)
            {
                int maxGroup = 0;
                for (int r = 0; r < runes.Count; r++)
                {
                    int g = priorityGroup != null && r < priorityGroup.Length ? Math.Max(0, priorityGroup[r]) : 0;
                    maxGroup = Math.Max(maxGroup, g);
                }
                for (int r = 0; r < runes.Count; r++)
                {
                    int g = priorityGroup != null && r < priorityGroup.Length ? Math.Max(0, priorityGroup[r]) : 0;
                    long gw = 1;
                    for (int k = 0; k < maxGroup - g; k++)
                    {
                        gw *= 10;
                    }
                    for (int l = 1; l <= runes[r].MaxStacks; l++)
                    {
                        obj.Add(LinearExpr.Term(hasL[r][l], gw * LayerWeight(l)));
                    }
                    obj.Add(LinearExpr.Term(levelVar[r], gw * 1000000L));
                }
            }
            else
            {
            if (policy == MaterialPolicy.MaxRepeatCount)
            {
                long maxStock = active.Count > 0 ? active.Max(i => Math.Max(1L, Math.Min(materials[i].Stock, 100000L))) : 1;
                var bVar = model.NewIntVar(1, Math.Max(1, maxStock), "batches");
                foreach (int i in active)
                {
                    long stock = Math.Min(materials[i].Stock, 10000L);
                    var usage = model.NewIntVar(0, stock, $"u{i}");
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(q[i, 0], 1), LinearExpr.Term(q[i, 1], 1), LinearExpr.Term(usage, -1) }) == 0);
                    var prod = model.NewIntVar(0, Math.Max(1, maxStock), $"p{i}");
                    model.AddMultiplicationEquality(prod, bVar, usage);
                    model.Add(LinearExpr.Term(prod, 1) <= stock);
                }
                obj.Add(LinearExpr.Term(bVar, 100000000L));
            }
            else
            {
                var grades = active.Select(i => materials[i].Grade).Distinct().OrderBy(g => g).ToList();
                var rank = new Dictionary<int, int>();
                for (int gi = 0; gi < grades.Count; gi++)
                {
                    rank[grades[gi]] = gi;
                }
                foreach (int i in active)
                {
                    long w = (rank.TryGetValue(materials[i].Grade, out var r2) ? r2 : 0) * 2000L + 1;
                    obj.Add(LinearExpr.Term(q[i, 0], -w));
                    obj.Add(LinearExpr.Term(q[i, 1], -w));
                }
            }
            foreach (int i in active)
            {
                obj.Add(LinearExpr.Term(q[i, 0], -1));
                obj.Add(LinearExpr.Term(q[i, 1], -1));
            }
            }
            // 可选成品属性：硬约束（与分解模型一致的简化判定）
            if (desiredAttr >= 0)
            {
                int ta = Math.Min(desiredAttr, Attr.Count - 1);
                if (ta == 0)
                {
                    // 金最大或并列最大（简化：只要求金不劣于其他，非加权维度）
                    for (int d = 1; d < 5; d++)
                    {
                        var terms = new List<LinearExpr>();
                        foreach (int i in active)
                        {
                            int a = materials[i].Attrs[ta];
                            if (a != 0) { terms.Add(LinearExpr.Term(q[i, 0], a)); terms.Add(LinearExpr.Term(q[i, 1], -a)); }
                            int b = materials[i].Attrs[d];
                            if (b != 0) { terms.Add(LinearExpr.Term(q[i, 0], -b)); terms.Add(LinearExpr.Term(q[i, 1], b)); }
                        }
                        model.Add(LinearExpr.Sum(terms) >= (long)bonus[d] - bonus[ta]);
                    }
                }
                else
                {
                    int wTa = (ta == 5 || ta == 6 || ta == 7) ? 10 : 1;
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        if (d == ta) continue;
                        int wD = (d == 5 || d == 6 || d == 7) ? 10 : 1;
                        var terms = new List<LinearExpr>();
                        foreach (int i in active)
                        {
                            int aTa = materials[i].Attrs[ta] * wTa;
                            if (aTa != 0) { terms.Add(LinearExpr.Term(q[i, 0], aTa)); terms.Add(LinearExpr.Term(q[i, 1], -aTa)); }
                            int aD = materials[i].Attrs[d] * wD;
                            if (aD != 0) { terms.Add(LinearExpr.Term(q[i, 0], -aD)); terms.Add(LinearExpr.Term(q[i, 1], aD)); }
                        }
                        long delta = (d < ta || d < 5) ? 1L : 0L;
                        long rhs = delta + (long)bonus[d] * wD - (long)bonus[ta] * wTa;
                        model.Add(LinearExpr.Sum(terms) >= rhs);
                    }
                }
            }
            model.Maximize(LinearExpr.Sum(obj));
            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeLimitSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},num_search_workers:{(CpSatQuantity.Deterministic ? 1 : Math.Max(1, workers))},random_seed:0";
            var status = solver.Solve(model);
            res.Status = status.ToString();
            res.ElapsedMs = sw.ElapsedMilliseconds;
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                return res;
            }
            // 8. 提取：每个类型把 q 件分配给具体材料实例（按材料顺序）并把位置回填
            var typeQ = new Dictionary<int, int>(); // type*2+side -> remaining count
            var typePlacements = new Dictionary<int, List<Plc>>();
            foreach (var p in allPlaces)
            {
                if (solver.Value(p.Var) != 0)
                {
                    int k = p.Type * 2 + (p.Side == Side.Yang ? 0 : 1);
                    if (!typePlacements.TryGetValue(k, out var l))
                    {
                        typePlacements[k] = l = new List<Plc>();
                    }
                    l.Add(p);
                }
            }
            var used = new Dictionary<int, int>();
            foreach (int i in active)
            {
                int k = typeOf[i] * 2;
                for (int s = 0; s < 2; s++)
                {
                    int cnt = (int)solver.Value(q[i, s]);
                    if (cnt <= 0)
                    {
                        continue;
                    }
                    int key = k + s;
                    if (!typePlacements.TryGetValue(key, out var list))
                    {
                        list = new List<Plc>();
                    }
                    used.TryGetValue(key, out var start);
                    for (int n = start; n < start + cnt && n < list.Count; n++)
                    {
                        var p = list[n];
                        // 与 Packer 的 Spot 语义一致：原点 = 锚点位置 - 锚点偏移 + 盘面 Min
                        var g = p.Side == Side.Yang ? yang : yin;
                        int ox = p.X - p.AnchorX + (g?.MinX ?? 0);
                        int oy = p.Y - p.AnchorY + (g?.MinY ?? 0);
                        res.Items.Add((materials[i], p.Side, ox, oy, p.Rot));
                    }
                    used[key] = start + cnt;
                }
            }
            res.Level = new int[runes.Count];
            for (int r = 0; r < runes.Count; r++)
            {
                res.Level[r] = (int)solver.Value(levelVar[r]);
            }
            var dbg = new List<string>();
            foreach (int i in active)
            {
                int cy = (int)solver.Value(q[i, 0]);
                int cn = (int)solver.Value(q[i, 1]);
                if (cy + cn > 0)
                {
                    dbg.Add($"{materials[i].Id}[{string.Join(",", materials[i].Attrs)}]y{cy}n{cn}");
                }
            }
            LastDebug = string.Join(" | ", dbg);
            return res;
        }

        public static string LastDebug = "";

        static long LayerWeight(int l)
        {
            switch (l)
            {
                case 1: return 100000L * 1000000L;
                case 2: return 1000L * 1000000L;
                case 3: return 10L * 1000000L;
                case 4: return 5L * 1000000L;
                case 5: return 3L * 1000000L;
                case 6: return 2L * 1000000L;
                default: return 1000000L;
            }
        }
    }
}
