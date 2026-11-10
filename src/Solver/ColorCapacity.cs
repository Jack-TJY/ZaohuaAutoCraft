using System;
using System.Collections.Generic;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 多种染色容量 + 形状数量上界（依据《染色容量与形状数量上界实施报告》）。
    /// 用**真实合法位置**计算每种规范形状在各染色类上的最小占用，推出安全上界与容量行，
    /// 在数量模型里提前排除「面积够、属性够，但形状摆不下」的组合。
    /// 全部为必要条件：使用 &lt;=、允许留空、通过≠可摆；不合并材料、不跨盘合并容量。
    /// </summary>
    public static class ColorCapacity
    {
        public class Scheme
        {
            public string Name;
            public int Classes;
            public Func<int, int, int> Color;
        }

        /// <summary>第一版染色集合：黑白、行列模2~5、二维模3×3。</summary>
        public static readonly List<Scheme> Schemes = new List<Scheme>
        {
            new Scheme { Name = "bw", Classes = 2, Color = (x, y) => (((x + y) % 2) + 2) % 2 },
            new Scheme { Name = "row2", Classes = 2, Color = (x, y) => ((y % 2) + 2) % 2 },
            new Scheme { Name = "col2", Classes = 2, Color = (x, y) => ((x % 2) + 2) % 2 },
            new Scheme { Name = "row3", Classes = 3, Color = (x, y) => ((y % 3) + 3) % 3 },
            new Scheme { Name = "row4", Classes = 4, Color = (x, y) => ((y % 4) + 4) % 4 },
            new Scheme { Name = "row5", Classes = 5, Color = (x, y) => ((y % 5) + 5) % 5 },
            new Scheme { Name = "col3", Classes = 3, Color = (x, y) => ((x % 3) + 3) % 3 },
            new Scheme { Name = "col4", Classes = 4, Color = (x, y) => ((x % 4) + 4) % 4 },
            new Scheme { Name = "col5", Classes = 5, Color = (x, y) => ((x % 5) + 5) % 5 },
            new Scheme { Name = "2d3", Classes = 9, Color = (x, y) => (((x % 3) + 3) % 3) + 3 * (((y % 3) + 3) % 3) },
        };

        public class TypeProfile
        {
            public string Key;
            public int Type;
            public int Area;
            public int Placements;
            /// <summary>该类型在单盘的数量上界（面积 / 位置数 / 染色容量 的最小值）。</summary>
            public int Upper;
            /// <summary>[schemeIndex][class] 最小占用；scheme 的类数为 0 表示无约束。</summary>
            public int[][] MinUse;
        }

        public class BoardProfile
        {
            public int Width;
            public int Height;
            /// <summary>[schemeIndex * Classes + class] 该染色类的格子容量。</summary>
            public int[] Capacity;
            public List<TypeProfile> Types = new List<TypeProfile>();
        }

        static readonly Dictionary<string, BoardProfile> cache = new Dictionary<string, BoardProfile>();

        /// <summary>盘面签名：尺寸 + 有效区（本游戏为纯矩形，用尺寸即可；换有效区需换键）。</summary>
        static string BoardKey(Grid grid)
        {
            return $"{grid.Width}x{grid.Height}|{grid.MinX},{grid.MinY}";
        }

        /// <summary>枚举类型在盘上的真实合法位置（去重），返回覆盖格子的相对坐标集合。</summary>
        static List<List<Cell>> LegalPlacements(Grid grid, List<Cell> shape)
        {
            var type = Packer.GetType(shape);
            var result = new List<List<Cell>>();
            var seen = new HashSet<string>();
            foreach (var ori in type.Orientations)
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
                        var key = string.Join(";", cells.Select(c => c.X + "," + c.Y).OrderBy(s => s, StringComparer.Ordinal));
                        if (seen.Add(key))
                        {
                            result.Add(cells);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>为某盘构建 profile（含所有给出的形状类型）。shapeKeys 用于区分类型。</summary>
        public static BoardProfile Build(Grid grid, IList<List<Cell>> shapes)
        {
            if (grid == null)
            {
                return null;
            }
            var profile = new BoardProfile { Width = grid.Width, Height = grid.Height };
            int totalClasses = Schemes.Sum(s => s.Classes);
            profile.Capacity = new int[Schemes.Count * 9];
            // 容量：统计每染色类的格子数
            var cap = new int[Schemes.Count][];
            for (int f = 0; f < Schemes.Count; f++)
            {
                cap[f] = new int[Schemes[f].Classes];
            }
            for (int x = 0; x < grid.Width; x++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    for (int f = 0; f < Schemes.Count; f++)
                    {
                        cap[f][Schemes[f].Color(x, y)]++;
                    }
                }
            }
            // 类型 profile
            var byKey = new Dictionary<string, TypeProfile>();
            foreach (var shape in shapes)
            {
                var type = Packer.GetType(shape);
                if (byKey.ContainsKey(type.Key))
                {
                    continue;
                }
                var tp = new TypeProfile { Key = type.Key, Area = shape.Count, MinUse = new int[Schemes.Count][] };
                var places = LegalPlacements(grid, shape);
                tp.Placements = places.Count;
                if (places.Count == 0)
                {
                    tp.Upper = 0;
                    byKey[type.Key] = tp;
                    continue;
                }
                long upper = Math.Min(grid.Width * grid.Height / Math.Max(1, shape.Count), places.Count);
                for (int f = 0; f < Schemes.Count; f++)
                {
                    int cn = Schemes[f].Classes;
                    var minUse = Enumerable.Repeat(int.MaxValue, cn).ToArray();
                    foreach (var cells in places)
                    {
                        var use = new int[cn];
                        foreach (var c in cells)
                        {
                            use[Schemes[f].Color(c.X, c.Y)]++;
                        }
                        for (int k = 0; k < cn; k++)
                        {
                            if (use[k] < minUse[k])
                            {
                                minUse[k] = use[k];
                            }
                        }
                    }
                    for (int k = 0; k < cn; k++)
                    {
                        if (minUse[k] == int.MaxValue)
                        {
                            minUse[k] = 0;
                        }
                    }
                    tp.MinUse[f] = minUse;
                    // 容量上界
                    for (int k = 0; k < cn; k++)
                    {
                        if (minUse[k] > 0)
                        {
                            upper = Math.Min(upper, cap[f][k] / minUse[k]);
                        }
                    }
                }
                tp.Upper = (int)Math.Max(0, upper);
                byKey[type.Key] = tp;
            }
            profile.Types = byKey.Values.ToList();
            // 打平容量
            for (int f = 0; f < Schemes.Count; f++)
            {
                for (int k = 0; k < Schemes[f].Classes; k++)
                {
                    profile.Capacity[f * 9 + k] = cap[f][k];
                }
            }
            return profile;
        }

        /// <summary>带缓存地取某盘 profile。</summary>
        public static BoardProfile Get(Grid grid, IList<List<Cell>> shapes)
        {
            string key = BoardKey(grid);
            lock (cache)
            {
                if (cache.TryGetValue(key, out var p))
                {
                    return p;
                }
            }
            var built = Build(grid, shapes);
            lock (cache)
            {
                cache[key] = built;
            }
            return built;
        }

        /// <summary>按形状键取该类型的 profile。</summary>
        public static TypeProfile ForShape(BoardProfile board, List<Cell> shape)
        {
            if (board == null)
            {
                return null;
            }
            string key = Packer.GetType(shape).Key;
            return board.Types.FirstOrDefault(t => t.Key == key && t.Area == shape.Count);
        }

        /// <summary>固定数量组合的预检（必要条件）：返回 null 表示通过，否则给出拒绝证据。</summary>
        public static string CheckCounts(BoardProfile board, Dictionary<string, int> typeCounts, Dictionary<string, List<Cell>> shapeOfType)
        {
            if (board == null)
            {
                return null;
            }
            foreach (var kv in typeCounts)
            {
                if (!shapeOfType.TryGetValue(kv.Key, out var shape))
                {
                    continue;
                }
                var tp = board.Types.FirstOrDefault(t => t.Key == kv.Key);
                if (tp == null)
                {
                    continue;
                }
                if (kv.Value > tp.Upper)
                {
                    return $"形状类型{kv.Key}数量{kv.Value}超过上界{tp.Upper}";
                }
            }
            return null;
        }
    }
}
