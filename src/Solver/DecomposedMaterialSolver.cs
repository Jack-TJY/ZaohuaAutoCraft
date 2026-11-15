using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Google.OrTools.Sat;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 材料阶段「分解法固定批次数 B 的可行性二分」实验求解器（默认不启用，见 EquipmentSolver.DecomposedMaterial）。
    ///
    /// 背景：材料阶段现在用 JointGeometrySolver 的 b×usage≤stock 整数乘法一次性最大化批次数，
    /// 乘法约束让 CP-SAT 很难在 12 秒内证明最优（实测稳定顶满预算返回 Feasible，批次 9~10）。
    /// 这里改成「固定 B 的纯线性可行性」：B 固定后 b×usage≤stock 等价于每种材料 usage_i ≤ floor(stock_i/B)，
    /// 模型退化成 0-1/整数线性，CP-SAT 能快速给出 Feasible / 可证明的 Infeasible；外层对 B 二分取最大可行值。
    ///
    /// 对给定 B 的可行性检查（与 CoverageGateProver 已验证可用的「数量模型 → 装填 → 切割重解」同构）：
    ///   1) 数量模型：阴阳两面数量变量 + 面积约束 + 属性硬下限（把固定等级向量按符文需求折算成每维净属性下限，
    ///      与 CoverageGateProver/ScaleRunes 的折算一致）+ 每种材料 usage_i ≤ floor(stock_i/B)，
    ///      目标 min 总件数；指定成品属性时追加与 CpSatQuantity.SolveUnified 同口径的归属硬约束。
    ///   2) Packer.Pack 阴阳两面分别真实装填；两面都成功 → B 可行，摆法即见证（真实批次按 min(stock/usage) 复算）。
    ///   3) 某面装填器【穷尽搜索证明装不下且未超时】→ 把该面材料缩小成「极小不可装核」，
    ///      按核的形状类型计数生成 ShapeCut 回灌数量模型重解（核心的所有超集都装不下，切割是有效必要条件）。
    ///   4) 装填超时 → 该 B 判 Unknown（绝不当作不可行）。
    ///
    /// 三态结论：Feasible（带回摆法）/ Infeasible（预算内所有探针都被数量模型证不可行）/ Unknown（未判定）。
    /// 二分上界 = min(64, 激活材料最大库存)（任一被用材料的单次用量≥1 且 B×用量≤库存 ⇒ B≤该材料库存）；
    /// 下界直接用调用方候选（材料阶段现有候选）的批次数——候选本身就是可行见证，不必重解。
    /// Unknown 探针保守处理：不抬下界（不当作可行），也不宣称全局不可行；最终只返回已证明可行的最好摆法。
    ///
    /// 正确性：只有当 Packer 穷尽证明装不下时才生成切割；数量模型 Infeasible 意味着该 B 的真实问题无解
    /// （面积/库存/属性是真实问题的必要条件，切割是极小不可装核，均为有效必要条件）。
    /// 返回的 Items 仍必须由调用方用 EquipmentSolver.Verify 独立复核后才能采用。
    ///
    /// 材料策略（MaterialPolicy）：
    ///   · MaxRepeatCount：固定批次数 B 的可行性二分（本类原有语义，最大化可重复炼制次数）。
    ///   · LowGradeFirst：不做 B 二分，单次求解数量模型；目标改为最小化品阶秩加权和
    ///     （与 JointGeometrySolver 的 LowGradeFirst 分支同口径：激活材料品阶去重排序得 rank，
    ///     每件权重 rank*2000+1，再加 -1/件 作次级最小件数目标）。库存约束相应退化为单次用量的
    ///     usage_i ≤ stock_i（不再按 B 均摊），与 JointGeometrySolver 低阶优先分支一致。
    ///     规格目标同分的解再按材料价格做三级 tie-break（对齐最终排序键的「秩和 → 价格」顺序，
    ///     避免等优解随机漂移造成材料总价偏离基线）。
    ///   两条路径共用同一套「数量模型 → 阴阳两面装填 → 极小不可装核切割重解」闭环与三态语义。
    /// </summary>
    public static class DecomposedMaterialSolver
    {
        /// <summary>材料阶段分解法结果：三态 + 最好可行批次 + 摆法（Feasible 时）。</summary>
        public class Result
        {
            /// <summary>Feasible / Infeasible / Unknown。</summary>
            public string Status = "Unknown";

            /// <summary>按摆法与真实库存复算的批次数 = min_i floor(stock_i / usage_i)。</summary>
            public int Batches;

            /// <summary>Feasible 的摆法：材料、阴阳面、盘内坐标（与 Packer/游戏约定一致）、旋转。</summary>
            public List<(Material mat, Side side, int x, int y, int rot)> Items = new List<(Material, Side, int, int, int)>();

            /// <summary>总耗时（毫秒）。</summary>
            public long ElapsedMs;

            /// <summary>诊断文本（探针/装填/切割统计与未判定原因）。</summary>
            public string Detail = "";
        }

        /// <summary>批次数二分搜索上限（与任务约定一致；真实库存实测批次 9~10，64 足够覆盖）。</summary>
        public const int MaxBatchesCap = 64;

        /// <summary>单次数量模型求解的时间分片上限（毫秒）。</summary>
        public const long QuantitySliceMs = 1500L;

        /// <summary>单面装填 / 缩小不可装核的单次时间分片上限（毫秒）。</summary>
        public const long PackSliceMs = 400L;

        /// <summary>缩小不可装核时每次试探装填的时间分片（毫秒）。</summary>
        public const long CoreSliceMs = 200L;

        /// <summary>剩余预算低于该值立即收尾（毫秒）。</summary>
        public const long MinRemainMs = 200L;

        /// <summary>单个批次数 B 最多重解轮数（每轮排除一个装不下的形状多重集）。</summary>
        public const int MaxCutRounds = 6;

        /// <summary>
        /// LowGradeFirst 单次求解的最大切割重解轮数：低阶优先的秩加权目标会把数量解推到几何上更难装填的
        /// 料型组合（同秩和/同件数下价格更低的组合很多），实测 6 轮不足以收敛到可装填解，
        /// 单轮 ~40ms，放宽到 40 轮仍在 6 秒预算内。
        /// </summary>
        public const int LowGradeMaxCutRounds = 40;

        /// <summary>一次 B 的可判定性探针（对外只暴露统计用的私有上下文）。</summary>
        class Ctx
        {
            public List<Material> Materials;
            public List<Rune> Runes;
            public int[] QualityLevel;
            public int[] Bonus;
            public Grid Yang;
            public Grid Yin;
            public bool[] Required;
            public int[] Need;
            public int TargetAttr = -1;
            public RuneEvaluator Eval;
            public MaterialPolicy Policy = MaterialPolicy.MaxRepeatCount;
            public long EndMs;
            public Stopwatch Sw;
            public int SolveCalls;
            public int PackCalls;
            public int CutTotal;
        }

        /// <summary>
        /// 固定质量向量后，按材料策略求材料阶段摆法。
        /// MaxRepeatCount：固定批次数 B 的可行性二分，取最大可行批次数；
        /// LowGradeFirst：单次求解，最小化品阶秩加权和（次级最少件数），再装填/切割闭环取可行摆法。
        /// </summary>
        /// <param name="materials">炼器材料（含库存/形状/属性）。</param>
        /// <param name="runes">已选符文。</param>
        /// <param name="bonus">锻造锤/天赋的固定属性加成。</param>
        /// <param name="yang">阳盘。</param>
        /// <param name="yin">阴盘。</param>
        /// <param name="priorityGroup">与 runes 对齐的优先级组号；分解法只判可行性，不建模优先级（保留签名与主流程一致）。</param>
        /// <param name="desiredAttr">指定成品属性维度，-1 表示不指定（会建模归属硬约束，复核仍由调用方做）。</param>
        /// <param name="qualityLevel">要锁定的质量向量（符文等级下限，主流程传当前候选的 Level）。</param>
        /// <param name="hintCandidate">材料阶段当前候选（提供批次下界与起点见证，可为 null；LowGradeFirst 不使用）。</param>
        /// <param name="budgetMs">总预算（毫秒）。</param>
        /// <param name="policy">材料策略：MaxRepeatCount（默认，B 二分）/ LowGradeFirst（单次低阶优先）。</param>
        public static Result Solve(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin,
            int[] priorityGroup, int desiredAttr, int[] qualityLevel,
            JointGeometrySolver.Candidate hintCandidate, long budgetMs,
            MaterialPolicy policy = MaterialPolicy.MaxRepeatCount)
        {
            var res = new Result();
            var sw = Stopwatch.StartNew();
            Result Unproven(string detail)
            {
                res.Status = "Unknown";
                res.Detail = detail;
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }
            try
            {
                if (materials == null || runes == null || runes.Count == 0)
                {
                    return Unproven("输入为空（未判定）");
                }
                if (bonus == null || bonus.Length != Attr.Count)
                {
                    bonus = new int[Attr.Count];
                }
                if (qualityLevel == null || qualityLevel.Length < runes.Count)
                {
                    return Unproven("缺少质量向量（未判定）");
                }
                if (budgetMs < MinRemainMs)
                {
                    return Unproven($"预算不足({budgetMs}ms)（未判定）");
                }
                if (yang == null || yang.Area <= 0)
                {
                    return Unproven("阳盘缺失或为空（未判定）");
                }
                long endMs = sw.ElapsedMilliseconds + budgetMs;

                // ── 步骤 0：固定等级向量 → 每维净属性硬下限（同 CoverageGateProver.ScaleRunes 的折算口径）──
                var required = new bool[Attr.Count];
                var need = new int[Attr.Count];
                for (int r = 0; r < runes.Count; r++)
                {
                    int lv = Math.Max(0, qualityLevel[r]);
                    if (lv <= 0 || runes[r].Requirement == null)
                    {
                        continue;
                    }
                    for (int d = 0; d < Attr.Count; d++)
                    {
                        int req = runes[r].Requirement[d];
                        if (req <= 0)
                        {
                            continue;
                        }
                        required[d] = true;
                        need[d] = Math.Max(need[d], req * lv);
                    }
                }
                for (int d = 0; d < Attr.Count; d++)
                {
                    need[d] -= bonus[d];
                }
                int ta = desiredAttr >= 0 ? Math.Min(desiredAttr, Attr.Count - 1) : -1;
                if (!required.Any(x => x) && ta < 0)
                {
                    return Unproven("质量向量无属性要求且未指定成品属性，分解法无意义（未判定）");
                }

                var ctx = new Ctx
                {
                    Materials = materials,
                    Runes = runes,
                    QualityLevel = qualityLevel,
                    Bonus = bonus,
                    Yang = yang,
                    Yin = yin,
                    Required = required,
                    Need = need,
                    TargetAttr = ta,
                    Eval = new RuneEvaluator(runes, bonus),
                    Policy = policy,
                    EndMs = endMs,
                    Sw = sw,
                };

                // ── 策略分支：低阶优先不做 B 二分，单次求解「最小化品阶秩加权和」──
                if (policy == MaterialPolicy.LowGradeFirst)
                {
                    return SolveLowGradeFirst(ctx, res, sw);
                }

                // ── 步骤 1：批次数搜索区间 ──
                // 上界：任一被用材料 usage_i≥1 且 B×usage_i≤stock_i ⇒ B≤stock_i≤激活材料最大库存；再按 64 封顶。
                long maxStock = 0;
                for (int i = 0; i < materials.Count; i++)
                {
                    var m = materials[i];
                    if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                    {
                        continue;
                    }
                    maxStock = Math.Max(maxStock, Math.Min(m.Stock, 100000L));
                }
                if (maxStock < 1)
                {
                    res.Status = "Infeasible";
                    res.Detail = "没有可用材料（已证不可行）";
                    res.ElapsedMs = sw.ElapsedMilliseconds;
                    return res;
                }
                int hi = (int)Math.Min((long)MaxBatchesCap, maxStock);

                // 起点下界：材料阶段现有候选的摆法本身就是一个可行见证（批次 = min(stock/用量)）。
                int hintBatches = 0;
                var bestItems = new List<(Material mat, Side side, int x, int y, int rot)>();
                if (hintCandidate != null && hintCandidate.Items.Count > 0)
                {
                    hintBatches = BatchesOf(hintCandidate.Items);
                    if (hintBatches > 0)
                    {
                        bestItems.AddRange(hintCandidate.Items);
                    }
                }
                bool haveBest = bestItems.Count > 0 && hintBatches > 0;
                int lo = haveBest ? hintBatches : 1;
                if (hi < lo)
                {
                    hi = lo;
                }
                int probes = 0;
                int feasibleProbes = 0;
                int infeasibleProbes = 0;
                int unknownProbes = 0;
                var unknownNotes = new List<string>();

                // ── 步骤 2：没有候选起点时先探 B=1，建立可行见证或直接判定整个材料阶段不可行 ──
                if (!haveBest)
                {
                    var first = TryB(ctx, 1);
                    probes++;
                    if (first.Status == "Feasible" && first.Batches > 0)
                    {
                        haveBest = true;
                        feasibleProbes++;
                        lo = Math.Max(1, first.Batches);
                        bestItems = first.Items;
                    }
                    else if (first.Status == "Infeasible")
                    {
                        infeasibleProbes++;
                        res.Status = "Infeasible";
                        res.Detail = $"B=1 数量模型已证不可行（{first.Detail}；探针{probes} 求解{ctx.SolveCalls}次 装填{ctx.PackCalls}次）";
                        res.ElapsedMs = sw.ElapsedMilliseconds;
                        return res;
                    }
                    else
                    {
                        unknownProbes++;
                        unknownNotes.Add($"1:{first.Detail}");
                        return Unproven($"B=1 未判定（{first.Detail}；预算{budgetMs}ms）");
                    }
                }

                // ── 步骤 3：对批次数 B 二分（谓词：B 可行；可行性对 B 单调）──
                while (lo < hi)
                {
                    if (RemainingMs(ctx.EndMs, sw) < MinRemainMs)
                    {
                        unknownProbes++;
                        unknownNotes.Add("预算用尽");
                        break;
                    }
                    int mid = lo + (hi - lo + 1) / 2;
                    var attempt = TryB(ctx, mid);
                    probes++;
                    if (attempt.Status == "Feasible" && attempt.Batches > lo)
                    {
                        feasibleProbes++;
                        lo = attempt.Batches;
                        bestItems = attempt.Items;
                    }
                    else if (attempt.Status == "Infeasible")
                    {
                        infeasibleProbes++;
                        hi = mid - 1;
                    }
                    else
                    {
                        // Unknown（超时/未判定）：不抬下界（不当作可行），保守收缩上界继续找已证可行的更大值；
                        // 全局上界因此未被证明，最终 Detail 里如实标注。
                        unknownProbes++;
                        if (unknownNotes.Count < 4)
                        {
                            unknownNotes.Add($"{mid}:{attempt.Detail}");
                        }
                        hi = mid - 1;
                    }
                }

                if (!haveBest)
                {
                    return Unproven($"没有探针给出可行摆法（探针{probes} 不可行{infeasibleProbes}/未知{unknownProbes}）");
                }

                // ── 步骤 4：按真实库存复算批次，返回已证明可行的最好摆法 ──
                int real = BatchesOf(bestItems);
                if (real <= 0)
                {
                    return Unproven("可行摆法复算批次为 0（未判定）");
                }
                string boundNote;
                if (unknownProbes > 0)
                {
                    boundNote = $"上界未证（未知探针{unknownProbes}：{string.Join("；", unknownNotes)}）";
                }
                else
                {
                    boundNote = $"上界已证（搜索上限{hi}）";
                }
                res.Status = "Feasible";
                res.Batches = real;
                res.Items = bestItems;
                res.Detail =
                    $"最优批次{real}（起点{hintBatches} 探针{probes}：可行{feasibleProbes}/不可行{infeasibleProbes}/未知{unknownProbes}；"
                    + $"数量模型{ctx.SolveCalls}次 装填{ctx.PackCalls}次 切割{ctx.CutTotal}次；{boundNote}）";
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }
            catch (Exception ex)
            {
                // 分解法任何异常都按未判定处理，绝不能影响主流程正确性
                res.Status = "Unknown";
                res.Detail = "异常（未判定）：" + ex.Message;
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }
        }

        /// <summary>
        /// 低阶优先（MaterialPolicy.LowGradeFirst）：不做批次数 B 二分，单次求解数量模型。
        /// 目标与 JointGeometrySolver 的同策略分支同口径（最小化品阶秩加权和，次级最少件数），
        /// 装填闭环/极小不可装核切割/预算控制/三态语义完全复用 TryB 的 b=0 模式。
        /// </summary>
        static Result SolveLowGradeFirst(Ctx ctx, Result res, Stopwatch sw)
        {
            var attempt = TryB(ctx, 0);
            if (attempt.Status == "Feasible" && attempt.Batches > 0)
            {
                res.Status = "Feasible";
                res.Batches = attempt.Batches;
                res.Items = attempt.Items;
                res.Detail = $"低阶优先秩加权 实批{attempt.Batches}（{attempt.Detail}）";
            }
            else
            {
                res.Status = attempt.Status == "Infeasible" ? "Infeasible" : "Unknown";
                res.Detail = attempt.Detail;
            }
            res.ElapsedMs = sw.ElapsedMilliseconds;
            return res;
        }

        static long RemainingMs(long endMs, Stopwatch sw)
        {
            return endMs - sw.ElapsedMilliseconds;
        }

        /// <summary>
        /// 判定「固定批次数 B 可行」（b&gt;0，MaxRepeatCount）或「低阶优先单次求解」（b==0，LowGradeFirst）：
        /// 数量模型（纯线性）→ 两面装填 → 装不下则极小不可装核切割重解。
        /// 返回 Feasible（Items 为见证摆法）/ Infeasible（数量模型已证不可行）/ Unknown（超时或未判定）。
        /// </summary>
        static Result TryB(Ctx ctx, int b)
        {
            var res = new Result();
            if (b < 0)
            {
                res.Status = "Infeasible";
                res.Detail = "B<0";
                return res;
            }
            bool lowGrade = b == 0;
            string tag = lowGrade ? "低阶优先" : $"B={b}";
            try
            {
                // ── 数量模型：两面数量变量 + 面积 + 属性硬下限 ──
                // MaxRepeatCount：usage_i ≤ floor(stock_i/B)；LowGradeFirst：usage_i ≤ stock_i（单次用量上限）
                var model = new CpModel();
                int n = ctx.Materials.Count;
                var yangVar = new IntVar[n];
                var yinVar = new IntVar[n];
                var active = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    var m = ctx.Materials[i];
                    if (m.Stock <= 0 || m.Shape.Count == 0 || m.Attrs.All(v => v == 0))
                    {
                        continue;
                    }
                    if (ctx.TargetAttr < 0)
                    {
                        // 未指定成品属性时按「能参与某维需求」剪枝（同 CoverageGateProver 复用 CpSatQuantity.Solve 的剪枝口径）：
                        // 某个维度 attr≠0 就能在阳(+attr) 或阴(-attr) 侧产生正贡献。
                        bool helps = false;
                        for (int d = 0; d < Attr.Count; d++)
                        {
                            if (ctx.Required[d] && m.Attrs[d] != 0)
                            {
                                helps = true;
                                break;
                            }
                        }
                        if (!helps)
                        {
                            continue;
                        }
                    }
                    int areaOne = Math.Max(1, m.Shape.Count);
                    // MaxRepeatCount：每种材料单次用量 ≤ floor(stock/B)；LowGradeFirst 不建模批次数，单次用量 ≤ stock
                    long perBatch = lowGrade ? Math.Min(m.Stock, 100000L) : Math.Min(m.Stock, 100000L) / b;
                    if (perBatch <= 0)
                    {
                        continue; // 库存不够（B 次各用 1 件 / 单次 1 件）
                    }
                    int yMax = ctx.Yang != null && Packer.CanFit(ctx.Yang, m.Shape)
                        ? (int)Math.Min(perBatch, ctx.Yang.Area / areaOne) : 0;
                    int nMax = ctx.Yin != null && Packer.CanFit(ctx.Yin, m.Shape)
                        ? (int)Math.Min(perBatch, ctx.Yin.Area / areaOne) : 0;
                    if (yMax <= 0 && nMax <= 0)
                    {
                        continue;
                    }
                    yangVar[i] = model.NewIntVar(0, yMax, $"y{i}");
                    yinVar[i] = model.NewIntVar(0, nMax, $"n{i}");
                    active.Add(i);
                    model.Add(LinearExpr.Sum(new List<LinearExpr>
                    {
                        LinearExpr.Term(yangVar[i], 1),
                        LinearExpr.Term(yinVar[i], 1),
                    }) <= perBatch);
                }
                if (active.Count == 0)
                {
                    res.Status = "Infeasible";
                    res.Detail = lowGrade ? $"{tag} 没有可用材料" : $"{tag} 没有库存够 B 次的可用材料";
                    return res;
                }
                if (ctx.Yang != null)
                {
                    model.Add(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yangVar[i], ctx.Materials[i].Shape.Count))) <= ctx.Yang.Area);
                }
                if (ctx.Yin != null)
                {
                    model.Add(LinearExpr.Sum(active.Select(i => LinearExpr.Term(yinVar[i], ctx.Materials[i].Shape.Count))) <= ctx.Yin.Area);
                }
                // 属性硬下限：每维 Σ(阳-阴)*attr ≥ max_r(req*质量等级) - bonus
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (!ctx.Required[d])
                    {
                        continue;
                    }
                    var terms = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        int a = ctx.Materials[i].Attrs[d];
                        if (a == 0)
                        {
                            continue;
                        }
                        terms.Add(LinearExpr.Term(yangVar[i], a));
                        terms.Add(LinearExpr.Term(yinVar[i], -a));
                    }
                    if (terms.Count == 0)
                    {
                        if (ctx.Need[d] > 0)
                        {
                            res.Status = "Infeasible";
                            res.Detail = $"{tag} 属性{Attr.Names[d]}无材料可提供（需{ctx.Need[d]}）";
                            return res;
                        }
                        continue;
                    }
                    model.Add(LinearExpr.Sum(terms) >= ctx.Need[d]);
                }
                // 指定成品属性：与 CpSatQuantity.SolveUnified 的 desiredAttr 硬约束同口径（否则复核必败）
                AddTargetAttrConstraints(model, ctx, active, yangVar, yinVar);
                if (lowGrade)
                {
                    // LowGradeFirst 目标（与 JointGeometrySolver 同策略分支同口径）：
                    // 先最小化品阶秩加权和（激活材料品阶去重升序得 rank，每件权重 rank*2000+1），
                    // 再加 -1/件 作次级最小件数目标（两项合计每件 rank*2000+2）。
                    // 三级 tie-break：价格。规格目标完全相同的解（同秩和同件数）在最终排序键
                    // UnifiedRankKey 里本来就按「秩和 → 价格 → 件数」比较，而同一品阶不同材料价格可差数万；
                    // 若不加价格项，CP-SAT 会在等优解里任意挑一个，实测导致材料总价从基线 73 万漂到 77 万。
                    // 因此把规格目标整体放大 SpecScale 倍后再叠加原始价格系数，
                    // 保证：任何规格目标更差的解都不会被选中（主/次级目标严格不变），仅在同分时选更便宜的。
                    const long SpecScale = 1000000L;
                    var grades = active.Select(i => ctx.Materials[i].Grade).Distinct().OrderBy(g => g).ToList();
                    var rank = new Dictionary<int, int>();
                    for (int gi = 0; gi < grades.Count; gi++)
                    {
                        rank[grades[gi]] = gi;
                    }
                    var obj = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        long w = (rank.TryGetValue(ctx.Materials[i].Grade, out var r2) ? r2 : 0) * 2000L + 1;
                        long coef = SpecScale * (w + 1) + Math.Max(0, ctx.Materials[i].Price);
                        obj.Add(LinearExpr.Term(yangVar[i], -coef));
                        obj.Add(LinearExpr.Term(yinVar[i], -coef));
                    }
                    model.Maximize(LinearExpr.Sum(obj));
                }
                else
                {
                    // 目标：min 总件数（件数少的方案面积更小，装填成功率更高；可行性结论不受目标影响）
                    var pieceTerms = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        pieceTerms.Add(LinearExpr.Term(yangVar[i], 1));
                        pieceTerms.Add(LinearExpr.Term(yinVar[i], 1));
                    }
                    model.Minimize(LinearExpr.Sum(pieceTerms));
                }

                // ── 装填 / 切割重解循环 ──
                int maxRounds = lowGrade ? LowGradeMaxCutRounds : MaxCutRounds;
                var roundNotes = new List<string>();
                for (int round = 0; round < maxRounds; round++)
                {
                    long remain = RemainingMs(ctx.EndMs, ctx.Sw);
                    if (remain < MinRemainMs)
                    {
                        res.Status = "Unknown";
                        res.Detail = $"{tag} 预算用尽（轮{round}）";
                        return res;
                    }
                    long slice = Math.Min(QuantitySliceMs, remain);
                    var solver = new CpSolver();
                    solver.StringParameters =
                        $"max_time_in_seconds:{(slice / 1000.0).ToString("F3", CultureInfo.InvariantCulture)},"
                        + $"num_search_workers:{(CpSatQuantity.Deterministic ? 1 : Math.Max(1, CpSatQuantity.Workers))},random_seed:0";
                    var status = solver.Solve(model);
                    ctx.SolveCalls++;
                    if (status == CpSolverStatus.Infeasible)
                    {
                        res.Status = "Infeasible";
                        res.Detail = $"{tag} 数量模型Infeasible（切割{round}次）";
                        return res;
                    }
                    if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
                    {
                        res.Status = "Unknown";
                        res.Detail = $"{tag} 数量模型{status}（切割{round}次）";
                        return res;
                    }
                    var items = new List<(Material mat, Side side)>();
                    foreach (int i in active)
                    {
                        int vy = (int)solver.Value(yangVar[i]);
                        int vn = (int)solver.Value(yinVar[i]);
                        for (int k = 0; k < vy; k++)
                        {
                            items.Add((ctx.Materials[i], Side.Yang));
                        }
                        for (int k = 0; k < vn; k++)
                        {
                            items.Add((ctx.Materials[i], Side.Yin));
                        }
                    }
                    if (items.Count == 0)
                    {
                        // 空方案不能证明「B 次」（游戏结算批次=0），按未判定处理
                        res.Status = "Unknown";
                        res.Detail = $"{tag} 数量模型给出空方案";
                        return res;
                    }
                    // 自检：真实等级/成品属性必须达到模型约束（模型口径错误时宁可判未判定，也不能返回假可行）
                    string selfErr = SelfCheck(ctx, items);
                    if (selfErr != null)
                    {
                        res.Status = "Unknown";
                        res.Detail = $"{tag} 自检失败：{selfErr}";
                        return res;
                    }
                    // 阴阳两面分别真实装填
                    CpSatQuantity.ShapeCut failCut = null;
                    var witness = new List<(Material mat, Side side, int x, int y, int rot)>();
                    foreach (Side side in new[] { Side.Yang, Side.Yin })
                    {
                        var pieces = items.Where(x => x.side == side).Select(x => x.mat).ToList();
                        if (pieces.Count == 0)
                        {
                            continue;
                        }
                        var grid = side == Side.Yang ? ctx.Yang : ctx.Yin;
                        if (grid == null || grid.Area <= 0)
                        {
                            res.Status = "Unknown";
                            res.Detail = $"{tag} {(side == Side.Yang ? "阳" : "阴")}盘缺失";
                            return res;
                        }
                        long packRemain = RemainingMs(ctx.EndMs, ctx.Sw);
                        if (packRemain < MinRemainMs)
                        {
                            res.Status = "Unknown";
                            res.Detail = $"{tag} 预算用尽（装填前）";
                            return res;
                        }
                        long packSlice = Math.Min(PackSliceMs, packRemain);
                        var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), ctx.Sw,
                            (int)(ctx.Sw.ElapsedMilliseconds + packSlice));
                        ctx.PackCalls++;
                        if (packed.Ok)
                        {
                            // Packer.Spots 与输入顺序一一对应；坐标已含盘面 Min 偏移，与 JointGeometrySolver 一致
                            for (int i = 0; i < pieces.Count; i++)
                            {
                                var spot = packed.Spots[i];
                                witness.Add((pieces[i], side, spot.x, spot.y, spot.rotate));
                            }
                            continue;
                        }
                        if (packed.TimedOut)
                        {
                            // 装填超时 != 装不下：绝不生成切割、绝不当不可行
                            res.Status = "Unknown";
                            res.Detail = $"{tag} 装填超时（{(side == Side.Yang ? "阳" : "阴")}面）";
                            return res;
                        }
                        // 装填器已穷尽证明该面装不下 → 缩小成极小不可装核 → 形状切割回灌重解
                        var core = MinimalUnpackableCore(ctx, grid, pieces);
                        var counts = new Dictionary<string, int>();
                        foreach (var p in core)
                        {
                            string key = Packer.GetType(p.Shape).Key;
                            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                        }
                        roundNotes.Add($"轮{round}{(side == Side.Yang ? "阳" : "阴")}面装不下 件{pieces.Count} 面积{pieces.Sum(p2 => p2.Shape.Count)} 核[{string.Join(",", counts.Select(kv2 => kv2.Key + ":" + kv2.Value))}]");
                        failCut = new CpSatQuantity.ShapeCut
                        {
                            YinSide = side == Side.Yin,
                            Counts = counts,
                        };
                        break;
                    }
                    if (failCut == null)
                    {
                        int real = BatchesOf(witness);
                        if (real >= Math.Max(1, b))
                        {
                            res.Status = "Feasible";
                            res.Batches = real;
                            res.Items = witness;
                            res.Detail = $"{tag} 装填成功 实批{real}";
                            return res;
                        }
                        res.Status = "Unknown";
                        res.Detail = $"{tag} 装填成功但复算批次{real}<{Math.Max(1, b)}";
                        return res;
                    }
                    ctx.CutTotal++;
                    AddShapeCut(model, failCut.YinSide ? yinVar : yangVar, active, ctx.Materials, failCut.Counts);
                }
                res.Status = "Unknown";
                res.Detail = $"{tag} {maxRounds}轮切割未收敛（{string.Join("；", roundNotes)}）";
                return res;
            }
            catch (Exception ex)
            {
                res.Status = "Unknown";
                res.Detail = $"{tag} 异常：{ex.Message}";
                return res;
            }
        }

        /// <summary>真实等级/成品属性自检（数量模型约束应保证；失败按未判定而不是可行）。</summary>
        static string SelfCheck(Ctx ctx, List<(Material mat, Side side)> items)
        {
            var placements = items.Select(x => new Placement { Material = x.mat, Side = x.side }).ToList();
            var attrs = ctx.Eval.SumAttrs(placements);
            var rr = ctx.Eval.Evaluate(attrs);
            for (int i = 0; i < ctx.Runes.Count; i++)
            {
                int want = i < ctx.QualityLevel.Length ? Math.Max(0, ctx.QualityLevel[i]) : 0;
                int have = i < rr.Level.Length ? rr.Level[i] : 0;
                if (have < want)
                {
                    return $"符文{ctx.Runes[i].Id} 等级{have}<{want}";
                }
            }
            if (ctx.TargetAttr >= 0)
            {
                int got = RuneEvaluator.ItemAttrOf(attrs);
                if (got != ctx.TargetAttr)
                {
                    return $"目标属性{Attr.Names[ctx.TargetAttr]}未达成（实际{Attr.Names[Math.Max(0, got)]}）";
                }
            }
            return null;
        }

        /// <summary>
        /// 指定成品属性的归属硬约束（从 CpSatQuantity.SolveUnified 的 desiredAttr 分支移植，口径一致）。
        /// ta=0（金）：金最大或并列最大（并列归金）；其余：目标维度按权重严格/不严格领先各维度。
        /// </summary>
        static void AddTargetAttrConstraints(CpModel model, Ctx ctx, List<int> active, IntVar[] yangVar, IntVar[] yinVar)
        {
            int ta = ctx.TargetAttr;
            if (ta < 0)
            {
                return;
            }
            if (ta == 0)
            {
                var sVar = new IntVar[Attr.Count];
                var maxS = model.NewIntVar(-1000000L, 1000000L, "maxS");
                for (int d = 0; d < Attr.Count; d++)
                {
                    int wd = (d == 5 || d == 6 || d == 7) ? 10 : 1;
                    var st = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        int a = ctx.Materials[i].Attrs[d];
                        if (a == 0)
                        {
                            continue;
                        }
                        st.Add(LinearExpr.Term(yangVar[i], (long)a * wd));
                        st.Add(LinearExpr.Term(yinVar[i], -(long)a * wd));
                    }
                    sVar[d] = model.NewIntVar(-1000000L, 1000000L, $"ds{d}");
                    st.Add(LinearExpr.Term(sVar[d], -1));
                    model.Add(LinearExpr.Sum(st) == -(long)ctx.Bonus[d] * wd);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(maxS, 1), LinearExpr.Term(sVar[d], -1) }) >= 0);
                }
                var eq = new IntVar[Attr.Count];
                for (int d = 0; d < Attr.Count; d++)
                {
                    eq[d] = model.NewBoolVar($"eq{d}");
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(maxS, 1), LinearExpr.Term(sVar[d], -1), LinearExpr.Term(eq[d], 1000000L) }) <= 1000000L);
                    model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(maxS, 1), LinearExpr.Term(sVar[d], -1), LinearExpr.Term(eq[d], 1000000L) }) >= 1);
                }
                var tie2 = model.NewBoolVar("tie2");
                // 并列归金只适用于非加权维度(金水木火土)；冰风雷并列归先者，不归金
                var eqSum = new List<LinearExpr>();
                for (int d = 0; d < 5; d++)
                {
                    eqSum.Add(LinearExpr.Term(eq[d], 1));
                }
                eqSum.Add(LinearExpr.Term(tie2, -2));
                model.Add(LinearExpr.Sum(eqSum) >= 0);
                model.Add(LinearExpr.Sum(new List<LinearExpr> { LinearExpr.Term(eq[0], 1), LinearExpr.Term(tie2, 1) }) >= 1);
            }
            else
            {
                int wTa = (ta == 5 || ta == 6 || ta == 7) ? 10 : 1;
                for (int d = 0; d < Attr.Count; d++)
                {
                    if (d == ta)
                    {
                        continue;
                    }
                    int wD = (d == 5 || d == 6 || d == 7) ? 10 : 1;
                    var domTerms = new List<LinearExpr>();
                    foreach (int i in active)
                    {
                        int aTa = ctx.Materials[i].Attrs[ta] * wTa;
                        if (aTa != 0)
                        {
                            domTerms.Add(LinearExpr.Term(yangVar[i], aTa));
                            domTerms.Add(LinearExpr.Term(yinVar[i], -aTa));
                        }
                        int aD = ctx.Materials[i].Attrs[d] * wD;
                        if (aD != 0)
                        {
                            domTerms.Add(LinearExpr.Term(yangVar[i], -aD));
                            domTerms.Add(LinearExpr.Term(yinVar[i], aD));
                        }
                    }
                    long delta = 0;
                    if (ta != 0)
                    {
                        bool weightedD = d >= 5;
                        delta = (d < ta || !weightedD) ? 1L : 0L;
                    }
                    long rhsConst = delta + (long)ctx.Bonus[d] * wD - (long)ctx.Bonus[ta] * wTa;
                    model.Add(LinearExpr.Sum(domTerms) >= rhsConst);
                }
            }
        }

        /// <summary>
        /// 把已证装不下的整面材料缩小成极小不可装核心：逐件尝试去掉，去掉后仍【穷尽证明】装不下才真去掉。
        /// 装填超时的尝试保守地保留该件（不影响「核心装不下」这一不变量），因此返回的核心一定仍装不下，切割有效。
        /// </summary>
        static List<Material> MinimalUnpackableCore(Ctx ctx, Grid grid, List<Material> pieces)
        {
            var core = new List<Material>(pieces);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = core.Count - 1; i >= 0; i--)
                {
                    long remain = RemainingMs(ctx.EndMs, ctx.Sw);
                    if (remain < MinRemainMs)
                    {
                        return core;
                    }
                    var test = new List<Material>(core);
                    test.RemoveAt(i);
                    long slice = Math.Min(CoreSliceMs, Math.Max(1, remain));
                    var packed = Packer.Pack(grid, test.Select(p => p.Shape).ToList(), ctx.Sw,
                        (int)(ctx.Sw.ElapsedMilliseconds + slice));
                    ctx.PackCalls++;
                    if (packed.TimedOut)
                    {
                        continue; // 无法判定：保留该件，核心仍保持「装不下」
                    }
                    if (!packed.Ok)
                    {
                        core = test;
                        changed = true;
                    }
                }
            }
            return core;
        }

        /// <summary>形状切割：至少一个形状的件数比 cut 里少一件（与 CpSatQuantity.AddShapeCut 同口径）。</summary>
        static void AddShapeCut(CpModel model, IntVar[] sideVar, List<int> active, List<Material> materials, Dictionary<string, int> counts)
        {
            const int BIG = 10001;
            var bs = new List<IntVar>();
            int id = 0;
            foreach (var kv in counts)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }
                var terms = new List<LinearExpr>();
                foreach (int i in active)
                {
                    if (Packer.GetType(materials[i].Shape).Key == kv.Key)
                    {
                        terms.Add(LinearExpr.Term(sideVar[i], 1));
                    }
                }
                var sumExpr = LinearExpr.Sum(terms);
                var boolVar = model.NewBoolVar($"cut{id++}");
                bs.Add(boolVar);
                // boolVar=1 时该形状件数 <= kv.Value-1；boolVar=0 时约束不生效
                model.Add(sumExpr + LinearExpr.Term(boolVar, BIG) <= kv.Value - 1 + BIG);
            }
            if (bs.Count > 0)
            {
                model.Add(LinearExpr.Sum(bs) >= 1);
            }
        }

        /// <summary>按真实库存复算批次数：min_i floor(stock_i / usage_i)；无材料返回 0。</summary>
        public static int BatchesOf(List<(Material mat, Side side, int x, int y, int rot)> items)
        {
            if (items == null || items.Count == 0)
            {
                return 0;
            }
            var use = new Dictionary<Material, int>();
            foreach (var it in items)
            {
                use.TryGetValue(it.mat, out int v);
                use[it.mat] = v + 1;
            }
            long b = long.MaxValue;
            foreach (var kv in use)
            {
                if (kv.Key.Stock <= 0)
                {
                    return 0;
                }
                b = Math.Min(b, kv.Key.Stock / kv.Value);
            }
            return b == long.MaxValue ? 0 : (int)Math.Min(int.MaxValue, b);
        }
    }
}
