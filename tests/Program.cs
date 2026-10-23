using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using ZaohuaAutoCraft.Solver;

// 用法: dotnet run -- <解包目录>
// 解包目录里需要 CraftingItem、TbItemCfg、TbDrawCfg、TbGradeCfg、TbDrugRecipeCfg（tools/extract.py 解出）
string dir = args.Length > 0 ? args[0] : "unpacked/data";

var draws = XDocument.Load(Path.Combine(dir, "TbDrawCfg")).Root.Elements()
    .ToDictionary(e => (string)e.Element("id"), e => ((string)e.Element("coordinate") ?? "0,0").Split('&')
        .Select(p => p.Split(',')).Select(p => new Cell(int.Parse(p[0]), int.Parse(p[1]))).ToList());
var grades = XDocument.Load(Path.Combine(dir, "TbGradeCfg")).Root.Elements()
    .ToDictionary(e => (int)e.Element("id"), e => (int?)e.Element("weight") ?? 0);
var items = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "TbItemCfg"))).RootElement.EnumerateArray()
    .ToDictionary(e => e.GetProperty("id").ToString(), e => e);

var mats = new List<Material>();
foreach (var c in JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "CraftingItem"))).RootElement.EnumerateArray())
{
    if (!c.TryGetProperty("attrDic", out var ad) || !c.TryGetProperty("drawId", out var di))
    {
        continue;
    }
    string id = c.GetProperty("id").ToString();
    var dic = ad.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
    items.TryGetValue(id, out var it);
    mats.Add(new Material
    {
        Id = id,
        Name = id,
        Attrs = Attr.FromDic(dic),
        Shape = draws[di.ToString()],
        Grade = it.ValueKind == JsonValueKind.Object && it.TryGetProperty("gradeId", out var g) ? grades.GetValueOrDefault(g.GetInt32()) : 0,
        Price = it.ValueKind == JsonValueKind.Object && it.TryGetProperty("price", out var pr) ? pr.GetInt32() : 0,
    });
}
var recipes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "TbDrugRecipeCfg"))).RootElement.EnumerateArray().ToList();
Console.WriteLine($"材料 {mats.Count} 种，丹方 {recipes.Count} 个");

static Dictionary<string, int> ParseDic(string s) => string.IsNullOrEmpty(s) ? new() :
    s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).ToDictionary(p => p[0], p => int.Parse(p[1]));

int failures = 0;
void Run(string label, Func<JsonElement, List<Material>> stockOf, Grid yang, Grid yin, int timeLimit)
{
    int ok = 0, noFit = 0, none = 0, timeout = 0;
    long maxMs = 0, totalMs = 0;
    string slowest = "";
    foreach (var r in recipes)
    {
        var req = new DrugRequest
        {
            Materials = stockOf(r),
            Need = Attr.FromDic(ParseDic(r.GetProperty("attrLimiteStr").GetString())),
            Yang = yang,
            Yin = yin,
            TimeLimitMs = timeLimit,
        };
        var res = new DrugSolver(req).Solve();
        totalMs += res.ElapsedMs;
        if (res.ElapsedMs > maxMs)
        {
            maxMs = res.ElapsedMs;
            slowest = r.GetProperty("id").ToString();
        }
        if (res.Best != null)
        {
            string err = DrugSolver.Verify(req, res.Best);
            if (err != null)
            {
                failures++;
                Console.WriteLine($"  !! 丹方 {r.GetProperty("id")} 复核失败: {err}");
            }
            ok++;
        }
        else if (res.TimedOut) { timeout++; if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"  超时 {r.GetProperty("id")}: {r.GetProperty("attrLimiteStr")} 组合 {res.CombosTried}"); }
        else if (res.FoundComboButNoFit) noFit++;
        else none++;
    }
    Console.WriteLine($"[{label}] 成功 {ok} / 装不下 {noFit} / 无解 {none} / 超时 {timeout}；平均 {totalMs / recipes.Count} ms，最慢 {maxMs} ms（丹方 {slowest}）");
}

// 1. 只给丹方示例材料（各 1 份），大丹炉：示例本身就是一个解，必须全部成功
Run("示例材料·大炉", r =>
{
    var ex = ParseDic(r.GetProperty("exampleItemStr").GetString());
    return mats.Where(m => ex.ContainsKey(m.Id)).Select(m => new Material { Id = m.Id, Name = m.Name, Attrs = m.Attrs, Shape = m.Shape, Grade = m.Grade, Price = m.Price, Stock = ex[m.Id] }).ToList();
}, new Grid(10, 10), new Grid(9, 10), 2000);

// 2. 全部材料各 99 件：最坏情况的搜索量
var all = mats.Select(m => new Material { Id = m.Id, Name = m.Name, Attrs = m.Attrs, Shape = m.Shape, Grade = m.Grade, Price = m.Price, Stock = 99 }).ToList();
Run("全材料·大炉", r => all, new Grid(10, 10), new Grid(9, 10), 2000);
Run("全材料·5x8", r => all, new Grid(5, 8), new Grid(5, 8), 2000);
Run("全材料·3x3", r => all, new Grid(3, 3), new Grid(3, 3), 2000);

Console.WriteLine(failures == 0 ? "复核全部通过" : $"复核失败 {failures} 个");
return failures == 0 ? 0 : 1;
