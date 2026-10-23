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

        Rect rect = new Rect(40, 80, 460, 640);

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
            MouseOver = visible && rect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
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
            string name = Singleton<TbDataImpl>.Instance.GetItemCfg(f.id)?.GetName ?? f.id;
            furnaceText = $"丹炉：{name}  阳 {f.yangGridSize.x}×{f.yangGridSize.y}  阴 {f.yinGridSize.x}×{f.yinGridSize.y}  属性加成：{Game.DrugBridge.AttrText(bonus)}";
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
                if (GUI.Button(new Rect(Screen.width - 190, 12, 170, 30), (visible ? "关闭自动摆放" : "自动摆放") + $" ({Plugin.ToggleKey.Value})"))
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

            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(230));
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
                }
            }
            GUILayout.EndScrollView();

            if (selected != null)
            {
                GUILayout.Label($"已选：{selected.ItemName}  要求 {selected.AttrText}");
                GUI.enabled = running == null;
                if (GUILayout.Button(running == null ? "计算摆法（最少材料）" : "计算中…", GUILayout.Height(28)))
                {
                    StartSolve();
                }
                GUI.enabled = true;
            }
            GUILayout.Label(status);

            if (result?.Best != null)
            {
                resultScroll = GUILayout.BeginScrollView(resultScroll, GUILayout.Height(160));
                foreach (var g in result.Best.Placements.GroupBy(p => (p.Side, p.Material)).OrderBy(g => g.Key.Side))
                {
                    GUILayout.Label($"{(g.Key.Side == Side.Yang ? "阳" : "阴")}  {g.Key.Material.Name} ×{g.Count()}    （{Game.DrugBridge.AttrText(g.Key.Material.Attrs)}）");
                }
                GUILayout.EndScrollView();
                if (GUILayout.Button("摆上", GUILayout.Height(30)))
                {
                    try
                    {
                        Game.DrugBridge.Apply(result.Best);
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
                    Materials = Game.DrugBridge.GetMaterials(),
                    Need = target.Select((v, d) => v - bonus[d]).ToArray(),
                    Yang = new Grid(furnace.yangGridSize.x, furnace.yangGridSize.y),
                    Yin = new Grid(furnace.yinGridSize.x, furnace.yinGridSize.y),
                    TimeLimitMs = Plugin.TimeLimitMs.Value
                };
                result = null;
                status = $"计算中…（背包材料 {request.Materials.Count} 种，需凑 {Game.DrugBridge.AttrText(request.Need)}）";
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
                status = $"{result.Message}（{result.ElapsedMs} ms）";
                return;
            }
            string err = DrugSolver.Verify(request, result.Best);
            if (err != null)
            {
                status = "内部复核失败：" + err;
                Plugin.Log.LogError(status);
                result = null;
                return;
            }
            status = $"找到摆法：共 {result.Best.PieceCount} 件材料（{result.ElapsedMs} ms）。确认后点「摆上」。";
        }

        void Verify()
        {
            try
            {
                var (placed, recipe, count, quality) = Game.DrugBridge.ReadGameResult();
                int expected = result?.Best?.PieceCount ?? 0;
                if (recipe != null && recipe.id == selected?.Cfg.id && placed == expected)
                {
                    status = $"✓ 游戏已识别为「{selected.ItemName}」，成丹 {count} 颗，品质 {quality}。请点游戏里的「炼制」。";
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
}
