using System;
using System.Collections.Generic;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>药性规则（TbDrugRecipeStateCfg）的纯数据拷贝。</summary>
    public class DrugState
    {
        public int Id;

        public string Name;

        public int PoolType;

        public int StateType;

        public string Area;

        public string Target1;

        public string Target2;

        public int Relation;

        public string CalculateType;

        public string BaseEff;
    }

    /// <summary>
    /// 移植 TbCraftingImpl.GetCraftingDrugCountAndQuality / GetEligiblePosCell：按摆位算成丹数和品质。
    /// 区域只和格子坐标有关，构造时一次算好；每次评估只看格子上是哪件材料。
    /// </summary>
    public class DrugStateEvaluator
    {
        class Rule
        {
            public DrugState Cfg;

            /// <summary>[pool] → 区域内的格子下标；pool 1 阳 2 阴，没有该侧时为 null。</summary>
            public int[][] Area = new int[3][];

            public List<List<int>> Target1, Target2;

            public int CalcOp, CalcValue;

            public bool HasCalc;

            /// <summary>1 加丹数、4 加品质，其它不影响预测。</summary>
            public int EffType, EffValue;
        }

        readonly Grid[] grids = new Grid[3];

        readonly List<Rule> rules = new List<Rule>();

        public int RuleCount => rules.Count;

        public DrugStateEvaluator(IEnumerable<DrugState> states, Grid yang, Grid yin)
        {
            grids[1] = yang != null && yang.Area > 0 ? yang : null;
            grids[2] = yin != null && yin.Area > 0 ? yin : null;
            foreach (var s in states)
            {
                if (s == null || string.IsNullOrEmpty(s.Area))
                {
                    continue;
                }
                var r = new Rule { Cfg = s, Target1 = ParseTarget(s.Target1), Target2 = ParseTarget(s.Target2) };
                for (int pool = 1; pool <= 2; pool++)
                {
                    if (grids[pool] != null && (s.PoolType == 0 || s.PoolType == pool))
                    {
                        r.Area[pool] = BuildArea(grids[pool], s.Area);
                    }
                }
                if (!string.IsNullOrEmpty(s.CalculateType))
                {
                    var p = s.CalculateType.Split('#');
                    r.HasCalc = true;
                    r.CalcOp = p.Length > 0 ? int.Parse(p[0]) : 0;
                    r.CalcValue = p.Length > 1 ? int.Parse(p[1]) : 0;
                }
                if (!string.IsNullOrEmpty(s.BaseEff))
                {
                    var p = s.BaseEff.Split(',');
                    if (p[0] == "UpdateCraftingDrugAttr" && p.Length >= 3)
                    {
                        r.EffType = int.Parse(p[1]);
                        r.EffValue = int.Parse(p[2]);
                    }
                }
                rules.Add(r);
            }
        }

        static List<List<int>> ParseTarget(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }
            return s.Split('|').Select(alt => alt.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()).ToList();
        }

        public static int CellIndex(Grid g, int x, int y)
        {
            return (x - g.MinX) + (y - g.MinY) * g.Width;
        }

        static (int x, int y) CellPos(Grid g, int idx)
        {
            return (idx % g.Width + g.MinX, idx / g.Width + g.MinY);
        }

        /// <summary>area 字符串：'&amp;' 分段逐段缩小，段内 '|' 取并集；与游戏逐条对应。</summary>
        static int[] BuildArea(Grid g, string area)
        {
            var full = Enumerable.Range(0, g.Area).ToList();
            var cur = new List<int>(full);
            foreach (string seg in area.Split('&'))
            {
                var next = new List<int>();
                var inNext = new HashSet<int>();
                void Add(int c)
                {
                    if (inNext.Add(c))
                    {
                        next.Add(c);
                    }
                }
                foreach (string tok in seg.Split('|'))
                {
                    int t = int.Parse(tok);
                    switch (t)
                    {
                        case 0:
                            next = new List<int>(full);
                            inNext = new HashSet<int>(full);
                            continue;
                        case 1:
                        case 2:
                        case 3:
                        case 4:
                        {
                            Func<int, int> key = c => t <= 2 ? CellPos(g, c).y : CellPos(g, c).x;
                            int ext = t == 1 || t == 4 ? cur.Max(key) : cur.Min(key);
                            foreach (int c in cur)
                            {
                                if (key(c) == ext)
                                {
                                    Add(c);
                                }
                            }
                            continue;
                        }
                        case 5:
                            if (0 >= g.MinX && 0 >= g.MinY && cur.Contains(CellIndex(g, 0, 0)))
                            {
                                Add(CellIndex(g, 0, 0));
                            }
                            continue;
                    }
                    if (t < 10 || t >= 20)
                    {
                        continue;
                    }
                    int n = t - 10;
                    foreach (int c in full)
                    {
                        if (inNext.Contains(c))
                        {
                            continue;
                        }
                        var (cx, cy) = CellPos(g, c);
                        int min = cur.Min(o =>
                        {
                            var (ox, oy) = CellPos(g, o);
                            return Math.Abs(cx - ox) + Math.Abs(cy - oy);
                        });
                        if (min == n)
                        {
                            Add(c);
                        }
                    }
                }
                cur = next;
            }
            return cur.ToArray();
        }

        /// <summary>
        /// 一侧的摆放情况：Cells[i] = 占这个格子的材料编号，-1 为空。材料编号指向 Pieces。
        /// </summary>
        public class Board
        {
            public int[][] Cells = new int[3][];

            public List<Material> Pieces = new List<Material>();
        }

        static bool Match(List<List<int>> target, int piece, Board b)
        {
            if (target == null)
            {
                return true;
            }
            Material m = piece < 0 ? null : b.Pieces[piece];
            foreach (var alt in target)
            {
                bool all = true;
                foreach (int cond in alt)
                {
                    if (!Cond(cond, m))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    return true;
                }
            }
            return false;
        }

        static bool Cond(int c, Material m)
        {
            if (c == 0)
            {
                return m == null;
            }
            if (m == null)
            {
                return false;
            }
            if (c >= 1 && c <= 8)
            {
                return m.Element == c;
            }
            int w = m.Grade;
            if (c >= 11 && c <= 14)
            {
                return (w + 2) / 3 >= c - 10;
            }
            if (c >= 21 && c <= 32)
            {
                return w >= c - 20;
            }
            if (c >= 41 && c <= 44)
            {
                return (w + 2) / 3 <= c - 40;
            }
            if (c >= 51 && c <= 62)
            {
                return w <= c - 50;
            }
            return false;
        }

        /// <summary>只看材料本身（不看位置）是否满足某条规则的 target1/target2，用来给候选组合排序。</summary>
        public int Affinity(Material m, bool forQuality)
        {
            int a = 0;
            var b = new Board();
            b.Pieces.Add(m);
            foreach (var r in rules)
            {
                if ((r.EffType == 4) != forQuality || r.EffValue <= 0)
                {
                    continue;
                }
                if (r.Target1 != null && Match(r.Target1, 0, b))
                {
                    a++;
                }
                if (r.Target2 != null && Match(r.Target2, 0, b))
                {
                    a++;
                }
            }
            return a;
        }

        public (int count, int quality) Evaluate(Board b, int baseCount, int baseQuality)
        {
            return Evaluate(b, baseCount, baseQuality, out _, out _);
        }

        /// <summary>
        /// progressCount / progressQuality：加丹数、加品质的规则离下一档还差多少的「进度」（满足条件的个数之和，
        /// 阈值类规则封顶在阈值）。摆位搜索用它打破平局：例如「每 3 个火系 +1」放第 1、2 个时分数不变，但进度在涨。
        /// </summary>
        public (int count, int quality) Evaluate(Board b, int baseCount, int baseQuality, out int progressCount, out int progressQuality)
        {
            int count = baseCount, quality = baseQuality;
            progressCount = 0;
            progressQuality = 0;
            var e1 = new List<int>();
            var e2 = new List<int>();
            foreach (var r in rules)
            {
                int num = 0;
                for (int pool = 1; pool <= 2; pool++)
                {
                    var area = r.Area[pool];
                    if (area == null)
                    {
                        continue;
                    }
                    var g = grids[pool];
                    var cells = b.Cells[pool];
                    e1.Clear();
                    e2.Clear();
                    foreach (int c in area)
                    {
                        if (Match(r.Target1, cells[c], b))
                        {
                            e1.Add(c);
                        }
                        if (Match(r.Target2, cells[c], b))
                        {
                            e2.Add(c);
                        }
                    }
                    int n8 = 0;
                    int st = r.Cfg.StateType;
                    switch (r.Cfg.Relation)
                    {
                        case 0:
                            if (st >= 11 && st <= 18)
                            {
                                int d = st - 11;
                                foreach (int p in e1.Select(c => cells[c]).Where(p => p >= 0).Distinct())
                                {
                                    int v = b.Pieces[p].Attrs[d];
                                    n8 += pool == 1 ? v : -v;
                                }
                            }
                            else
                            {
                                n8 = st == 0 ? e1.Count : e1.Select(c => cells[c]).Distinct().Count();
                            }
                            break;
                        case 10:
                        case 13:
                        case 14:
                        case 15:
                        case 16:
                        {
                            // 相邻类关系：只看 c1 的固定邻格是否在 E2 里，不做 E1×E2 全配对
                            var in2 = new bool[cells.Length];
                            foreach (int c in e2)
                            {
                                in2[c] = true;
                            }
                            var pairs = new HashSet<long>();
                            int rel = r.Cfg.Relation;
                            foreach (int c1 in e1)
                            {
                                var (x1, y1) = CellPos(g, c1);
                                for (int k = 0; k < 4; k++)
                                {
                                    int dx = k == 0 ? 1 : k == 1 ? -1 : 0, dy = k == 2 ? 1 : k == 3 ? -1 : 0;
                                    bool want = rel == 10 || (rel == 13 && dx == -1) || (rel == 14 && dx == 1) || (rel == 15 && dy == 1) || (rel == 16 && dy == -1);
                                    if (!want)
                                    {
                                        continue;
                                    }
                                    int x2 = x1 + dx, y2 = y1 + dy;
                                    if (x2 < g.MinX || y2 < g.MinY || x2 >= g.MinX + g.Width || y2 >= g.MinY + g.Height)
                                    {
                                        continue;
                                    }
                                    int c2 = CellIndex(g, x2, y2);
                                    if (in2[c2] && (st == 0 || pairs.Add(((long)(cells[c1] + 1) << 32) | (uint)(cells[c2] + 1))))
                                    {
                                        n8++;
                                    }
                                }
                            }
                            break;
                        }
                        case 11:
                        case 12:
                        {
                            var pairs = new HashSet<long>();
                            foreach (int c1 in e1)
                            {
                                var (x1, y1) = CellPos(g, c1);
                                foreach (int c2 in e2)
                                {
                                    var (x2, y2) = CellPos(g, c2);
                                    bool ok;
                                    switch (r.Cfg.Relation)
                                    {
                                        case 10: ok = Math.Abs(x2 - x1) + Math.Abs(y2 - y1) == 1; break;
                                        case 11: ok = x2 == x1; break;
                                        case 12: ok = y2 == y1; break;
                                        case 13: ok = y2 == y1 && x2 == x1 - 1; break;
                                        case 14: ok = y2 == y1 && x2 == x1 + 1; break;
                                        case 15: ok = x2 == x1 && y2 == y1 + 1; break;
                                        default: ok = x2 == x1 && y2 == y1 - 1; break;
                                    }
                                    if (ok && (st == 0 || pairs.Add(((long)(cells[c1] + 1) << 32) | (uint)(cells[c2] + 1))))
                                    {
                                        n8++;
                                    }
                                }
                            }
                            break;
                        }
                        case 100:
                        case 102:
                        {
                            var per = new Dictionary<int, HashSet<int>>();
                            foreach (int c in e1)
                            {
                                var (x, y) = CellPos(g, c);
                                int line = r.Cfg.Relation == 100 ? x : y;
                                if (!per.TryGetValue(line, out var set))
                                {
                                    per[line] = set = new HashSet<int>();
                                }
                                set.Add(cells[c]);
                            }
                            n8 = per.Count > 0 ? per.Values.Max(s => s.Count) : 0;
                            break;
                        }
                        case 101:
                        {
                            int all, elig;
                            if (st == 0)
                            {
                                all = area.Count(c => cells[c] >= 0);
                                elig = e1.Count;
                            }
                            else
                            {
                                all = area.Select(c => cells[c]).Where(p => p >= 0).Distinct().Count();
                                elig = e1.Select(c => cells[c]).Where(p => p >= 0).Distinct().Count();
                            }
                            n8 = all == elig ? 1 : 0;
                            break;
                        }
                    }
                    num = r.Cfg.Relation == 100 || r.Cfg.Relation == 102 ? Math.Max(num, n8) : num + n8;
                }
                int mul = 0;
                if (r.HasCalc)
                {
                    switch (r.CalcOp)
                    {
                        case 0: mul = num == r.CalcValue ? 1 : 0; break;
                        case 1: mul = num > r.CalcValue ? 1 : 0; break;
                        case 2: mul = num >= r.CalcValue ? 1 : 0; break;
                        case 3: mul = num < r.CalcValue ? 1 : 0; break;
                        case 4: mul = num <= r.CalcValue ? 1 : 0; break;
                        case 5: mul = r.CalcValue == 0 ? 0 : num / r.CalcValue; break;
                    }
                }
                if (r.EffValue > 0 && (r.CalcOp == 1 || r.CalcOp == 2 || r.CalcOp == 5))
                {
                    int prog = r.CalcOp == 5 ? num : Math.Min(num, r.CalcValue + (r.CalcOp == 1 ? 1 : 0));
                    if (r.EffType == 1)
                    {
                        progressCount += Math.Max(0, prog);
                    }
                    else if (r.EffType == 4)
                    {
                        progressQuality += Math.Max(0, prog);
                    }
                }
                if (r.EffType == 1)
                {
                    count += r.EffValue * mul;
                }
                else if (r.EffType == 4)
                {
                    quality += r.EffValue * mul;
                }
            }
            return (Math.Max(0, count), Math.Max(0, Math.Min(3, quality)));
        }

        /// <summary>由一套摆法生成 Board。</summary>
        public Board BuildBoard(IEnumerable<Placement> placements)
        {
            var b = new Board();
            for (int pool = 1; pool <= 2; pool++)
            {
                if (grids[pool] != null)
                {
                    b.Cells[pool] = Enumerable.Repeat(-1, grids[pool].Area).ToArray();
                }
            }
            foreach (var p in placements)
            {
                int pool = (int)p.Side;
                var g = grids[pool];
                if (g == null)
                {
                    continue;
                }
                int id = b.Pieces.Count;
                b.Pieces.Add(p.Material);
                foreach (var c in p.Material.Shape)
                {
                    var rc = Packer.Rotate(c, p.Rotate);
                    b.Cells[pool][CellIndex(g, rc.X + p.X, rc.Y + p.Y)] = id;
                }
            }
            return b;
        }
    }
}
