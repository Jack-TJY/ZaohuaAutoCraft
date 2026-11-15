using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZaohuaAutoCraft.Solver
{
    /// <summary>
    /// 覆盖门槛三态证明/求解器（离线报告方案）：对覆盖门槛的每个「最小等级向量」，
    /// 反复执行「数量模型求解 → 阴阳两面几何装填 → 装不下的形状多重集作为 ShapeCut 回灌重解」，
    /// 直到数量模型返回 Infeasible（该向量已证不可行）、装填成功（拿到可行摆法）或轮次/预算用尽。
    /// 结论三态：全部最小向量都被数量模型证 Infeasible 才返回 Infeasible；
    /// 任一向量两面都装填成功则返回 Feasible 并带回该摆法；
    /// 其余任何情况（Unknown / 超时 / 装填超时 / 组合超限 / 异常）一律返回 Unknown（绝不能当不可行）。
    ///
    /// 正确性要点：
    /// 1. 覆盖门槛可行 ⇒ 存在一个「最小等级向量」也可行：把可行解的等级逐项调低到不能再降，
    ///    等级降低只会放宽属性需求，材料多重集不变所以几何仍可装，因此检查最小向量是完备的。
    /// 2. 形状切割只在装填器【穷尽搜索后证明装不下】时生成，且用「极小不可装多重集」的件数：
    ///    可装则去掉多余件即得该核心的摆法，所以核心的所有超集都装不下，切割是有效必要条件。
    /// 3. 数量模型是真实问题的松弛（面积/库存/形状可放性/属性阈值），它 Infeasible 即真实问题
    ///    在该等级向量下无解。Unknown 绝不当作不可行。
    ///
    /// 数量模型选型说明（重要）：
    /// 固定等级向量的属性要求可精确折算成「每个属性维度一个硬下限」：
    ///   Σ(阳-阴)*attr ≥ max_r(req[r][d] * vec[r]) - bonus[d]，
    /// 因此复用 CpSatQuantity.Solve（把 rune 需求按 vec 缩放）与带 fixedLevel 的 SolveUnified
    /// 在「可行性」上是同一套约束（SolveUnified 额外多了等级变量与形状可放性上界，可行域只会更小），
    /// 对证明不可行而言，使用更宽松的 Solve 模型是保守的：它证出 Infeasible 同样意味着真实问题无解。
    /// 实测（鞋子2 真实库存 189 种、7x10 双盘、5 符文）：
    /// - SolveUnified(..., fixedLevel: vec) 每次 300~800ms，且带切割时 8 轮内不收敛（Unknown），
    ///   无法在 2500ms 预算内完成证明；
    /// - CpSatQuantity.Solve（min 件数目标）+ 极小核切割：鞋子2 [5,3,2] 稳定 6 轮、约 0.6~0.8s 证完
    ///   （两个排列向量更是在无切割的第 0 轮就直接 Infeasible）。
    /// 证明目标是「不可行」，目标函数只影响枚举顺序、不影响可行性结论，故这里用后者作为证明引擎。
    /// </summary>
    public enum GateOutcome
    {
        /// <summary>拿到真实可行的摆法（Items 带材料/阴阳面/坐标/旋转），调用方必须自行复核后再采用。</summary>
        Feasible,

        /// <summary>覆盖门槛已证不可行：不存在任何同时满足它的等级向量与材料摆法。</summary>
        Infeasible,

        /// <summary>未证明（超时 / 装填超时 / 数量模型 Unknown / 组合超限 / 异常）。绝不能当作不可行剪枝。</summary>
        Unknown
    }

    /// <summary>覆盖门槛三态结果。Feasible 时 Items 等价于 JointGeometrySolver.Candidate.Items 的摆法。</summary>
    public class GateResult
    {
        public GateOutcome Outcome = GateOutcome.Unknown;

        /// <summary>Feasible 时的摆法：材料、阴阳面、盘内坐标(x,y)、旋转。</summary>
        public List<(Material mat, Side side, int x, int y, int rot)> Items = new List<(Material, Side, int, int, int)>();

        /// <summary>Feasible 时命中的最小等级向量（实际等级请由调用方按摆法结算，只会不低于它）。</summary>
        public int[] Level = new int[0];

        /// <summary>诊断文本（结论 / 枚举数 / 放弃原因 / 用时），供日志输出。</summary>
        public string Detail = "";

        /// <summary>耗时（毫秒）。</summary>
        public long ElapsedMs;
    }

    public static class CoverageGateProver
    {
        /// <summary>枚举组合数上限：prod(MaxStacks+1) 超过该值立即放弃（未证明）。</summary>
        public const int MaxEnumCombos = 4096;

        /// <summary>每个最小等级向量最多重解轮数（每轮排除一个装不下的形状多重集）。</summary>
        public const int MaxCutRounds = 8;

        /// <summary>单次数量模型求解的时间分片上限（毫秒）。</summary>
        public const long SolveSliceMs = 800L;

        /// <summary>单面装填 / 缩小不可装核心的时间分片上限（毫秒）。</summary>
        public const long PackSliceMs = 300L;

        /// <summary>剩余预算低于该值立即放弃（未证明）。</summary>
        public const long MinRemainMs = 150L;

        /// <summary>
        /// 三态求解覆盖门槛：Feasible = 拿到满足门槛的真实摆法；Infeasible = 已证不存在；
        /// Unknown = 未证明（绝不能当不可行剪枝）。
        /// </summary>
        /// <param name="materials">炼器材料（含库存、形状、属性）。</param>
        /// <param name="runes">已选符文（用 MaxStacks 枚举等级上限）。</param>
        /// <param name="bonus">锻造锤/天赋的固定属性加成。</param>
        /// <param name="yang">阳盘。</param>
        /// <param name="yin">阴盘。</param>
        /// <param name="priorityGroup">与 runes 对齐的优先级组号（不影响可行性结论，保留签名与主流程一致）。</param>
        /// <param name="desiredAttr">指定成品属性维度，-1 表示不指定。
        /// 求解器故意不建模该约束（它是额外限制，去掉只会让可行解更多、但结论仍然可靠）；调用方复核时会检查它。</param>
        /// <param name="policy">材料策略（求解引擎用 min 件数目标，方向不影响可行性结论，保留签名与主流程一致）。</param>
        /// <param name="cov">覆盖门槛：第 l 层（1-based）至少 cov[l-1] 个符文等级 ≥ l。</param>
        /// <param name="budgetMs">整个求解器共享的时间预算（毫秒）。</param>
        public static GateResult TryProve(
            List<Material> materials, List<Rune> runes, int[] bonus, Grid yang, Grid yin,
            int[] priorityGroup, int desiredAttr, MaterialPolicy policy,
            int[] cov, long budgetMs)
        {
            var res = new GateResult();
            var sw = Stopwatch.StartNew();
            // Unknown 的统一出口：只填诊断与耗时，绝不当作不可行。
            GateResult Unproven(string detail)
            {
                res.Detail = detail;
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }
            try
            {
                if (materials == null || runes == null || runes.Count == 0 || cov == null || cov.Length == 0)
                {
                    return Unproven("输入为空（未证明）");
                }
                if (bonus == null || bonus.Length != Attr.Count)
                {
                    bonus = new int[Attr.Count];
                }
                if (budgetMs < MinRemainMs)
                {
                    return Unproven($"预算不足({budgetMs}ms)（未证明）");
                }
                long endMs = sw.ElapsedMilliseconds + budgetMs;

                // ── 步骤 1：枚举覆盖门槛的全部最小等级向量 ──
                bool overflow;
                List<int[]> vectors = EnumerateMinimalVectors(runes, cov, out overflow);
                if (overflow)
                {
                    return Unproven($"组合数超上限{MaxEnumCombos}（未证明）");
                }
                if (vectors.Count == 0)
                {
                    // 覆盖门槛本身就不可能有任何等级向量满足（如某层要求人数超过能到该层的符文数）。
                    // 「覆盖门槛可行 ⇒ 存在最小向量可行」的逆否即：无最小向量 ⇒ 门槛不可行，这是精确结论。
                    res.Outcome = GateOutcome.Infeasible;
                    res.Detail = "无任何等级向量可满足该覆盖门槛";
                    res.ElapsedMs = sw.ElapsedMilliseconds;
                    return res;
                }

                // ── 步骤 2：逐个最小向量做「数量模型 → 装填检查 → 形状切割 → 重解」循环 ──
                int solveCalls = 0;
                int packCalls = 0;
                int cutTotal = 0;
                foreach (int[] vec in vectors)
                {
                    if (RemainingMs(endMs, sw) < MinRemainMs)
                    {
                        return Unproven($"预算用尽（已枚举{vectors.Count}个，未证明）");
                    }
                    // 固定等级向量 → 按符文需求折算成每维属性硬下限（等级 0 的符文不产生约束）
                    var levelRunes = ScaleRunes(runes, vec);
                    var cuts = new List<CpSatQuantity.ShapeCut>();
                    bool proven = false;
                    for (int round = 0; round < MaxCutRounds; round++)
                    {
                        long remain = RemainingMs(endMs, sw);
                        if (remain < MinRemainMs)
                        {
                            return Unproven($"预算用尽（等级向量[{string.Join(",", vec)}]未证明）");
                        }
                        // (a) 数量模型：固定等级向量（已折算为属性下限），带上已排除的形状切割
                        long sliceMs = Math.Min(SolveSliceMs, remain);
                        CpSatQuantity.LastStatus = "";
                        var items = CpSatQuantity.Solve(
                            materials, levelRunes, bonus, yang, yin, sliceMs / 1000.0, cuts, true);
                        solveCalls++;
                        string status = CpSatQuantity.LastStatus;
                        // (b) Infeasible：该最小向量整体不可行，换下一个向量
                        if (status == "Infeasible")
                        {
                            proven = true;
                            break;
                        }
                        // Unknown / 超时 / 无解信息不全：未证明，立即放弃
                        if (items == null || items.Count == 0)
                        {
                            return Unproven($"数量模型{(string.IsNullOrEmpty(status) ? "Unknown" : status)}（等级向量[{string.Join(",", vec)}]，未证明）");
                        }
                        // (c) 几何检查：阴阳两面分别真实装填；两面都成功则收集成可行摆法
                        CpSatQuantity.ShapeCut failCut = null;
                        var witness = new List<(Material mat, Side side, int x, int y, int rot)>();
                        foreach (Side side in new[] { Side.Yang, Side.Yin })
                        {
                            var grid = side == Side.Yang ? yang : yin;
                            var pieces = items.Where(x => x.side == side).Select(x => x.mat).ToList();
                            if (pieces.Count == 0)
                            {
                                continue;
                            }
                            if (grid == null || grid.Area <= 0)
                            {
                                return Unproven("盘面缺失或为空（未证明）");
                            }
                            long packRemain = RemainingMs(endMs, sw);
                            if (packRemain < MinRemainMs)
                            {
                                return Unproven("预算用尽（装填前，未证明）");
                            }
                            long packSlice = Math.Min(PackSliceMs, packRemain);
                            var packed = Packer.Pack(grid, pieces.Select(p => p.Shape).ToList(), sw,
                                (int)(sw.ElapsedMilliseconds + packSlice));
                            packCalls++;
                            if (packed.Ok)
                            {
                                // 装填成功：按输入顺序把每块的摆法回填给对应材料（Packer.Spots 与输入对齐）
                                for (int i = 0; i < pieces.Count; i++)
                                {
                                    var spot = packed.Spots[i];
                                    witness.Add((pieces[i], side, spot.x, spot.y, spot.rotate));
                                }
                                continue;
                            }
                            if (packed.TimedOut)
                            {
                                // 装填超时 != 装不下，未证明
                                return Unproven($"装填超时（等级向量[{string.Join(",", vec)}]，未证明）");
                            }
                            // 该面已穷尽证明装不下：缩小成极小不可装多重集后生成切割。
                            // 极小核心的所有超集都装不下（能装则去掉多余件即得核心摆法），切割更强且仍然有效。
                            var core = MinimalUnpackableCore(grid, pieces, endMs, sw, ref packCalls);
                            var counts = new Dictionary<string, int>();
                            foreach (var p in core)
                            {
                                string key = Packer.GetType(p.Shape).Key;
                                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                            }
                            failCut = new CpSatQuantity.ShapeCut { YinSide = side == Side.Yin, Counts = counts };
                            break;
                        }
                        if (failCut == null)
                        {
                            // 两面都装得下 ⇒ 找到一个满足覆盖门槛的真实方案 ⇒ Feasible，带回摆法供调用方复核
                            res.Outcome = GateOutcome.Feasible;
                            res.Items = witness;
                            res.Level = (int[])vec.Clone();
                            res.Detail = $"等级向量[{string.Join(",", vec)}]装填成功（门槛可行，件{witness.Count}）";
                            res.ElapsedMs = sw.ElapsedMilliseconds;
                            return res;
                        }
                        cuts.Add(failCut);
                        cutTotal++;
                    }
                    // (d) 8 轮用尽仍非 Infeasible：未证明（按三态约定整体返回 Unknown，绝不当作不可行剪枝）
                    if (!proven)
                    {
                        return Unproven($"等级向量[{string.Join(",", vec)}] {MaxCutRounds}轮未证不可行（未证明）");
                    }
                }

                res.Outcome = GateOutcome.Infeasible;
                res.Detail = $"最小向量{vectors.Count}个全部Infeasible（重解{solveCalls}次/装填{packCalls}次/切割{cutTotal}次） 用时{sw.ElapsedMilliseconds}ms";
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }
            catch (Exception ex)
            {
                // 求解器任何异常都按「未证明」处理，保证不影响主流程正确性
                res.Outcome = GateOutcome.Unknown;
                res.Detail = "异常（未证明）：" + ex.Message;
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }
        }

        static long RemainingMs(long endMs, Stopwatch sw)
        {
            return endMs - sw.ElapsedMilliseconds;
        }

        /// <summary>把等级向量折叠进符文需求：等级 0 的符文整体清零（不产生属性要求）。</summary>
        static List<Rune> ScaleRunes(List<Rune> runes, int[] vec)
        {
            var list = new List<Rune>(runes.Count);
            for (int i = 0; i < runes.Count; i++)
            {
                var r = runes[i];
                int lv = i < vec.Length ? Math.Max(0, vec[i]) : 0;
                var req = new int[Attr.Count];
                for (int d = 0; d < Attr.Count; d++)
                {
                    req[d] = r.Requirement[d] * lv;
                }
                list.Add(new Rune
                {
                    Id = r.Id,
                    Name = r.Name,
                    Requirement = req,
                    MaxStacks = Math.Max(1, r.MaxStacks),
                    Weight = r.Weight,
                    DefaultWeight = r.DefaultWeight
                });
            }
            return list;
        }

        /// <summary>
        /// 把已证装不下的整面材料缩小成极小不可装核心：逐件尝试去掉，去掉后仍【穷尽证明】装不下才真去掉。
        /// 装填超时的尝试保守地保留该件（不影响「核心装不下」这一不变量），
        /// 因此返回的核心一定仍装不下，切割有效。整体预算不足时直接返回当前核心。
        /// </summary>
        static List<Material> MinimalUnpackableCore(Grid grid, List<Material> pieces, long endMs, Stopwatch sw, ref int packCalls)
        {
            var core = new List<Material>(pieces);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = core.Count - 1; i >= 0; i--)
                {
                    if (RemainingMs(endMs, sw) < MinRemainMs)
                    {
                        return core;
                    }
                    var test = new List<Material>(core);
                    test.RemoveAt(i);
                    var packed = Packer.Pack(grid, test.Select(p => p.Shape).ToList(), sw,
                        (int)(sw.ElapsedMilliseconds + Math.Min(PackSliceMs, Math.Max(1, RemainingMs(endMs, sw)))));
                    packCalls++;
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

        /// <summary>
        /// 枚举覆盖门槛的最小等级向量。
        /// 最小向量与「嵌套集合」一一对应：S_1 ⊇ S_2 ⊇ … ⊇ S_L，其中 S_l = {符文 : 等级 ≥ l}，
        /// |S_l| 必须正好等于 cov[l-1]。从最高层往下逐层挑人即可**直接构造**，无需遍历全部组合。
        /// 实测：鞋子3（上限[1,4,3,3,3,3,1,3]、cov[8,6,5,1]）全部组合 20480，但最小向量只有 5 个；
        /// 鞋子2（[1,1,3,3,3]、cov[5,3,2]）全部组合 256，最小向量 3 个。
        /// </summary>
        /// <param name="overflow">最小向量个数超上限时为 true（此时返回空列表且不可当「无向量」用）。</param>
        public static List<int[]> EnumerateMinimalVectors(List<Rune> runes, int[] cov, out bool overflow)
        {
            overflow = false;
            var result = new List<int[]>();
            if (runes == null || runes.Count == 0 || cov == null || cov.Length == 0)
            {
                return result;
            }
            int n = runes.Count;
            int maxL = cov.Length;
            for (int i = 0; i < n; i++)
            {
                maxL = Math.Max(maxL, Math.Max(0, runes[i].MaxStacks));
            }
            var need = new int[maxL];
            for (int l = 1; l <= maxL; l++)
            {
                need[l - 1] = l <= cov.Length ? Math.Max(0, cov[l - 1]) : 0;
            }
            // 每层能达到的符文数；要求超过可达数则无向量（该门槛必不可能）
            var cap = new int[maxL];
            for (int l = 1; l <= maxL; l++)
            {
                int c = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Math.Max(0, runes[i].MaxStacks) >= l)
                    {
                        c++;
                    }
                }
                cap[l - 1] = c;
            }
            for (int l = 1; l <= maxL; l++)
            {
                if (need[l - 1] > cap[l - 1])
                {
                    return result;
                }
            }
            var sets = new List<int>[maxL]; // sets[l-1] = S_l
            var vec = new int[n];
            void Emit()
            {
                Array.Clear(vec, 0, n);
                for (int l = 1; l <= maxL; l++)
                {
                    foreach (int i in sets[l - 1])
                    {
                        if (vec[i] < l)
                        {
                            vec[i] = l;
                        }
                    }
                }
                result.Add((int[])vec.Clone());
            }
            // 处理第 l 层（从 maxL 往 1）；返回 false = 已达上限需中止
            bool Build(int l)
            {
                if (l == 0)
                {
                    Emit();
                    return result.Count <= MaxEnumCombos;
                }
                // 上层 S_{l+1} 必须整体包含在本层里
                var upper = l < maxL ? sets[l] : new List<int>();
                int add = need[l - 1] - upper.Count;
                if (add < 0)
                {
                    return true; // 上层人数已超本层要求（need 非增时不会发生）
                }
                var pool = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (Math.Max(0, runes[i].MaxStacks) < l || upper.Contains(i))
                    {
                        continue;
                    }
                    pool.Add(i);
                }
                if (add > pool.Count)
                {
                    return true; // 够不到本层要求的人数
                }
                if (add == 0)
                {
                    sets[l - 1] = new List<int>(upper);
                    return Build(l - 1);
                }
                var pick = new int[add];
                bool Choose(int start, int chosen)
                {
                    if (chosen == add)
                    {
                        var s = new List<int>(upper);
                        for (int z = 0; z < add; z++)
                        {
                            s.Add(pool[pick[z]]);
                        }
                        sets[l - 1] = s;
                        return Build(l - 1);
                    }
                    for (int z = start; z < pool.Count; z++)
                    {
                        pick[chosen] = z;
                        if (!Choose(z + 1, chosen + 1))
                        {
                            return false;
                        }
                    }
                    return true;
                }
                return Choose(0, 0);
            }
            Build(maxL);
            if (result.Count > MaxEnumCombos)
            {
                overflow = true;
                result.Clear();
            }
            return result;
        }

        /// <summary>覆盖门槛是否被等级向量满足。</summary>
        static bool SatisfiesCoverage(int[] vec, int[] need)
        {
            for (int l = 1; l <= need.Length; l++)
            {
                int want = need[l - 1];
                if (want <= 0)
                {
                    continue;
                }
                int have = 0;
                for (int i = 0; i < vec.Length; i++)
                {
                    if (vec[i] >= l)
                    {
                        have++;
                    }
                }
                if (have < want)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>最小性：每个正分量再降 1 都会破坏覆盖门槛。</summary>
        static bool IsMinimal(int[] vec, int[] need)
        {
            for (int i = 0; i < vec.Length; i++)
            {
                if (vec[i] <= 0)
                {
                    continue;
                }
                int old = vec[i];
                vec[i] = old - 1;
                bool still = SatisfiesCoverage(vec, need);
                vec[i] = old;
                if (still)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
