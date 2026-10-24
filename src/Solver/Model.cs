using System.Collections.Generic;

namespace ZaohuaAutoCraft.Solver
{
    // 求解器只用这里的纯数据，不碰 Unity 和游戏对象，方便在游戏外测试。

    /// <summary>与游戏 CraftingAttrEnum 顺序一致（去掉 none）。</summary>
    public static class Attr
    {
        public const int Count = 8;

        public static readonly string[] Keys = { "gold", "water", "wood", "fire", "soil", "ice", "wind", "thunder" };

        public static readonly string[] Names = { "金", "水", "木", "火", "土", "冰", "风", "雷" };

        public static int IndexOf(string key)
        {
            return System.Array.IndexOf(Keys, key);
        }

        public static int[] FromDic(IDictionary<string, int> dic)
        {
            var v = new int[Count];
            if (dic == null)
            {
                return v;
            }
            foreach (var kv in dic)
            {
                int i = IndexOf(kv.Key);
                if (i >= 0)
                {
                    v[i] += kv.Value;
                }
            }
            return v;
        }
    }

    public struct Cell
    {
        public int X;
        public int Y;

        public Cell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public override string ToString() => $"({X},{Y})";
    }

    public class Material
    {
        public string Id;

        public string Name;

        /// <summary>放在阳格时的属性；放阴格取反。</summary>
        public int[] Attrs = new int[Attr.Count];

        /// <summary>游戏 TbDrawCfg.Coordinates，未旋转。</summary>
        public List<Cell> Shape = new List<Cell>();

        public long Stock;

        /// <summary>TbGradeCfg.weight，越大越高阶。</summary>
        public int Grade;

        public int Price;
    }

    public class Grid
    {
        public int Width;

        public int Height;

        public int Area => Width * Height;

        public Grid(int w, int h)
        {
            Width = w;
            Height = h;
        }

        // 游戏坐标以中心为原点：x ∈ [(1-W)/2, W/2]，y ∈ [(1-H)/2, H/2]
        public int MinX => (1 - Width) / 2;

        public int MinY => (1 - Height) / 2;
    }

    public enum Side
    {
        Yang = 1,
        Yin = 2
    }

    /// <summary>一件材料的摆放，坐标和旋转都是游戏的约定（TbCraftingItemLogSto）。</summary>
    public class Placement
    {
        public Material Material;

        public Side Side;

        public int X;

        public int Y;

        /// <summary>DantianGridDirEnum：0 上 1 右 2 下 3 左。</summary>
        public int Rotate;
    }

    public class DrugRequest
    {
        public List<Material> Materials = new List<Material>();

        /// <summary>材料（阳加阴减）之和必须正好等于它：丹方要求减去丹炉与天赋的固定加成。</summary>
        public int[] Need = new int[Attr.Count];

        public Grid Yang;

        public Grid Yin;

        /// <summary>每种材料至少要够炼的批数。</summary>
        public int Batch = 1;

        public int MaxPieces = 24;

        public int TimeLimitMs = 1000;
    }

    public class Solution
    {
        public List<Placement> Placements = new List<Placement>();

        public int PieceCount => Placements.Count;

        public int MaxGrade;

        public int GradeSum;

        public long Price;
    }

    public class SolveResult
    {
        public Solution Best;

        /// <summary>凑数有解但都装不下时为 true，方便提示换大丹炉。</summary>
        public bool FoundComboButNoFit;

        public bool TimedOut;

        public int CombosTried;

        public long ElapsedMs;

        public string Message;
    }
}
