using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft
{
    /// <summary>炼器自动摆放窗口（IMGUI，不改游戏预制体）。</summary>
    internal class EquipmentWindow
    {
        const int WindowId = 0x5A42;

        Rect rect = new Rect(520, 60, 560, 700);

        bool visible;

        /// <summary>是否允许「部分达成备选」（放弃部分符文）。</summary>
        bool allowPartial;

        float nextRuneCheck;

        Vector2 runeScroll, resultScroll;

        List<Rune> runes = new List<Rune>();

        bool[] priority = new bool[0];

        /// <summary>优先符文的排序（符文下标，越前优先级越高）。报告 P1-2。</summary>
        List<int> prioOrder = new List<int>();

        int selectedType;

        string furnaceText = "";

        string status = "";

        EquipmentRequest request;

        Task<EquipmentSolveResult> running;

        /// <summary>设置变更计数：变更后旧任务结果作废（报告 P1-5）。</summary>
        int calcVersion;

        int runningVersion;

        EquipmentSolveResult result;

        int chosen;

        EquipmentSolution applied;

        int verifyFrames;

        Font font;

        internal static bool MouseOver;

        public void Update()
        {
            bool open = Game.EquipmentBridge.IsOpen;
            if (!open)
            {
                if (visible)
                {
                    Hide();
                }
                MouseOver = false;
                return;
            }
            if (Plugin.ToggleKey.Value.IsDown())
            {
                if (visible)
                {
                    Hide();
                }
                else
                {
                    Show();
                }
            }
            if (running != null && running.IsCompleted)
            {
                OnSolved();
            }
            if (verifyFrames > 0 && --verifyFrames == 0)
            {
                Verify();
            }
            // 面板打开时定时检查符文是否变化，变了就自动刷新（无需开关面板）
            if (visible && UnityEngine.Time.unscaledTime >= nextRuneCheck)
            {
                nextRuneCheck = UnityEngine.Time.unscaledTime + 0.5f;
                if (RunesChanged())
                {
                    RefreshFurnace();
                    result = null;
                    calcVersion++;
                    chosen = 0;
                    applied = null;
                    status = "";
                }
            }
            var mouse = Input.mousePosition;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            MouseOver = pressing || ButtonRect().Contains(gui) || (visible && rect.Contains(gui));
        }

        void Show()
        {
            visible = true;
            try
            {
                RefreshFurnace();
            }
            catch (Exception e)
            {
                status = "读取炼器信息失败：" + e.Message;
                Plugin.Log.LogError(e);
            }
        }

        void Hide()
        {
            visible = false;
            MouseOver = false;
        }

        /// <summary>当前已选符文与缓存是否不同（按 ID 集合比较）。</summary>
        bool RunesChanged()
        {
            try
            {
                var cur = Game.EquipmentBridge.GetSelectedRunes();
                if (cur == null)
                {
                    return runes.Count != 0;
                }
                if (cur.Count != runes.Count)
                {
                    return true;
                }
                var a = cur.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var b = runes.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                return !a.SequenceEqual(b);
            }
            catch
            {
                return false;
            }
        }

        void RefreshFurnace()
        {
            var f = Game.EquipmentBridge.GetFurnaceCfg();
            if (f == null)
            {
                furnaceText = "未装备锻造锤";
                return;
            }
            var bonus = Game.EquipmentBridge.GetFixedBonus(f);
            string name = Singleton<TbDataImpl>.Instance.GetItemCfg(f.id)?.GetName ?? f.id;
            furnaceText = $"锻造锤：{name}  阳 {f.yangGridSize.x}×{f.yangGridSize.y}  阴 {f.yinGridSize.x}×{f.yinGridSize.y}  属性加成：{Game.DrugBridge.AttrText(bonus)}";
            runes = Game.EquipmentBridge.GetSelectedRunes();
            selectedType = Game.EquipmentBridge.GetSelectedType();
            if (priority.Length != runes.Count)
            {
                priority = new bool[runes.Count];
            }
        }

        public void OnGUI()
        {
            if (!Game.EquipmentBridge.IsOpen)
            {
                return;
            }
            if (font == null)
            {
                font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun" }, 14);
            }
            var oldFont = GUI.skin.font;
            GUI.skin.font = font;
            try
            {
                DrawToggleButton();
                if (visible)
                {
                    rect = GUI.Window(WindowId, rect, DrawWindow, "自动炼器");
                }
            }
            finally
            {
                GUI.skin.font = oldFont;
            }
        }

        bool pressing, dragged;

        Vector2 pressMouse, pressPos, dragPos;

        Rect ButtonRect()
        {
            float x = dragged ? dragPos.x : Plugin.ButtonX.Value < 0 ? Screen.width - 190 : Plugin.ButtonX.Value;
            float y = dragged ? dragPos.y : Plugin.ButtonY.Value < 0 ? 90 : Plugin.ButtonY.Value;
            return new Rect(Mathf.Clamp(x, 0, Screen.width - 170), Mathf.Clamp(y, 0, Screen.height - 30), 170, 30);
        }

        void DrawToggleButton()
        {
            var r = ButtonRect();
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                pressing = true;
                dragged = false;
                pressMouse = e.mousePosition;
                pressPos = r.position;
                e.Use();
            }
            else if (pressing && e.type == EventType.MouseDrag)
            {
                if ((e.mousePosition - pressMouse).sqrMagnitude > 16)
                {
                    dragged = true;
                }
                if (dragged)
                {
                    dragPos = pressPos + e.mousePosition - pressMouse;
                }
                e.Use();
            }
            else if (pressing && e.type == EventType.MouseUp)
            {
                pressing = false;
                if (dragged)
                {
                    var p = ButtonRect().position;
                    dragged = false;
                    Plugin.ButtonX.Value = Mathf.RoundToInt(p.x);
                    Plugin.ButtonY.Value = Mathf.RoundToInt(p.y);
                }
                else
                {
                    if (visible)
                    {
                        Hide();
                    }
                    else
                    {
                        Show();
                    }
                }
                e.Use();
            }
            GUI.Button(ButtonRect(), (visible ? "关闭自动摆放" : "自动摆放") + $" ({Plugin.ToggleKey.Value})");
        }

        // 满足最多符文是所有模式的内在前提，不再作为独立模式展示
        static readonly string[] ModeNames = { "指定符文优先", "最少材料", "最低品阶", "最多炼制次数", "最大属性" };

        static readonly EquipmentMode[] ModeValues = { EquipmentMode.MaxRuneLevel, EquipmentMode.MinPieces, EquipmentMode.LowGrade, EquipmentMode.MaxBatches, EquipmentMode.MaxAttr };

        void DrawWindow(int id)
        {
            GUILayout.Label(furnaceText);
            GUILayout.Label($"已选符文 {runes.Count} 个（法器类型 {selectedType}）：");
            runeScroll = GUILayout.BeginScrollView(runeScroll, GUILayout.Height(120));
            if (priority.Length != runes.Count)
            {
                priority = new bool[runes.Count];
            }
            GUILayout.Label("符文优先级（勾选=优先组，可多选、可调顺序；未勾选=较低优先组）：");
            for (int i = 0; i < runes.Count; i++)
            {
                var r = runes[i];
                GUILayout.BeginHorizontal();
                bool oldPri = priority[i];
                priority[i] = GUILayout.Toggle(priority[i], "优先", GUILayout.Width(56));
                if (priority[i] != oldPri)
                {
                    if (priority[i])
                    {
                        if (!prioOrder.Contains(i))
                        {
                            prioOrder.Add(i);
                        }
                    }
                    else
                    {
                        prioOrder.Remove(i);
                    }
                    result = null;
                    calcVersion++;
                    chosen = 0;
                    applied = null;
                    status = "";
                }
                GUILayout.Label($"{r.Name}  要求 {Game.DrugBridge.AttrText(r.Requirement)}  满级 {r.MaxStacks}");
                GUILayout.EndHorizontal();
            }
            // 优先顺序（上移/下移）：报告 P1-2
            if (prioOrder.Count > 1)
            {
                GUILayout.Label("优先顺序（越前优先级越高）：");
                for (int k = 0; k < prioOrder.Count; k++)
                {
                    int ri = prioOrder[k];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{k + 1}. {runes[ri].Name}", GUILayout.Width(150));
                    if (k > 0 && GUILayout.Button("▲ 上移", GUILayout.Width(60)))
                    {
                        int tmp = prioOrder[k - 1];
                        prioOrder[k - 1] = prioOrder[k];
                        prioOrder[k] = tmp;
                        result = null;
                        calcVersion++;
                        chosen = 0;
                        applied = null;
                    }
                    if (k < prioOrder.Count - 1 && GUILayout.Button("▼ 下移", GUILayout.Width(60)))
                    {
                        int tmp = prioOrder[k + 1];
                        prioOrder[k + 1] = prioOrder[k];
                        prioOrder[k] = tmp;
                        result = null;
                        calcVersion++;
                        chosen = 0;
                        applied = null;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Label("法宝属性（不指定 = 无属性约束）：");
            int attrIdx = Mathf.Clamp(Plugin.EquipmentTargetAttr.Value, -1, Attr.Names.Length - 1);
            var attrNames = new List<string> { "不指定" };
            attrNames.AddRange(Attr.Names);
            int nAttr = GUILayout.SelectionGrid(attrIdx + 1, attrNames.ToArray(), 5);
            if (nAttr - 1 != attrIdx)
            {
                Plugin.EquipmentTargetAttr.Value = nAttr - 1;
                result = null;
                calcVersion++;
                chosen = 0;
                applied = null;
                status = "";
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("材料策略", GUILayout.Width(64));
            int pol = (int)Plugin.EquipmentMaterialPolicy.Value;
            int nPol = GUILayout.SelectionGrid(pol, new[] { "最多炼制次数", "低阶材料优先" }, 2);
            if (nPol != pol)
            {
                Plugin.EquipmentMaterialPolicy.Value = (MaterialPolicy)nPol;
                result = null;
                calcVersion++;
                chosen = 0;
                applied = null;
                status = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("最长计算时间（秒）：");
            int sec = Mathf.Max(1, Plugin.EquipmentTimeLimitMs.Value / 1000);
            GUILayout.BeginHorizontal();
            GUI.enabled = running == null;
            if (GUILayout.Button("−", GUILayout.Width(28)) && sec > 1)
            {
                Plugin.EquipmentTimeLimitMs.Value = (sec - 1) * 1000;
            }
            GUILayout.Label(sec.ToString(), GUILayout.Width(30));
            if (GUILayout.Button("+", GUILayout.Width(28)) && sec < 120)
            {
                Plugin.EquipmentTimeLimitMs.Value = (sec + 1) * 1000;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (runes.Count > 0)
            {
                GUI.enabled = running == null;
                if (GUILayout.Button(running == null ? "计算摆法" : "计算中…", GUILayout.Height(28)))
                {
                    StartSolve();
                    GUIUtility.ExitGUI();
                }
                GUI.enabled = true;
            }
            GUILayout.Label(status + (result?.Status != null ? $"  [{result.Status}]" : ""));

            if (result != null && result.Best == null && !allowPartial && Plugin.EquipmentTargetAttr.Value >= 0)
            {
                if (GUILayout.Button("查看部分达成备选（放弃部分符文）", GUILayout.Height(26)))
                {
                    allowPartial = true;
                    StartSolve();
                    GUIUtility.ExitGUI();
                }
            }

            if (result?.Best != null)
            {
                for (int i = 0; i < result.Solutions.Count; i++)
                {
                    var s = result.Solutions[i];
                    string head = $"方案{i + 1}：{s.PieceCount}件 生效符文{s.Runes.ActiveCount}/{runes.Count} 总等级{s.Runes.TotalLevel} 品阶分{s.Runes.GradeScore} 可炼{s.Batches}次";
                    if (GUILayout.Toggle(chosen == i, head) && chosen != i)
                    {
                        chosen = i;
                        GUIUtility.ExitGUI();
                    }
                }
                var sol = result.Solutions[Mathf.Clamp(chosen, 0, result.Solutions.Count - 1)];
                resultScroll = GUILayout.BeginScrollView(resultScroll, GUILayout.Height(130));
                foreach (var g in sol.Placements.GroupBy(p => (p.Side, p.Material)).OrderBy(g => g.Key.Side))
                {
                    GUILayout.Label($"{(g.Key.Side == Side.Yang ? "阳" : "阴")}  {g.Key.Material.Name} ×{g.Count()}    （{Game.DrugBridge.AttrText(g.Key.Material.Attrs)}）");
                }
                GUILayout.EndScrollView();
                if (GUILayout.Button("摆上", GUILayout.Height(30)))
                {
                    try
                    {
                        applied = sol;
                        Game.EquipmentBridge.Apply(sol);
                        status = "已摆放，正在核对游戏结算…";
                        verifyFrames = 2;
                    }
                    catch (Exception e)
                    {
                        status = "摆放失败：" + e.Message;
                        Plugin.Log.LogError(e);
                    }
                }
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        void StartSolve()
        {
            try
            {
                var furnace = Game.EquipmentBridge.GetFurnaceCfg();
                if (furnace == null)
                {
                    status = "请先装备锻造锤";
                    return;
                }
                var bonus = Game.EquipmentBridge.GetFixedBonus(furnace);
                runes = Game.EquipmentBridge.GetSelectedRunes();
                if (runes.Count == 0)
                {
                    status = "请先在炼器界面勾选符文";
                    return;
                }
                var pg = new int[runes.Count];
                // 按 prioOrder 顺序分配严格优先级组 0,1,2,…；未优先符文排在最后一组
                int lowGroup = Math.Max(0, prioOrder.Count);
                for (int i = 0; i < runes.Count; i++)
                {
                    int idx = prioOrder.IndexOf(i);
                    pg[i] = idx >= 0 ? idx : lowGroup;
                }
                request = new EquipmentRequest
                {
                    Materials = Game.EquipmentBridge.GetMaterials(),
                    Runes = runes,
                    Bonus = bonus,
                    Yang = new Grid(furnace.yangGridSize.x, furnace.yangGridSize.y),
                    Yin = new Grid(furnace.yinGridSize.x, furnace.yinGridSize.y),
                    Unified = true,
                    TimeLimitMs = Plugin.EquipmentTimeLimitMs.Value,
                    TargetAttr = Plugin.EquipmentTargetAttr.Value,
                    PriorityGroup = pg,
                    MaterialPolicy = Plugin.EquipmentMaterialPolicy.Value,
                    AllowPartial = allowPartial,
                    TopK = 3,
                };
                result = null;
                chosen = 0;
                status = $"计算中…约 {request.TimeLimitMs / 1000.0:0.#} 秒（符文 {runes.Count} 个，材料 {request.Materials.Count} 种）";
                var req = request;
                runningVersion = ++calcVersion;
                Plugin.Log.LogInfo($"[炼器] 开始计算: 符文{runes.Count} 材料{req.Materials.Count} 目标属性{req.TargetAttr} 策略{req.MaterialPolicy} 时限{req.TimeLimitMs}ms 线程{CpSatQuantity.Workers} 阳盘{req.Yang?.Width}x{req.Yang?.Height} 阴盘{req.Yin?.Width}x{req.Yin?.Height} 上限[{string.Join(",", runes.ConvertAll(x => x.MaxStacks))}] 需求[{string.Join(";", runes.ConvertAll(x => Game.DrugBridge.AttrText(x.Requirement)))}]");
                running = Task.Run(() => new EquipmentSolver(req).Solve());
            }
            catch (Exception e)
            {
                status = "读取失败：" + e.Message;
                Plugin.Log.LogError(e);
            }
        }

        void OnSolved()
        {
            var task = running;
            running = null;
            if (runningVersion != calcVersion)
            {
                // 计算期间设置已变更，丢弃过期结果（报告 P1-5）
                result = null;
                calcVersion++;
                chosen = 0;
                applied = null;
                status = "设置已变更，旧计算结果已作废，请重新计算";
                return;
            }
            if (task.IsFaulted)
            {
                status = "计算出错：" + task.Exception?.GetBaseException().Message;
                Plugin.Log.LogError(task.Exception);
                return;
            }
            result = task.Result;
            Plugin.Log.LogInfo($"[炼器] 结果: Status={result.Status} Elapsed={result.ElapsedMs}ms 解={result.Solutions?.Count ?? 0} | CP-SAT: {CpSatQuantity.LastLog}");
            if (result.Best == null)
            {
                status = $"{result.Message}（{result.ElapsedMs} ms）";
                return;
            }
            foreach (var s in result.Solutions)
            {
                string err = EquipmentSolver.Verify(request, s);
                if (err != null)
                {
                    status = "内部复核失败：" + err;
                    Plugin.Log.LogError(status);
                    result.Solutions.Clear();
                    return;
                }
            }
            status = $"找到 {result.Solutions.Count} 个方案（{result.ElapsedMs} ms）" + (result.TimedOut ? "，已到时间上限，未必最优" : "") + "。选一个后点「摆上」。";
        }

        void Verify()
        {
            try
            {
                var (placed, runeDic, attr) = Game.EquipmentBridge.ReadGameResult();
                int active = runeDic?.Count ?? 0;
                if (applied != null && active == applied.Runes.ActiveCount)
                {
                    bool same = CheckLevels(runeDic);
                    // 内部 0 按用户确认为默认金（不再显示「无」）
                    string attrName = attr >= 1 && attr <= 8 ? Attr.Names[attr - 1] : Attr.Names[0];
                    status = $"✓ 游戏已识别，生效符文 {active} 个，法器属性 {attrName}。请点游戏里的「炼制」。";
                    if (request?.Mode == EquipmentMode.MaxAttr)
                    {
                        int actual = attr >= 1 && attr <= 8 ? attr - 1 : 0;
                        int ta = Math.Max(0, request.TargetAttr);
                        if (actual != ta)
                        {
                            status += $"\n（注意：游戏实际归属 {Attr.Names[actual]}，与目标 {Attr.Names[ta]} 不一致，可能材料不足或炉子/天赋加成影响）";
                        }
                    }
                    if (!same)
                    {
                        status += "\n（预测的符文等级和游戏不一致，以游戏为准；请把日志发给作者）";
                        Plugin.Log.LogWarning("符文等级预测不一致：" + string.Join(" ", request.Runes.Select(r => r.Id)) + "；摆法 " + string.Join(" ", applied.Placements.Select(p => $"{p.Material.Id}@{(int)p.Side}:{p.X},{p.Y},{p.Rotate}")));
                    }
                }
                else
                {
                    status = $"✗ 游戏结算不一致：摆上 {placed} 件，生效符文 {active} 个。请把日志发给作者。";
                    Plugin.Log.LogWarning(status);
                }
            }
            catch (Exception e)
            {
                status = "核对失败：" + e.Message;
                Plugin.Log.LogError(e);
            }
        }

        bool CheckLevels(Dictionary<string, int> runeDic)
        {
            if (applied?.Runes == null || request?.Runes == null)
            {
                return true;
            }
            for (int i = 0; i < request.Runes.Count; i++)
            {
                int want = applied.Runes.Level[i];
                int got = runeDic != null && runeDic.TryGetValue(request.Runes[i].Id, out int lv) ? lv : 0;
                if (want != got)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
