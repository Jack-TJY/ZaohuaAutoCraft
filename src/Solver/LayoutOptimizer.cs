using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 调摆位：每块预先枚举出全部合法摆位（位掩码，最多 128 格），
    /// 做「单块挪到最好的位置 / 同形状两块互换」的爬山，卡住后随机踢开一两块重来。不跨阴阳挪动（会改属性）。
    /// 还能「垫料」：阳格、阴格各放一件属性相同的材料，属性互相抵消，只为多满足药性规则。
    /// </summary>
    public class LayoutOptimizer
    {
        struct Mask
        {
            public ulong A, B;

            public bool Hits(Mask o) => (A & o.A) != 0 || (B & o.B) != 0;

            public void Or(Mask o)
            {
                A |= o.A;
                B |= o.B;
            }

            public void AndNot(Mask o)
            {
                A &= ~o.A;
                B &= ~o.B;
            }

            public void Set(int i)
            {
                if (i < 64)
                {
                    A |= 1UL << i;
                }
                else
                {
                    B |= 1UL << (i - 64);
                }
            }

            public bool Same(Mask o) => A == o.A && B == o.B;
        }

        class Spot
        {
            public Mask Mask;

            /// <summary>与这块相邻（上下左右）的格子，用来优先试贴着别的材料放。</summary>
            public Mask Around;

            public int[] Cells;

            public int X, Y, Rotate;
        }

        class Piece
        {
            public Placement Placement;

            public int Pool;

            public List<Spot> Spots;

            public int Cur;

            public string TypeKey;
        }

        readonly DrugStateEvaluator eval;

        readonly Grid[] grids = new Grid[3];

        readonly Func<int, int, long> objective;

        readonly int baseCount, baseQuality;

        static readonly Dictionary<string, List<Spot>> spotCache = new Dictionary<string, List<Spot>>();

        DrugStateEvaluator.Board board;

        List<Piece> pieces;

        Mask[] occ;

        Random rng;

        /// <param name="objective">(丹数, 品质) → 分数，越大越好。</param>
        public LayoutOptimizer(DrugStateEvaluator eval, Grid yang, Grid yin, int baseCount, int baseQuality, Func<int, int, long> objective)
        {
            this.eval = eval;
            grids[1] = yang;
            grids[2] = yin;
            this.baseCount = baseCount;
            this.baseQuality = baseQuality;
            this.objective = objective;
        }

        static List<Spot> SpotsFor(Grid g, List<Cell> shape)
        {
            string key = g.Width + "x" + g.Height + ":" + string.Join(";", shape.Select(c => c.X + "," + c.Y));
            lock (spotCache)
            {
                if (spotCache.TryGetValue(key, out var list))
                {
                    return list;
                }
                list = new List<Spot>();
                var seen = new HashSet<(ulong, ulong)>();
                for (int d = 0; d < 4; d++)
                {
                    var rot = shape.Select(c => Packer.Rotate(c, d)).ToList();
                    for (int ox = g.MinX - 12; ox <= g.MinX + g.Width + 12; ox++)
                    {
                        for (int oy = g.MinY - 12; oy <= g.MinY + g.Height + 12; oy++)
                        {
                            var m = new Mask();
                            var cells = new int[rot.Count];
                            bool ok = true;
                            for (int i = 0; i < rot.Count; i++)
                            {
                                int x = rot[i].X + ox, y = rot[i].Y + oy;
                                if (x < g.MinX || y < g.MinY || x >= g.MinX + g.Width || y >= g.MinY + g.Height)
                                {
                                    ok = false;
                                    break;
                                }
                                cells[i] = DrugStateEvaluator.CellIndex(g, x, y);
                                m.Set(cells[i]);
                            }
                            // 同一组格子只留一种摆法（对称形状不同旋转会重合）
                            if (!ok || !seen.Add((m.A, m.B)))
                            {
                                continue;
                            }
                            var around = new Mask();
                            foreach (var c in rot)
                            {
                                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                                {
                                    int x = c.X + ox + dx, y = c.Y + oy + dy;
                                    if (x >= g.MinX && y >= g.MinY && x < g.MinX + g.Width && y < g.MinY + g.Height)
                                    {
                                        around.Set(DrugStateEvaluator.CellIndex(g, x, y));
                                    }
                                }
                            }
                            around.AndNot(m);
                            list.Add(new Spot { Mask = m, Around = around, Cells = cells, X = ox, Y = oy, Rotate = d });
                        }
                    }
                }
                spotCache[key] = list;
                return list;
            }
        }

        bool Load(Solution sol)
        {
            board = eval.BuildBoard(sol.Placements);
            pieces = new List<Piece>();
            occ = new Mask[3];
            foreach (var p in sol.Placements)
            {
                int pool = (int)p.Side;
                var g = grids[pool];
                var spots = SpotsFor(g, p.Material.Shape);
                var mine = new Mask();
                foreach (var c in p.Material.Shape)
                {
                    var rc = Packer.Rotate(c, p.Rotate);
                    mine.Set(DrugStateEvaluator.CellIndex(g, rc.X + p.X, rc.Y + p.Y));
                }
                int cur = spots.FindIndex(s => s.Mask.Same(mine));
                if (cur < 0)
                {
                    return false;
                }
                occ[pool].Or(mine);
                pieces.Add(new Piece { Placement = p, Pool = pool, Spots = spots, Cur = cur, TypeKey = Packer.GetType(p.Material.Shape).Key });
            }
            return true;
        }

        void Save()
        {
            foreach (var pc in pieces)
            {
                var s = pc.Spots[pc.Cur];
                pc.Placement.X = s.X;
                pc.Placement.Y = s.Y;
                pc.Placement.Rotate = s.Rotate;
            }
        }

        /// <summary>主分数 × 1024 + 进度（只用来打破平局，不影响最终比较）。</summary>
        long Score()
        {
            var (cnt, q) = eval.Evaluate(board, baseCount, baseQuality, out int pc, out int pq);
            long main = objective(cnt, q);
            int prog = q >= 3 ? pc : pq * 4 + pc;
            return main * 1024 + Math.Min(1023, prog);
        }

        void Lift(int i)
        {
            var pc = pieces[i];
            if (pc.Cur < 0)
            {
                return;
            }
            foreach (int c in pc.Spots[pc.Cur].Cells)
            {
                board.Cells[pc.Pool][c] = -1;
            }
            occ[pc.Pool].AndNot(pc.Spots[pc.Cur].Mask);
            pc.Cur = -1;
        }

        void Drop(int i, int spot)
        {
            var pc = pieces[i];
            pc.Cur = spot;
            foreach (int c in pc.Spots[spot].Cells)
            {
                board.Cells[pc.Pool][c] = i;
            }
            occ[pc.Pool].Or(pc.Spots[spot].Mask);
        }

        void Put(int i, int spot)
        {
            Lift(i);
            Drop(i, spot);
        }

        bool Free(int i, int spot)
        {
            var pc = pieces[i];
            var o = occ[pc.Pool];
            if (pc.Cur >= 0)
            {
                o.AndNot(pc.Spots[pc.Cur].Mask);
            }
            return !o.Hits(pc.Spots[spot].Mask);
        }

        /// <summary>把第 i 块挪到当前最好的空位（含原位），返回新分数。</summary>
        long BestMove(int i, long score)
        {
            int from = pieces[i].Cur, best = from;
            long bs = score;
            for (int s = 0; s < pieces[i].Spots.Count; s++)
            {
                if (s == from || !Free(i, s))
                {
                    continue;
                }
                Put(i, s);
                long v = Score();
                if (v > bs)
                {
                    bs = v;
                    best = s;
                }
            }
            Put(i, best);
            return bs;
        }

        void RestoreAll(int[] cur)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                Lift(i);
            }
            for (int i = 0; i < pieces.Count; i++)
            {
                Drop(i, cur[i]);
            }
        }

        long HillClimb(long score, long deadlineMs, Stopwatch clock)
        {
            bool improved = true;
            while (improved && clock.ElapsedMilliseconds < deadlineMs)
            {
                improved = false;
                foreach (int i in Enumerable.Range(0, pieces.Count).OrderBy(_ => rng.Next()))
                {
                    long v = BestMove(i, score);
                    if (v > score)
                    {
                        score = v;
                        improved = true;
                    }
                }
                // 同形状、不同材料的两块直接互换位置
                for (int i = 0; i < pieces.Count; i++)
                {
                    for (int j = i + 1; j < pieces.Count; j++)
                    {
                        var a = pieces[i];
                        var b = pieces[j];
                        if (a.Pool != b.Pool || a.TypeKey != b.TypeKey || a.Placement.Material == b.Placement.Material)
                        {
                            continue;
                        }
                        var ma = a.Spots[a.Cur].Mask;
                        var mb = b.Spots[b.Cur].Mask;
                        int sa = a.Spots.FindIndex(s => s.Mask.Same(mb));
                        int sb = b.Spots.FindIndex(s => s.Mask.Same(ma));
                        if (sa < 0 || sb < 0)
                        {
                            continue;
                        }
                        int oa = a.Cur, ob = b.Cur;
                        Lift(i);
                        Lift(j);
                        Drop(i, sa);
                        Drop(j, sb);
                        long v = Score();
                        if (v > score)
                        {
                            score = v;
                            improved = true;
                        }
                        else
                        {
                            Lift(i);
                            Lift(j);
                            Drop(i, oa);
                            Drop(j, ob);
                        }
                    }
                }
            }
            return score;
        }

        long Search(long score, Stopwatch clock, long deadlineMs)
        {
            long bestScore = HillClimb(score, deadlineMs, clock);
            score = bestScore;
            var best = pieces.Select(p => p.Cur).ToArray();
            int stall = 0;
            while (clock.ElapsedMilliseconds < deadlineMs && stall < 30 && pieces.Count > 0)
            {
                // 随机踢开 1–2 块再爬山
                int kicks = 1 + rng.Next(Math.Min(2, pieces.Count));
                for (int k = 0; k < kicks; k++)
                {
                    int i = rng.Next(pieces.Count);
                    var free = Enumerable.Range(0, pieces[i].Spots.Count).Where(s => s != pieces[i].Cur && Free(i, s)).ToList();
                    if (free.Count > 0)
                    {
                        Put(i, free[rng.Next(free.Count)]);
                    }
                }
                score = HillClimb(Score(), deadlineMs, clock);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = pieces.Select(p => p.Cur).ToArray();
                    stall = 0;
                }
                else
                {
                    stall++;
                    RestoreAll(best);
                    score = bestScore;
                }
            }
            RestoreAll(best);
            return bestScore;
        }

        /// <summary>原地改 sol 的摆位，返回最终 (丹数, 品质)。</summary>
        public (int count, int quality) Optimize(Solution sol, Stopwatch clock, long deadlineMs, int seed = 1)
        {
            rng = new Random(seed);
            if (sol.Placements.Count == 0 || eval.RuleCount == 0 || !Load(sol))
            {
                return eval.Evaluate(eval.BuildBoard(sol.Placements), baseCount, baseQuality);
            }
            Search(Score(), clock, deadlineMs);
            Save();
            return eval.Evaluate(board, baseCount, baseQuality);
        }

        /// <summary>
        /// 垫料：每轮试所有「阳一件 + 阴一件、属性相同」的组合，挑让分数涨得最多的放上去，直到不再涨或没库存/没地方。
        /// remaining：每种材料还能用几件（已扣掉方案里用掉的）。
        /// </summary>
        public (int count, int quality) Pad(Solution sol, List<(Material yang, Material yin)> pairs, Dictionary<Material, long> remaining, Stopwatch clock, long deadlineMs, int seed = 1)
        {
            rng = new Random(seed);
            if (eval.RuleCount == 0 || pairs.Count == 0 || !Load(sol))
            {
                return eval.Evaluate(eval.BuildBoard(sol.Placements), baseCount, baseQuality);
            }
            long score = Search(Score(), clock, Math.Min(deadlineMs, clock.ElapsedMilliseconds + 60));
            var added = new List<(Placement, Placement)>();
            // 上一轮选中的垫料对：只要它还在涨就一直放它，不用每放一对都把所有候选试一遍（候选多时慢十几倍）
            (Material a, Material b)? sticky = null;
            while (clock.ElapsedMilliseconds < deadlineMs)
            {
                // 按「每格涨多少分」挑，免得大块先把地方占满
                double bestGain = 0;
                (Material a, Material b, int sa, int sb) bestPick = default;
                var tryList = sticky != null ? new List<(Material, Material)> { sticky.Value } : pairs;
                foreach (var (ma, mb) in tryList)
                {
                    long need = ma == mb ? 2 : 1;
                    if (Left(remaining, ma) < need || Left(remaining, mb) < need)
                    {
                        continue;
                    }
                    var (sa, sb, v) = TryPair(ma, mb, score, clock, deadlineMs);
                    double gain = (double)(v - score) / (ma.Shape.Count + mb.Shape.Count);
                    if (sa >= 0 && v > score && gain > bestGain)
                    {
                        bestGain = gain;
                        bestPick = (ma, mb, sa, sb);
                    }
                    if (clock.ElapsedMilliseconds > deadlineMs)
                    {
                        break;
                    }
                }
                if (bestGain <= 0)
                {
                    if (sticky != null)
                    {
                        sticky = null;
                        continue;
                    }
                    break;
                }
                sticky = (bestPick.a, bestPick.b);
                added.Add((AddPiece(sol, bestPick.a, Side.Yang, bestPick.sa), AddPiece(sol, bestPick.b, Side.Yin, bestPick.sb)));
                remaining[bestPick.a]--;
                remaining[bestPick.b]--;
                score = Score();
            }
            score = Search(score, clock, deadlineMs + 150);
            Save();
            // 只涨了「进度」、最后没换来丹数/品质的垫料拿掉，别白费材料
            long Main()
            {
                var (c, q) = eval.Evaluate(eval.BuildBoard(sol.Placements), baseCount, baseQuality);
                return objective(c, q);
            }
            long main = Main();
            for (int k = added.Count - 1; k >= 0; k--)
            {
                var (pa, pb) = added[k];
                int ia = sol.Placements.IndexOf(pa), ib = sol.Placements.IndexOf(pb);
                sol.Placements.Remove(pa);
                sol.Placements.Remove(pb);
                if (Main() < main)
                {
                    sol.Placements.Insert(Math.Min(ia, sol.Placements.Count), pa);
                    sol.Placements.Insert(Math.Min(ib, sol.Placements.Count), pb);
                }
                else
                {
                    remaining[pa.Material]++;
                    remaining[pb.Material]++;
                }
            }
            return eval.Evaluate(eval.BuildBoard(sol.Placements), baseCount, baseQuality);
        }

        static long Left(Dictionary<Material, long> remaining, Material m)
        {
            return remaining.TryGetValue(m, out long v) ? v : 0;
        }

        Placement AddPiece(Solution sol, Material m, Side side, int spot)
        {
            var p = new Placement { Material = m, Side = side };
            sol.Placements.Add(p);
            int pool = (int)side;
            board.Pieces.Add(m);
            pieces.Add(new Piece { Placement = p, Pool = pool, Spots = SpotsFor(grids[pool], m.Shape), Cur = -1, TypeKey = Packer.GetType(m.Shape).Key });
            Drop(pieces.Count - 1, spot);
            return p;
        }

        /// <summary>先给阳侧那件找最好的位置，再固定它给阴侧那件找；只试贴着已有材料的位置和少量随机位置，控制开销。</summary>
        (int sa, int sb, long score) TryPair(Material ma, Material mb, long score, Stopwatch clock, long deadlineMs)
        {
            int sa = BestFreeSpot(ma, 1, clock, deadlineMs, out _);
            if (sa < 0)
            {
                return (-1, -1, score);
            }
            var spotsA = SpotsFor(grids[1], ma.Shape);
            // 临时放上阳侧那件
            int ia = pieces.Count;
            board.Pieces.Add(ma);
            pieces.Add(new Piece { Pool = 1, Spots = spotsA, Cur = -1 });
            Drop(ia, sa);
            int sb = BestFreeSpot(mb, 2, clock, deadlineMs, out long v);
            Lift(ia);
            pieces.RemoveAt(ia);
            board.Pieces.RemoveAt(ia);
            return sb < 0 ? (-1, -1, score) : (sa, sb, v);
        }

        int BestFreeSpot(Material m, int pool, Stopwatch clock, long deadlineMs, out long bestScore)
        {
            var spots = SpotsFor(grids[pool], m.Shape);
            int idx = pieces.Count;
            board.Pieces.Add(m);
            pieces.Add(new Piece { Pool = pool, Spots = spots, Cur = -1 });
            var cand = new List<int>();
            var rest = new List<int>();
            for (int s = 0; s < spots.Count; s++)
            {
                if (occ[pool].Hits(spots[s].Mask))
                {
                    continue;
                }
                (occ[pool].Hits(spots[s].Around) ? cand : rest).Add(s);
            }
            // 贴着已有材料的位置最多试 48 个，其余随机抽一些（区域类规则可能要空旷的地方）
            if (cand.Count > 48)
            {
                cand = cand.OrderBy(_ => rng.Next()).Take(48).ToList();
            }
            foreach (int s in rest.OrderBy(_ => rng.Next()).Take(Math.Max(12, 64 - cand.Count)))
            {
                cand.Add(s);
            }
            int best = -1;
            bestScore = long.MinValue;
            foreach (int s in cand)
            {
                Drop(idx, s);
                long v = Score();
                Lift(idx);
                if (v > bestScore)
                {
                    bestScore = v;
                    best = s;
                }
                if (clock.ElapsedMilliseconds > deadlineMs)
                {
                    break;
                }
            }
            pieces.RemoveAt(idx);
            board.Pieces.RemoveAt(idx);
            return best;
        }
    }
}
