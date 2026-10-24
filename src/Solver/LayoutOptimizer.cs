using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 材料已定、只调位置：每块预先枚举出全部合法摆位（位掩码，最多 128 格），
    /// 再做「单块挪到最好的位置 / 同形状两块互换」的爬山，卡住后随机踢开一两块重来。
    /// 不跨阴阳挪动（会改属性）。
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
        }

        class Spot
        {
            public Mask Mask;

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
                            if (ok && seen.Add((m.A, m.B)))
                            {
                                list.Add(new Spot { Mask = m, Cells = cells, X = ox, Y = oy, Rotate = d });
                            }
                        }
                    }
                }
                spotCache[key] = list;
                return list;
            }
        }

        /// <summary>原地改 sol 的摆位，返回最终 (丹数, 品质)。</summary>
        public (int count, int quality) Optimize(Solution sol, Stopwatch clock, long deadlineMs, int seed = 1)
        {
            var board = eval.BuildBoard(sol.Placements);
            var start = eval.Evaluate(board, baseCount, baseQuality);
            if (sol.Placements.Count == 0 || eval.RuleCount == 0)
            {
                return start;
            }
            var pieces = new List<Piece>();
            var occ = new Mask[3];
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
                int cur = spots.FindIndex(s => s.Mask.A == mine.A && s.Mask.B == mine.B);
                if (cur < 0)
                {
                    return start;
                }
                occ[pool].Or(mine);
                pieces.Add(new Piece { Placement = p, Pool = pool, Spots = spots, Cur = cur, TypeKey = Packer.GetType(p.Material.Shape).Key });
            }
            // board.Pieces 与 sol.Placements 同序
            long Score()
            {
                var (cnt, q) = eval.Evaluate(board, baseCount, baseQuality);
                return objective(cnt, q);
            }
            void Put(int i, int spot)
            {
                var pc = pieces[i];
                foreach (int c in pc.Spots[pc.Cur].Cells)
                {
                    board.Cells[pc.Pool][c] = -1;
                }
                occ[pc.Pool].AndNot(pc.Spots[pc.Cur].Mask);
                pc.Cur = spot;
                foreach (int c in pc.Spots[spot].Cells)
                {
                    board.Cells[pc.Pool][c] = i;
                }
                occ[pc.Pool].Or(pc.Spots[spot].Mask);
            }
            bool Free(int i, int spot)
            {
                var pc = pieces[i];
                var o = occ[pc.Pool];
                o.AndNot(pc.Spots[pc.Cur].Mask);
                return !o.Hits(pc.Spots[spot].Mask);
            }

            var rng = new Random(seed);
            long score = Score();
            long bestScore = score;
            var best = pieces.Select(p => p.Cur).ToArray();
            int stall = 0;
            while (clock.ElapsedMilliseconds < deadlineMs && stall < 40)
            {
                // 爬山
                bool improved = true;
                while (improved && clock.ElapsedMilliseconds < deadlineMs)
                {
                    improved = false;
                    foreach (int i in Enumerable.Range(0, pieces.Count).OrderBy(_ => rng.Next()))
                    {
                        int from = pieces[i].Cur, bestSpot = from;
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
                                bestSpot = s;
                            }
                        }
                        Put(i, bestSpot);
                        if (bestSpot != from)
                        {
                            score = bs;
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
                            int sa = a.Spots.FindIndex(s => s.Mask.A == mb.A && s.Mask.B == mb.B);
                            int sb = b.Spots.FindIndex(s => s.Mask.A == ma.A && s.Mask.B == ma.B);
                            if (sa < 0 || sb < 0)
                            {
                                continue;
                            }
                            int oa = a.Cur, ob = b.Cur;
                            SwapTo(i, sa, j, sb);
                            long v = Score();
                            if (v > score)
                            {
                                score = v;
                                improved = true;
                            }
                            else
                            {
                                SwapTo(i, oa, j, ob);
                            }
                        }
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = pieces.Select(p => p.Cur).ToArray();
                    stall = 0;
                }
                else
                {
                    stall++;
                    // 回到最好解再踢
                    RestoreAll(best);
                    score = bestScore;
                }
                // 随机踢开 1–2 块
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
                score = Score();
            }
            RestoreAll(best);

            void SwapTo(int i, int si, int j, int sj)
            {
                // 先都拿起来再放下，避免中间状态互相覆盖
                foreach (int k in new[] { i, j })
                {
                    var pc = pieces[k];
                    foreach (int c in pc.Spots[pc.Cur].Cells)
                    {
                        board.Cells[pc.Pool][c] = -1;
                    }
                    occ[pc.Pool].AndNot(pc.Spots[pc.Cur].Mask);
                }
                pieces[i].Cur = si;
                pieces[j].Cur = sj;
                foreach (int k in new[] { i, j })
                {
                    var pc = pieces[k];
                    foreach (int c in pc.Spots[pc.Cur].Cells)
                    {
                        board.Cells[pc.Pool][c] = k;
                    }
                    occ[pc.Pool].Or(pc.Spots[pc.Cur].Mask);
                }
            }

            void RestoreAll(int[] cur)
            {
                for (int pool = 1; pool <= 2; pool++)
                {
                    occ[pool] = new Mask();
                    if (board.Cells[pool] != null)
                    {
                        for (int c = 0; c < board.Cells[pool].Length; c++)
                        {
                            board.Cells[pool][c] = -1;
                        }
                    }
                }
                for (int i = 0; i < pieces.Count; i++)
                {
                    var pc = pieces[i];
                    pc.Cur = cur[i];
                    foreach (int c in pc.Spots[pc.Cur].Cells)
                    {
                        board.Cells[pc.Pool][c] = i;
                    }
                    occ[pc.Pool].Or(pc.Spots[pc.Cur].Mask);
                }
            }

            foreach (var pc in pieces)
            {
                var s = pc.Spots[pc.Cur];
                pc.Placement.X = s.X;
                pc.Placement.Y = s.Y;
                pc.Placement.Rotate = s.Rotate;
            }
            return eval.Evaluate(board, baseCount, baseQuality);
        }
    }
}
