using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Google.OrTools.Sat;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft.Tests
{
    /// <summary>离线测试用的 CP-SAT 包装：复用 src 的 CpSatQuantity + 装填 + 计时（不重复建模）。</summary>
    public static class CpSatSolver
    {
        public class Result
        {
            public bool Ok;
            public List<(Material mat, Side side)> Items = new();
            public long TotalPrice;
            public int PieceCount => Items.Count;
            public long QuantityMs;
            public long PackMs;
            public CpSolverStatus Status;
            public string Message;
        }

        public static Result Solve(List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin, double timeLimitSec)
        {
            var sw = Stopwatch.StartNew();
            var res = new Result();
            var items = CpSatQuantity.Solve(materials, runes, bonus, yang, yin, timeLimitSec);
            res.QuantityMs = sw.ElapsedMilliseconds;
            if (items == null)
            {
                res.Message = "CP-SAT 无解/超时";
                res.Status = CpSolverStatus.Unknown;
                return res;
            }
            res.Items = items;
            res.TotalPrice = items.Sum(x => (long)x.mat.Price);
            res.Status = CpSolverStatus.Optimal;
            sw.Restart();
            var sol = PackItems(materials, yang, yin, items, 500);
            res.PackMs = sw.ElapsedMilliseconds;
            res.Ok = sol != null;
            res.Message = (res.Ok ? "最优" : "数量解装不下") + "+装填" + (res.Ok ? "成功" : "失败");
            return res;
        }

        public static EquipmentSolution PackItems(List<Material> materials, Grid yang, Grid yin, List<(Material mat, Side side)> items, int packTimeMs)
        {
            var sol = new EquipmentSolution();
            var sw = Stopwatch.StartNew();
            foreach (Side side in new[] { Side.Yang, Side.Yin })
            {
                var pieces = items.Where(x => x.side == side).Select(x => x.mat).ToList();
                if (pieces.Count == 0)
                {
                    continue;
                }
                var grid = side == Side.Yang ? yang : yin;
                if (grid == null)
                {
                    return null;
                }
                var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), sw, packTimeMs);
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
    }
}
