using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 把一组形状不重叠地放进一个矩形格子（格子可以留空）。
    /// 回溯：按行扫描找第一个空格，要么用某块盖住它，要么在空格预算内把它留空。
    /// </summary>
    public class Packer
    {
        public class Orientation
        {
            public int Rotate;

            /// <summary>游戏旋转后的坐标（未平移）。</summary>
            public Cell[] Rotated;

            /// <summary>以扫描序第一个格为原点的偏移。</summary>
            public Cell[] Offsets;

            public Cell Anchor;
        }

        public class PieceType
        {
            public string Key;

            public int Size;

            public List<Orientation> Orientations = new List<Orientation>();
        }

        public class Result
        {
            public bool Ok;

            public bool TimedOut;

            /// <summary>与输入顺序对应：每块的游戏原点坐标与旋转。</summary>
            public List<(int x, int y, int rotate)> Spots;
        }

        static readonly Cell[] RotateAngles = { new Cell(1, 0), new Cell(0, -1), new Cell(-1, 0), new Cell(0, 1) };

        static readonly Dictionary<string, PieceType> typeCache = new Dictionary<string, PieceType>();

        public static Cell Rotate(Cell c, int dir)
        {
            Cell r = RotateAngles[dir];
            return new Cell(c.X * r.X - c.Y * r.Y, c.X * r.Y + c.Y * r.X);
        }

        static string Canon(IEnumerable<Cell> cells)
        {
            var list = cells.ToList();
            int mx = list.Min(c => c.X), my = list.Min(c => c.Y);
            return string.Join(";", list.Select(c => (c.X - mx) + "," + (c.Y - my)).OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>同一形状（任意旋转下）共用一个类型，相同的块可以互换，减少重复搜索。</summary>
        public static PieceType GetType(List<Cell> shape)
        {
            var canons = Enumerable.Range(0, 4).Select(d => Canon(shape.Select(c => Rotate(c, d)))).ToList();
            string key = canons.OrderBy(s => s, StringComparer.Ordinal).First();
            lock (typeCache)
            {
                if (typeCache.TryGetValue(key, out var t))
                {
                    return t;
                }
                t = new PieceType { Key = key, Size = shape.Count };
                var seen = new HashSet<string>();
                for (int d = 0; d < 4; d++)
                {
                    if (!seen.Add(canons[d]))
                    {
                        continue;
                    }
                    var rotated = shape.Select(c => Rotate(c, d)).ToArray();
                    // 扫描序：先 y 从大到小（游戏里 y 大在上），再 x 从小到大
                    var anchor = rotated.OrderByDescending(c => c.Y).ThenBy(c => c.X).First();
                    t.Orientations.Add(new Orientation
                    {
                        Rotate = d,
                        Rotated = rotated,
                        Anchor = anchor,
                        Offsets = rotated.Select(c => new Cell(c.X - anchor.X, c.Y - anchor.Y)).ToArray()
                    });
                }
                typeCache[key] = t;
                return t;
            }
        }

        /// <summary>染色快筛：只是必要条件，返回 false 表示一定装不下。</summary>
        public static bool ColoringCheck(Grid grid, IList<List<Cell>> shapes)
        {
            int total = shapes.Sum(s => s.Count);
            if (total > grid.Area)
            {
                return false;
            }
            if (shapes.Any(s => !GetType(s).Orientations.Any(o => Fits(grid, o))))
            {
                return false;
            }
            // 棋盘染色
            if (!ClassCheck(grid, shapes, 2, (x, y) => (x + y) & 1))
            {
                return false;
            }
            // 按行 / 按列模 k 分色，卡长条
            for (int k = 3; k <= 5; k++)
            {
                int kk = k;
                if (!ClassCheck(grid, shapes, kk, (x, y) => ((y % kk) + kk) % kk) || !ClassCheck(grid, shapes, kk, (x, y) => ((x % kk) + kk) % kk))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>这个形状有没有某个旋转能放进空格子。</summary>
        public static bool CanFit(Grid grid, List<Cell> shape)
        {
            return shape.Count <= grid.Area && GetType(shape).Orientations.Any(o => Fits(grid, o));
        }

        static bool Fits(Grid grid, Orientation o)
        {
            int w = o.Offsets.Max(c => c.X) - o.Offsets.Min(c => c.X) + 1;
            int h = o.Offsets.Max(c => c.Y) - o.Offsets.Min(c => c.Y) + 1;
            return w <= grid.Width && h <= grid.Height;
        }

        static bool ClassCheck(Grid grid, IList<List<Cell>> shapes, int k, Func<int, int, int> color)
        {
            var cap = new int[k];
            for (int x = 0; x < grid.Width; x++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    cap[color(x, y)]++;
                }
            }
            var need = new int[k];
            foreach (var s in shapes)
            {
                var t = GetType(s);
                var minPer = Enumerable.Repeat(int.MaxValue, k).ToArray();
                foreach (var o in t.Orientations.Where(o => Fits(grid, o)))
                {
                    for (int sx = 0; sx < k; sx++)
                    {
                        for (int sy = 0; sy < k; sy++)
                        {
                            var cnt = new int[k];
                            foreach (var c in o.Offsets)
                            {
                                cnt[color(c.X + sx + 100 * k, c.Y + sy + 100 * k)]++;
                            }
                            for (int j = 0; j < k; j++)
                            {
                                minPer[j] = Math.Min(minPer[j], cnt[j]);
                            }
                        }
                    }
                }
                for (int j = 0; j < k; j++)
                {
                    need[j] += minPer[j];
                }
            }
            for (int j = 0; j < k; j++)
            {
                if (need[j] > cap[j])
                {
                    return false;
                }
            }
            return true;
        }

        public static Result Pack(Grid grid, IList<List<Cell>> shapes, Stopwatch clock, int timeLimitMs)
        {
            var result = new Result();
            if (shapes.Count == 0)
            {
                result.Ok = true;
                result.Spots = new List<(int, int, int)>();
                return result;
            }
            if (!ColoringCheck(grid, shapes))
            {
                return result;
            }
            var types = new List<PieceType>();
            var typeOf = new int[shapes.Count];
            for (int i = 0; i < shapes.Count; i++)
            {
                var t = GetType(shapes[i]);
                int idx = types.IndexOf(t);
                if (idx < 0)
                {
                    types.Add(t);
                    idx = types.Count - 1;
                }
                typeOf[i] = idx;
            }
            // 大块先放，回溯更早失败
            var order = Enumerable.Range(0, types.Count).OrderByDescending(i => types[i].Size).ToArray();
            var remain = new int[types.Count];
            foreach (int ti in typeOf)
            {
                remain[ti]++;
            }
            int w = grid.Width, h = grid.Height;
            var occ = new bool[w, h];
            var stack = new List<(int type, Orientation o, int gx, int gy)>();
            int budget = grid.Area - shapes.Sum(s => s.Count);
            long nodes = 0;
            bool timedOut = false;

            // 扫描序与 Orientation.Anchor 一致：y 从大到小，再 x 从小到大
            bool Rec(int p)
            {
                if ((++nodes & 1023) == 0 && clock.ElapsedMilliseconds > timeLimitMs)
                {
                    timedOut = true;
                }
                if (timedOut)
                {
                    return false;
                }
                int total = w * h;
                while (p < total && occ[p % w, h - 1 - p / w])
                {
                    p++;
                }
                bool any = false;
                foreach (int r in remain)
                {
                    if (r > 0)
                    {
                        any = true;
                        break;
                    }
                }
                if (!any)
                {
                    return true;
                }
                if (p >= total)
                {
                    return false;
                }
                int gx = p % w, gy = h - 1 - p / w;
                foreach (int ti in order)
                {
                    if (remain[ti] == 0)
                    {
                        continue;
                    }
                    foreach (var o in types[ti].Orientations)
                    {
                        bool ok = true;
                        foreach (var c in o.Offsets)
                        {
                            int x = gx + c.X, y = gy + c.Y;
                            if (x < 0 || y < 0 || x >= w || y >= h || occ[x, y])
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (!ok)
                        {
                            continue;
                        }
                        foreach (var c in o.Offsets)
                        {
                            occ[gx + c.X, gy + c.Y] = true;
                        }
                        remain[ti]--;
                        stack.Add((ti, o, gx, gy));
                        if (Rec(p + 1))
                        {
                            return true;
                        }
                        stack.RemoveAt(stack.Count - 1);
                        remain[ti]++;
                        foreach (var c in o.Offsets)
                        {
                            occ[gx + c.X, gy + c.Y] = false;
                        }
                    }
                }
                if (budget > 0)
                {
                    budget--;
                    occ[gx, gy] = true;
                    if (Rec(p + 1))
                    {
                        return true;
                    }
                    occ[gx, gy] = false;
                    budget++;
                }
                return false;
            }

            if (!Rec(0))
            {
                result.TimedOut = timedOut;
                return result;
            }
            // 把同类型的摆位按输入顺序分给各块
            var pool = stack.ToList();
            result.Spots = new List<(int, int, int)>();
            for (int i = 0; i < shapes.Count; i++)
            {
                int k = pool.FindIndex(s => s.type == typeOf[i]);
                var s0 = pool[k];
                pool.RemoveAt(k);
                // 这块自己的形状不一定和类型代表的形状同向，要找它自己的旋转使格子集合一致
                var target = new HashSet<(int, int)>(s0.o.Offsets.Select(c => (s0.gx + c.X, s0.gy + c.Y)));
                bool done = false;
                for (int d = 0; d < 4 && !done; d++)
                {
                    var rotated = shapes[i].Select(c => Rotate(c, d)).ToList();
                    var anchor = rotated.OrderByDescending(c => c.Y).ThenBy(c => c.X).First();
                    int ox = s0.gx - anchor.X, oy = s0.gy - anchor.Y;
                    if (rotated.All(c => target.Contains((c.X + ox, c.Y + oy))))
                    {
                        result.Spots.Add((ox + grid.MinX, oy + grid.MinY, d));
                        done = true;
                    }
                }
                if (!done)
                {
                    throw new InvalidOperationException("形状旋转匹配失败");
                }
            }
            result.Ok = true;
            return result;
        }
    }
}
