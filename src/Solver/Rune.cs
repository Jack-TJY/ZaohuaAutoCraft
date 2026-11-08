using System.Collections.Generic;

namespace ZaohuaAutoCraft.Solver
{
    // 炼器（阶段 3）的纯数据模型，与炼丹的 DrugRequest/Solution 平行，不碰 Unity。

    /// <summary>符文（TbCraftingRuneCfg）的纯数据拷贝。</summary>
    public class Rune
    {
        public string Id;

        public string Name;

        /// <summary>要求属性：正数要求值，非要求维度为 0。来自 attrLimiteStr（属性:数值;…）。</summary>
        public int[] Requirement = new int[Attr.Count];

        public int MaxStacks;

        /// <summary>参与品阶的权重；为 0 时用 DefaultWeight（CommonConst.CraftingRuneWeight[grade.weight]）。</summary>
        public int Weight;

        /// <summary>Weight == 0 时的兜底权重，由桥接/测试层预填好。</summary>
        public int DefaultWeight;
    }

    public enum EquipmentMode
    {
        /// <summary>生效（等级≥1）的符文个数最多，再比总等级。</summary>
        MaxActiveRunes,

        /// <summary>玩家勾选的符文先到 maxStacks，剩余空间再给其它符文。</summary>
        MaxRuneLevel,

        /// <summary>所有已选符文至少 1 级的前提下件数最少。</summary>
        MinPieces,

        /// <summary>同上前提，材料品阶最低。</summary>
        LowGrade,

        /// <summary>符文等级尽量高，同等级比炼制次数。</summary>
        MaxBatches,

        /// <summary>指定属性最大（法器属性按冰风雷×10取最大）。</summary>
        MaxAttr
    }

    /// <summary>材料策略：在符文质量确定后如何取舍材料。</summary>
    public enum MaterialPolicy
    {
        /// <summary>同一摆法可重复炼制次数最多。</summary>
        MaxRepeatCount,

        /// <summary>低阶材料优先（品阶数量向量按从高到低字典序最小）。</summary>
        LowGradeFirst
    }

    public class EquipmentRequest
    {
        public List<Material> Materials = new List<Material>();

        /// <summary>玩家已选的符文。</summary>
        public List<Rune> Runes = new List<Rune>();

        /// <summary>锻造锤与天赋的固定属性加成。</summary>
        public int[] Bonus = new int[Attr.Count];

        public Grid Yang;

        public Grid Yin;

        public EquipmentMode Mode = EquipmentMode.MinPieces;

        public int TimeLimitMs = 1000;

        public int MaxPieces = 24;

        public int TopK = 3;

        /// <summary>MaxRuneLevel 模式：与 Runes 对齐的每符文目标等级。</summary>
        public int[] TargetLevel;

        /// <summary>MaxRuneLevel 模式：与 Runes 对齐的「优先符文」标记（优先符文尽量满级）。</summary>
        public bool[] Priority;

        /// <summary>统一流程：与 Runes 对齐的优先级组号（0=最高优先，依次往后；null 或全相同=全部同组）。</summary>
        public int[] PriorityGroup;

        /// <summary>统一流程：材料策略（符文质量确定后再优化）。</summary>
        public MaterialPolicy MaterialPolicy = MaterialPolicy.MaxRepeatCount;

        /// <summary>MaxAttr 模式：目标属性维度（0–7），-1 表示未指定。</summary>
        public int TargetAttr = -1;

        /// <summary>允许「部分达成备选」：固定符文组合无法全部满足时，返回放弃部分符文的备选（默认关闭）。</summary>
        public bool AllowPartial;
    }

    /// <summary>符文结算结果（对应 CraftingEquipmentTmp 里我们关心的部分）。</summary>
    public class RuneResult
    {
        /// <summary>与 Runes 对齐的每符文等级（不生效为 0）。</summary>
        public int[] Level = new int[0];

        public int ActiveCount;

        public int TotalLevel;

        /// <summary>Σ 权重×等级，用于品阶档位。</summary>
        public long GradeScore;

        /// <summary>摆完后每属性总值（bonus + Σ贡献）。</summary>
        public int[] FinalAttrs = new int[Attr.Count];

        /// <summary>法器属性：冰风雷×10取最大；并列或全零为 0（取值 0–7，游戏枚举在此基础上 +1）。</summary>
        public int ItemAttr;
    }

    public class EquipmentSolution
    {
        public List<Placement> Placements = new List<Placement>();

        public int PieceCount => Placements.Count;

        public int MaxGrade;

        public int GradeSum;

        public long Price;

        public RuneResult Runes;

        /// <summary>按库存能连续炼几次。</summary>
        public int Batches = 1;
    }

    public class EquipmentSolveResult
    {
        public EquipmentSolution Best => Solutions.Count > 0 ? Solutions[0] : null;

        public List<EquipmentSolution> Solutions = new List<EquipmentSolution>();

        /// <summary>能凑出属性但装不下。</summary>
        public bool FoundComboButNoFit;

        public bool TimedOut;

        public int CombosTried;

        public long ElapsedMs;

        public string Message;

        /// <summary>求解状态：CP-SAT最优 / CP-SAT可行(限时) / 启发式 / 已证无解 / 未定(超时)。</summary>
        public string Status;
    }
}
