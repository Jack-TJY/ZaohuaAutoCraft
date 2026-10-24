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
        Element = it.ValueKind == JsonValueKind.Object && it.TryGetProperty("attribute", out var at) ? at.GetInt32() : 0,
        Buyable = it.ValueKind == JsonValueKind.Object && ((it.TryGetProperty("IsShop", out var sh) && sh.GetInt32() == 1) || (it.TryGetProperty("IsTrader", out var tr) && tr.GetInt32() == 1)),
    });
}
var states = XDocument.Load(Path.Combine(dir, "TbDrugRecipeStateCfg")).Root.Elements().Select(e => new DrugState
{
    Id = (int)e.Element("id"),
    Name = (string)e.Element("name"),
    PoolType = (int?)e.Element("poolType") ?? 0,
    StateType = (int?)e.Element("stateType") ?? 0,
    Area = (string)e.Element("area"),
    Target1 = (string)e.Element("target1"),
    Target2 = (string)e.Element("target2"),
    Relation = (int?)e.Element("relation") ?? 0,
    CalculateType = (string)e.Element("calculateType"),
    BaseEff = (string)e.Element("baseEff"),
}).ToDictionary(s => s.Id);
var recipes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "TbDrugRecipeCfg"))).RootElement.EnumerateArray().ToList();
Console.WriteLine($"材料 {mats.Count} 种，丹方 {recipes.Count} 个");

static Dictionary<string, int> ParseDic(string s) => string.IsNullOrEmpty(s) ? new() :
    s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).ToDictionary(p => p[0], p => int.Parse(p[1]));

int failures = 0;
string rid = null;
Material Copy(Material m, long stock) => new Material { Id = m.Id, Name = m.Name, Attrs = m.Attrs, Shape = m.Shape, Grade = m.Grade, Price = m.Price, Element = m.Element, Buyable = m.Buyable, Stock = stock };
void Run(string label, Func<JsonElement, List<Material>> stockOf, Grid yang, Grid yin, int timeLimit, DrugMode mode = DrugMode.MinPieces, bool topUp = false)
{
    int ok = 0, noFit = 0, none = 0, timeout = 0;
    long maxMs = 0, totalMs = 0, sumCount = 0, sumQuality = 0, sumBatches = 0, sumPieces = 0, sumCost = 0;
    string slowest = "";
    foreach (var r in recipes)
    {
        var st = r.GetProperty("stateIdStr").ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Where(states.ContainsKey).Select(i => states[i]).ToList();
        var req = new DrugRequest
        {
            Materials = stockOf(r),
            Need = Attr.FromDic(ParseDic(r.GetProperty("attrLimiteStr").GetString())),
            Yang = yang,
            Yin = yin,
            TimeLimitMs = timeLimit,
            Mode = mode,
            States = st,
            BaseCount = r.GetProperty("count").GetInt32(),
            BaseQuality = 1,
            TopUp = topUp,
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
            foreach (var sol in res.Solutions)
            {
                string err = DrugSolver.Verify(req, sol);
                if (err != null)
                {
                    failures++;
                    Console.WriteLine($"  !! 丹方 {r.GetProperty("id")} 复核失败: {err}");
                    if (rid != null) foreach (var p in sol.Placements) Console.WriteLine($"     {p.Side} {p.Material.Id} {string.Join(",", p.Material.Attrs)} stock={p.Material.Stock} @{p.X},{p.Y} r{p.Rotate}");
                }
            }
            ok++;
            sumCount += res.Best.Count;
            sumQuality += res.Best.Quality;
            sumBatches += res.Best.Batches;
            sumPieces += res.Best.PieceCount;
            sumCost += res.Best.BuyCost;
        }
        else if (res.TimedOut) { timeout++; if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"  超时 {r.GetProperty("id")}: {r.GetProperty("attrLimiteStr")} 组合 {res.CombosTried}"); }
        else if (res.FoundComboButNoFit) noFit++;
        else none++;
    }
    double n = Math.Max(1, ok);
    Console.WriteLine($"[{label}] 成功 {ok} / 装不下 {noFit} / 无解 {none} / 超时 {timeout}；平均 {totalMs / recipes.Count} ms，最慢 {maxMs} ms（丹方 {slowest}）；均件数 {sumPieces / n:F2} 均丹数 {sumCount / n:F2} 均品质 {sumQuality / n:F2} 均批数 {sumBatches / n:F1}" + (topUp ? $" 均补料花费 {sumCost / n:F0}" : ""));
}

rid = Environment.GetEnvironmentVariable("RECIPE");
if (rid != null)
{
    recipes = recipes.Where(r => r.GetProperty("id").ToString() == rid).ToList();
    DrugSolver.Debug = Console.WriteLine;
}
var only = Environment.GetEnvironmentVariable("ONLY");
bool Want(string tag) => only == null || only.Split(',').Contains(tag);

// 1. 只给丹方示例材料（各 1 份），大丹炉：示例本身就是一个解，必须全部成功
if (Want("ex")) Run("示例材料·大炉", r =>
{
    var ex = ParseDic(r.GetProperty("exampleItemStr").GetString());
    return mats.Where(m => ex.ContainsKey(m.Id)).Select(m => Copy(m, ex[m.Id])).ToList();
}, new Grid(10, 10), new Grid(9, 10), 2000);

// 2. 全部材料各 99 件：最坏情况的搜索量
var all = mats.Select(m => Copy(m, 99)).ToList();
var rnd = new Random(42);
var some = mats.Select(m => Copy(m, rnd.Next(4) == 0 ? rnd.Next(1, 30) : 0)).ToList();
foreach (var mode in Enum.GetValues<DrugMode>())
{
    if (!Want(mode.ToString())) continue;
    if (Environment.GetEnvironmentVariable("SMALL") == null)
    {
        Run($"全材料·大炉·{mode}", r => all, new Grid(10, 10), new Grid(9, 10), 2000, mode);
        Run($"全材料·5x8·{mode}", r => all, new Grid(5, 8), new Grid(5, 8), 2000, mode);
    }
    Run($"全材料·3x3·{mode}", r => all, new Grid(3, 3), new Grid(3, 3), 2000, mode);
    if (Environment.GetEnvironmentVariable("SMALL") == null) Run($"随机库存·大炉·{mode}", r => some, new Grid(10, 10), new Grid(9, 10), 2000, mode);
}
// 3. 补料：随机库存，全部可买
if (Want("topup")) Run("补料·随机库存·大炉", r => mats.Select(m => Copy(m, some.First(x => x.Id == m.Id).Stock)).ToList(), new Grid(10, 10), new Grid(9, 10), 2000, DrugMode.MinPieces, true);

Console.WriteLine(failures == 0 ? "复核全部通过" : $"复核失败 {failures} 个");
return failures == 0 ? 0 : 1;
