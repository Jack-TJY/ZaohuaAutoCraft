using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZaohuaAutoCraft.Solver;

namespace ZaohuaAutoCraft
{
    /// <summary>炼丹自动摆放窗口（IMGUI，不改游戏预制体）。</summary>
    internal class DrugWindow
    {
        const int WindowId = 0x5A41;

        Rect rect = new Rect(40, 60, 500, 700);

        bool visible;

        string search = "";

        Vector2 listScroll, resultScroll;

        List<Game.DrugBridge.RecipeInfo> recipes = new List<Game.DrugBridge.RecipeInfo>();

        Game.DrugBridge.RecipeInfo selected;

        string furnaceText = "";

        string status = "";

        DrugRequest request;

        Task<SolveResult> running;

        SolveResult result;

        int verifyFrames;

        Font font;

        internal static bool MouseOver;

        public void Update()
        {
            bool open = Game.DrugBridge.IsOpen;
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
            // 鼠标在窗口上时让游戏 UI 的射线检测落空，避免点穿到下面的背包格子（见 GraphicRaycasterPatch）。
            // 不能直接关 EventSystem：游戏每帧都在用 EventSystem.current，关掉会刷空引用报错。
            var mouse = Input.mousePosition;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            MouseOver = pressing || ButtonRect().Contains(gui) || (visible && rect.Contains(gui));
        }

        void Show()
        {
            visible = true;
            try
            {
                recipes = Game.DrugBridge.GetRecipes();
                RefreshFurnace();
            }
            catch (Exception e)
            {
                status = "读取丹方失败：" + e.Message;
                Plugin.Log.LogError(e);
            }
        }

        void Hide()
        {
            visible = false;
            MouseOver = false;
        }

        void RefreshFurnace()
        {
            var f = Game.DrugBridge.GetFurnaceCfg();
            if (f == null)
            {
                furnaceText = "未装备丹炉";
                return;
            }
            var bonus = Game.DrugBridge.GetFixedBonus(f);
            string extra = (bonus.Count != 0 ? $"  丹数+{bonus.Count}" : "") + (bonus.Quality != 0 ? $"  品质+{bonus.Quality}" : "");
            string name = Singleton<TbDataImpl>.Instance.GetItemCfg(f.id)?.GetName ?? f.id;
            furnaceText = $"丹炉：{name}  阳 {f.yangGridSize.x}×{f.yangGridSize.y}  阴 {f.yinGridSize.x}×{f.yinGridSize.y}  属性加成：{Game.DrugBridge.AttrText(bonus.Attrs)}{extra}";
        }

        public void OnGUI()
        {
            if (!Game.DrugBridge.IsOpen)
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
                    rect = GUI.Window(WindowId, rect, DrawWindow, "自动炼丹");
                }
            }
            finally
            {
                GUI.skin.font = oldFont;
            }
        }

        bool pressing, dragged;

        Vector2 pressMouse, pressPos, dragPos;

        /// <summary>按钮当前位置。配置里是负数时用默认位置：右上角往下一点，避开游戏自己的按钮。</summary>
        Rect ButtonRect()
        {
            float x = dragged ? dragPos.x : Plugin.ButtonX.Value < 0 ? Screen.width - 190 : Plugin.ButtonX.Value;
            float y = dragged ? dragPos.y : Plugin.ButtonY.Value < 0 ? 90 : Plugin.ButtonY.Value;
            return new Rect(Mathf.Clamp(x, 0, Screen.width - 170), Mathf.Clamp(y, 0, Screen.height - 30), 170, 30);
        }

        /// <summary>单击开关窗口，按住拖动可以挪位置，松手后记到配置里。</summary>
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

        static readonly string[] ModeNames = { "最少材料", "低阶优先", "最多成丹", "最高品质", "最多可炼次数" };

        int chosen;

        bool lastTopUp;

        void DrawWindow(int id)
        {
            GUILayout.Label(furnaceText);
            GUILayout.BeginHorizontal();
            bool all = GUILayout.Toggle(Plugin.ShowAllRecipes.Value, "显示全部丹方");
            if (all != Plugin.ShowAllRecipes.Value)
            {
                Plugin.ShowAllRecipes.Value = all;
            }
            GUILayout.Label("搜索", GUILayout.Width(36));
            search = GUILayout.TextField(search ?? "", GUILayout.Width(160));
            GUILayout.EndHorizontal();

            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(190));
            foreach (var r in recipes)
            {
                if (!all && !r.Unlocked)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(search) && !(r.Name ?? "").Contains(search) && !(r.ItemName ?? "").Contains(search))
                {
                    continue;
                }
                string label = $"{(r == selected ? "▶ " : "")}{r.ItemName}（{r.Name}）  {r.AttrText}{(r.Unlocked ? "" : "  [未学]")}";
                if (GUILayout.Button(label, GUI.skin.label))
                {
                    selected = r;
                    result = null;
                    status = "";
                    // 布局内容变了，结束本帧 GUI，避免 Layout/Repaint 控件数不一致报错
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.EndScrollView();

            int mode = Mathf.Clamp((int)Plugin.DefaultMode.Value, 0, ModeNames.Length - 1);
            int newMode = GUILayout.SelectionGrid(mode, ModeNames, 3);
            if (newMode != mode)
            {
                Plugin.DefaultMode.Value = (DrugMode)newMode;
            }

            if (selected != null)
            {
                GUILayout.Label($"已选：{selected.ItemName}  要求 {selected.AttrText}");
                GUI.enabled = running == null;
                if (GUILayout.Button(running == null ? $"计算摆法（{ModeNames[newMode]}）" : "计算中…", GUILayout.Height(28)))
                {
                    StartSolve(false);
                    GUIUtility.ExitGUI();
                }
                GUI.enabled = true;
            }
            GUILayout.Label(status);

            if (result != null && result.Solutions.Count == 0 && !lastTopUp && running == null && selected != null)
            {
                if (GUILayout.Button("建议补料（算出最便宜的买法）", GUILayout.Height(26)))
                {
                    StartSolve(true);
                    GUIUtility.ExitGUI();
                }
            }

            if (result?.Best != null)
            {
                for (int i = 0; i < result.Solutions.Count; i++)
                {
                    var s = result.Solutions[i];
                    string head = $"方案{i + 1}：{s.PieceCount} 件  成丹 {s.Count}  品质 {s.Quality}" + (lastTopUp ? $"  补料约 {s.BuyCost} 灵石" : $"  可炼 {s.Batches} 次") + $"  最高品阶 {s.MaxGrade}";
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
                    int buy = lastTopUp && sol.ToBuy.TryGetValue(g.Key.Material, out int b) ? b : 0;
                    GUILayout.Label($"{(g.Key.Side == Side.Yang ? "阳" : "阴")}  {g.Key.Material.Name} ×{g.Count()}    （{Game.DrugBridge.AttrText(g.Key.Material.Attrs)}）" + (buy > 0 ? $"  需购买 {buy}" : ""));
                }
                GUILayout.EndScrollView();
                if (lastTopUp)
                {
                    GUILayout.Label("买齐标出的材料后，用上面的模式重新计算即可摆放。价格按物品基础价估算。");
                }
                else if (GUILayout.Button("摆上", GUILayout.Height(30)))
                {
                    try
                    {
                        applied = sol;
                        Game.DrugBridge.Apply(sol);
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

        Solution applied;

        void StartSolve(bool topUp)
        {
            try
            {
                var furnace = Game.DrugBridge.GetFurnaceCfg();
                if (furnace == null)
                {
                    status = "请先装备丹炉";
                    return;
                }
                var bonus = Game.DrugBridge.GetFixedBonus(furnace);
                var target = Attr.FromDic(selected.Cfg.AttrLimitDic);
                request = new DrugRequest
                {
                    Materials = Game.DrugBridge.GetMaterials(topUp),
                    Need = target.Select((v, d) => v - bonus.Attrs[d]).ToArray(),
                    Yang = new Grid(furnace.yangGridSize.x, furnace.yangGridSize.y),
                    Yin = new Grid(furnace.yinGridSize.x, furnace.yinGridSize.y),
                    TimeLimitMs = Plugin.TimeLimitMs.Value,
                    Mode = Plugin.DefaultMode.Value,
                    States = Game.DrugBridge.GetStates(selected.Cfg, bonus),
                    BaseCount = bonus.Count + selected.Cfg.count,
                    BaseQuality = bonus.Quality + 1,
                    TopUp = topUp
                };
                result = null;
                chosen = 0;
                lastTopUp = topUp;
                status = $"计算中…（候选材料 {request.Materials.Count} 种，需凑 {Game.DrugBridge.AttrText(request.Need)}）";
                var req = request;
                running = Task.Run(() => new DrugSolver(req).Solve());
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
            if (task.IsFaulted)
            {
                status = "计算出错：" + task.Exception?.GetBaseException().Message;
                Plugin.Log.LogError(task.Exception);
                return;
            }
            result = task.Result;
            if (result.Best == null)
            {
                status = $"{result.Message}（{result.ElapsedMs} ms）" + (lastTopUp ? "；买得到的材料也凑不出" : "");
                return;
            }
            foreach (var s in result.Solutions)
            {
                string err = DrugSolver.Verify(request, s);
                if (err != null)
                {
                    status = "内部复核失败：" + err;
                    Plugin.Log.LogError(status);
                    result.Solutions.Clear();
                    return;
                }
            }
            status = lastTopUp
                ? $"补料方案 {result.Solutions.Count} 个（{result.ElapsedMs} ms），最便宜约 {result.Best.BuyCost} 灵石。"
                : $"找到 {result.Solutions.Count} 个方案（{result.ElapsedMs} ms）" + (result.TimedOut ? "，已到时间上限，未必最优" : "") + "。选一个后点「摆上」。";
        }

        void Verify()
        {
            try
            {
                var (placed, recipe, count, quality) = Game.DrugBridge.ReadGameResult();
                int expected = applied?.PieceCount ?? 0;
                if (recipe != null && recipe.id == selected?.Cfg.id && placed == expected)
                {
                    bool same = applied == null || (applied.Count == count && applied.Quality == quality);
                    status = $"✓ 游戏已识别为「{selected.ItemName}」，成丹 {count} 颗，品质 {quality}。请点游戏里的「炼制」。";
                    if (!same)
                    {
                        status += $"\n（预测是成丹 {applied.Count}、品质 {applied.Quality}，和游戏不一致，以游戏为准；麻烦把日志发给作者）";
                        Plugin.Log.LogWarning($"品质/丹数预测不一致：丹方 {selected.Cfg.id} 预测 {applied.Count}/{applied.Quality} 游戏 {count}/{quality}；规则 {string.Join(",", request.States.Select(x => x.Id))}；摆法 " +
                            string.Join(" ", applied.Placements.Select(p => $"{p.Material.Id}@{(int)p.Side}:{p.X},{p.Y},{p.Rotate}")));
                    }
                }
                else
                {
                    string got = recipe == null ? "无丹方（会出废丹）" : recipe.GetName;
                    status = $"✗ 游戏结算不一致：摆上 {placed}/{expected} 件，识别为 {got}。请把日志发给作者。";
                    Plugin.Log.LogWarning(status);
                }
            }
            catch (Exception e)
            {
                status = "核对失败：" + e.Message;
                Plugin.Log.LogError(e);
            }
        }
    }

    [HarmonyPatch(typeof(GraphicRaycaster), nameof(GraphicRaycaster.Raycast), new[] { typeof(PointerEventData), typeof(List<RaycastResult>) })]
    internal static class GraphicRaycasterPatch
    {
        private static bool Prefix()
        {
            return !DrugWindow.MouseOver;
        }
    }

    /// <summary>
    /// 射线被挡掉后游戏会以为鼠标不在 UI 上，地图、棋子等会把点击当成点地图。
    /// 鼠标在窗口或按钮上时告诉游戏「在 UI 上」。
    /// </summary>
    [HarmonyPatch(typeof(EventSystem), nameof(EventSystem.IsPointerOverGameObject), new Type[0])]
    internal static class PointerOverPatch
    {
        private static void Postfix(ref bool __result)
        {
            __result |= DrugWindow.MouseOver;
        }
    }

    [HarmonyPatch(typeof(EventSystem), nameof(EventSystem.IsPointerOverGameObject), new[] { typeof(int) })]
    internal static class PointerOverIdPatch
    {
        private static void Postfix(ref bool __result)
        {
            __result |= DrugWindow.MouseOver;
        }
    }
}
