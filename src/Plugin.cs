using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZaohuaAutoCraft
{
    // 炼丹界面打开时显示「自动摆放」按钮：选丹方 → 算摆法 → 预览 → 摆上（炼制仍由玩家自己点）。
    [BepInPlugin("zaohua.autocraft", "自动炼丹炼器", "0.2.4")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowAllRecipes;

        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        internal static ConfigEntry<int> TimeLimitMs;

        internal static ConfigEntry<int> PlacementTimeLimitMs;

        internal static ConfigEntry<int> ButtonX, ButtonY;

        internal static ConfigEntry<Solver.DrugMode> DefaultMode;

        private void Awake()
        {
            Log = Logger;
            ShowAllRecipes = Config.Bind("炼丹", "ShowAllRecipes", false, "true = 列出全部丹方（未学的也能按属性炼出）；false = 只列已学丹方和自己炼过的配方");
            // 游戏自己占用 F5 快速存档、F8 快速读档，这里换了键名，旧配置里的 F8 不再生效
            ToggleKey = Config.Bind("通用", "OpenKey", new KeyboardShortcut(KeyCode.F9), "打开/关闭自动摆放窗口（不要设成 F5/F8，那是游戏的快速存读档）");
            TimeLimitMs = Config.Bind("通用", "TimeLimitMs", 3000, "最长计算时间（毫秒），用于最少材料/最低品阶/最多可炼次数/建议补料。一般 1 秒内算完，只有材料很多或丹炉很小时才会用满；用满仍无解可调大。窗口里也能调");
            PlacementTimeLimitMs = Config.Bind("炼丹", "PlacementTimeLimitMs", 8000, "最长计算时间（毫秒），用于最多成丹/最高品质。要反复试垫料和摆位，时间越长丹数/品质越高（通法丹实测 3 秒约 300 颗、8 秒约 313 颗），但要等这么久才出结果。窗口里也能调");
            ButtonX = Config.Bind("通用", "ButtonX", -1, "「自动摆放」按钮的位置（像素，左上角为原点）；在游戏里按住按钮拖动会自动改这里，-1 = 默认位置");
            ButtonY = Config.Bind("通用", "ButtonY", -1, "同上，纵坐标");
            DefaultMode = Config.Bind("炼丹", "Mode", Solver.DrugMode.MinPieces, "默认模式：MinPieces 最少材料 / LowGrade 最低品阶（先让用到的最高品阶最低，再比品阶总和，最后比灵石价值） / MaxCount 最多成丹 / MaxQuality 最高品质 / MaxBatches 最多可炼次数（窗口里切换会自动记住）");
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
