using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZaohuaAutoCraft
{
    // 炼丹界面打开时显示「自动摆放」按钮：选丹方 → 算摆法 → 预览 → 摆上（炼制仍由玩家自己点）。
    [BepInPlugin("zaohua.autocraft", "自动炼丹炼器", "0.1.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowAllRecipes;

        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        internal static ConfigEntry<int> TimeLimitMs;

        private void Awake()
        {
            Log = Logger;
            ShowAllRecipes = Config.Bind("炼丹", "ShowAllRecipes", false, "true = 列出全部丹方（未学的也能按属性炼出）；false = 只列已学丹方和自己炼过的配方");
            // 游戏自己占用 F5 快速存档、F8 快速读档，这里换了键名，旧配置里的 F8 不再生效
            ToggleKey = Config.Bind("通用", "OpenKey", new KeyboardShortcut(KeyCode.F9), "打开/关闭自动摆放窗口（不要设成 F5/F8，那是游戏的快速存读档）");
            TimeLimitMs = Config.Bind("通用", "TimeLimitMs", 2000, "每次求解的时间上限（毫秒）");
            new Harmony("zaohua.autocraft").PatchAll();
            // BepInEx 自带的管理物体在这个游戏里会被销毁，导致插件的 Update/OnGUI 不跑，所以自己建一个常驻物体
            var go = new GameObject("ZaohuaAutoCraft");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
            Log.LogInfo("自动炼丹炼器已加载，炼丹界面右上角有「自动摆放」按钮，快捷键 " + ToggleKey.Value);
        }
    }

    internal class Runner : MonoBehaviour
    {
        private readonly DrugWindow window = new DrugWindow();

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
