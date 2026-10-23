using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZaohuaAutoCraft
{
    // 炼丹界面打开时显示「自动摆放」按钮：选丹方 → 算摆法 → 预览 → 摆上（炼制仍由玩家自己点）。
    [BepInPlugin("zaohua.autocraft", "自动炼丹炼器", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowAllRecipes;

        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        internal static ConfigEntry<int> TimeLimitMs;

        private DrugWindow window;

        private void Awake()
        {
            Log = Logger;
            ShowAllRecipes = Config.Bind("炼丹", "ShowAllRecipes", false, "true = 列出全部丹方（未学的也能按属性炼出）；false = 只列已学丹方和自己炼过的配方");
            ToggleKey = Config.Bind("通用", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "打开/关闭自动摆放窗口");
            TimeLimitMs = Config.Bind("通用", "TimeLimitMs", 2000, "每次求解的时间上限（毫秒）");
            window = new DrugWindow();
            new Harmony("zaohua.autocraft").PatchAll();
        }

        private void Update()
        {
            window.Update();
        }

        private void OnGUI()
        {
            window.OnGUI();
        }
    }
}
