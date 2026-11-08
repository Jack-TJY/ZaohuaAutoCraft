using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Google.OrTools.Sat;
using ZaohuaAutoCraft.Solver;
using ZaohuaAutoCraft.Tests;

// 合成炼器测试（不依赖解包数据）：SYNTH=1 时运行后退出
if (Environment.GetEnvironmentVariable("SYNTH") == "1")
{
    RunSynthEquip();
    return 0;
}

void RunSynthEquip()
{
    Material M(string id, int[] attrs, long stock = 99) => new Material
    {
        Id = id, Name = id, Attrs = attrs, Shape = new List<Cell> { new Cell(0, 0) }, Stock = stock, Grade = 1, Price = 1
    };
    var rune1 = new List<Rune> { new Rune { Id = "r1", Name = "r1", Requirement = new[] { 100, 0, 0, 50, 0, 0, 0, 0 }, MaxStacks = 5, Weight = 1 } };
    var mats1 = new List<Material>
    {
        M("goldOre", new[] { 100, 0, 0, 0, 0, 0, 0, 0 }),
        M("fireOre", new[] { 0, 0, 0, 50, 0, 0, 0, 0 }),
        M("mixed", new[] { 60, 0, 0, 30, 0, 0, 0, 0 }),
        M("negGold", new[] { -80, 0, 0, 0, 0, 0, 0, 0 })
    };
    foreach (var mode in new[] { EquipmentMode.MinPieces, EquipmentMode.MaxActiveRunes })
    {
        var req = new EquipmentRequest { Materials = mats1, Runes = rune1, Yang = new Grid(3, 3), Yin = new Grid(3, 3), Mode = mode, TimeLimitMs = 2000, TopK = 3 };
        var res = new EquipmentSolver(req).Solve();
        Console.WriteLine($"[合成·{mode}] 解 {res.Solutions.Count} 个，{res.ElapsedMs}ms，{(res.Message ?? "")}");
        foreach (var s in res.Solutions)
        {
            Console.WriteLine($"  {s.PieceCount}件 等级[{string.Join(",", s.Runes.Level)}] 生效{s.Runes.ActiveCount} 品阶分{s.Runes.GradeScore} 属性[{string.Join(",", s.Runes.FinalAttrs)}] 法器属性{s.Runes.ItemAttr}");
            Console.WriteLine("  Verify: " + (EquipmentSolver.Verify(req, s) ?? "OK"));
        }
    }
    // 阴格必须：唯一含金的材料是负值，放阴格翻正
    var rune2 = new List<Rune> { new Rune { Id = "r2", Name = "r2", Requirement = new[] { 80, 0, 0, 0, 0, 0, 0, 0 }, MaxStacks = 3, Weight = 1 } };
    var mats2 = new List<Material> { M("negGold", new[] { -80, 0, 0, 0, 0, 0, 0, 0 }) };
    var req2 = new EquipmentRequest { Materials = mats2, Runes = rune2, Yang = new Grid(3, 3), Yin = new Grid(3, 3), Mode = EquipmentMode.MinPieces, TimeLimitMs = 2000, TopK = 1 };
    var res2 = new EquipmentSolver(req2).Solve();
    Console.WriteLine($"[合成·阴格] 解 {res2.Solutions.Count} 个，{(res2.Message ?? "")}");
    foreach (var s in res2.Solutions)
    {
        Console.WriteLine($"  {s.PieceCount}件 等级[{string.Join(",", s.Runes.Level)}] 属性[{string.Join(",", s.Runes.FinalAttrs)}]");
        Console.WriteLine("  Verify: " + (EquipmentSolver.Verify(req2, s) ?? "OK"));
    }
}

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
        TypeId = it.ValueKind == JsonValueKind.Object && it.TryGetProperty("typeId", out var ti) ? ti.GetInt32() : 0,
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

// 炼器：符文（TbCraftingRuneCfg 是 JSON）。DefaultWeight = CraftingRuneWeight[grade.weight]（移植游戏公式）
var CraftingRuneWeight = new[] { 0, 1, 4, 7, 10, 40, 70, 100, 400, 700, 1000, 4000, 7000 };
var allRunes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "TbCraftingRuneCfg"))).RootElement.EnumerateArray().Select(e =>
{
    var r = new Rune
    {
        Id = e.GetProperty("id").GetString(),
        Name = e.GetProperty("name").GetString(),
        Requirement = Attr.FromDic(ParseDic(e.GetProperty("attrLimiteStr").GetString())),
        MaxStacks = e.GetProperty("maxStacks").GetInt32(),
        Weight = e.TryGetProperty("weight", out var w) ? w.GetInt32() : 0
    };
    int gw = grades.GetValueOrDefault(e.TryGetProperty("grade", out var g) ? g.GetInt32() : 0);
    r.DefaultWeight = gw >= 0 && gw < CraftingRuneWeight.Length ? CraftingRuneWeight[gw] : 0;
    return r;
}).ToList();
Console.WriteLine($"符文 {allRunes.Count} 个");

int failures = 0;
string rid = null;
Material Copy(Material m, long stock) => new Material { Id = m.Id, Name = m.Name, Attrs = m.Attrs, Shape = m.Shape, Grade = m.Grade, Price = m.Price, Element = m.Element, Buyable = m.Buyable, TypeId = m.TypeId, Stock = stock };
void Run(string label, Func<JsonElement, List<Material>> stockOf, Grid yang, Grid yin, int timeLimit, DrugMode mode = DrugMode.MinPieces, bool topUp = false)
{
    int ok = 0, noFit = 0, none = 0, timeout = 0;
    long maxMs = 0, totalMs = 0, sumCount = 0, sumQuality = 0, sumBatches = 0, sumPieces = 0, sumCost = 0, sumPrice = 0;
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
            sumPrice += res.Best.Price;
        }
        else if (res.TimedOut) { timeout++; if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"  超时 {r.GetProperty("id")}: {r.GetProperty("attrLimiteStr")} 组合 {res.CombosTried}"); }
        else if (res.FoundComboButNoFit) noFit++;
        else none++;
    }
    double n = Math.Max(1, ok);
    Console.WriteLine($"[{label}] 成功 {ok} / 装不下 {noFit} / 无解 {none} / 超时 {timeout}；平均 {totalMs / recipes.Count} ms，最慢 {maxMs} ms（丹方 {slowest}）；均件数 {sumPieces / n:F2} 均丹数 {sumCount / n:F2} 均品质 {sumQuality / n:F2} 均批数 {sumBatches / n:F1} 均价值 {sumPrice / n:F0}" + (topUp ? $" 均补料花费 {sumCost / n:F0}" : ""));
}

// 炼器离线测试：给定库存规则，逐个符文验证求解结果与复核
void RunEquip(string label, Func<Material, long> stockOf, Grid yang, Grid yin, EquipmentMode mode, int timeLimit, int targetAttr = -1, bool requireActive = true)
{
    int ok = 0, none = 0, noFit = 0, timeout = 0;
    long maxMs = 0, totalMs = 0;
    string slowest = "";
    foreach (var r in allRunes)
    {
        var m2 = mats.Where(m => m.TypeId == 361).Select(m => Copy(m, stockOf(m))).ToList();
        var req = new EquipmentRequest { Materials = m2, Runes = new List<Rune> { r }, Yang = yang, Yin = yin, Mode = mode, TimeLimitMs = timeLimit, TopK = 1, TargetAttr = targetAttr };
        var res = new EquipmentSolver(req).Solve();
        totalMs += res.ElapsedMs;
        if (res.ElapsedMs > maxMs) { maxMs = res.ElapsedMs; slowest = r.Id; }
        if (res.Best != null)
        {
            string err = EquipmentSolver.Verify(req, res.Best);
            if (err != null) { failures++; Console.WriteLine($"  !! 符文 {r.Id} 复核失败: {err}"); }
            else if (requireActive && res.Best.Runes.ActiveCount < 1) { failures++; Console.WriteLine($"  !! 符文 {r.Id} 未激活 等级[{string.Join(",", res.Best.Runes.Level)}]"); }
            else ok++;
        }
        else if (res.TimedOut) { timeout++; if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"  超时 符文 {r.Id} 组合 {res.CombosTried}"); }
        else if (res.FoundComboButNoFit) noFit++;
        else none++;
    }
    Console.WriteLine($"[炼器·{label}] 成功 {ok} / 无解 {none} / 装不下 {noFit} / 超时 {timeout}；平均 {totalMs / Math.Max(1, allRunes.Count)} ms，最慢 {maxMs} ms（符文 {slowest}）");
}

rid = Environment.GetEnvironmentVariable("RECIPE");
if (rid != null)
{
    recipes = recipes.Where(r => r.GetProperty("id").ToString() == rid).ToList();
    DrugSolver.Debug = Console.WriteLine;
}
// 用玩家存档的库存和配方复现：SAVE=<存档 json> STOCK=<库存 json> RECIPE=80
var savePath = Environment.GetEnvironmentVariable("SAVE");
if (savePath != null)
{
    var stock = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Environment.GetEnvironmentVariable("STOCK")));
    var mine = mats.Where(m => stock.ContainsKey(m.Id)).Select(m => Copy(m, stock[m.Id])).ToList();
    var r = recipes[0];
    var st = r.GetProperty("stateIdStr").ToString().Split(',').Select(int.Parse).Select(i => states[i]).ToList();
    var yang = new Grid(10, 10);
    var yin = new Grid(9, 10);
    int baseCount = 4 + r.GetProperty("count").GetInt32(), baseQuality = 2 + 1;
    var ev = new DrugStateEvaluator(st, yang, yin);
    var save = JsonDocument.Parse(File.ReadAllText(savePath)).RootElement;
    var byId = mats.ToDictionary(m => m.Id);
    foreach (var t in save.GetProperty("craftingTemplateStoList").EnumerateArray())
    {
        if (t.GetProperty("type").GetInt32() != 0 || t.GetProperty("itemLogStoList").GetArrayLength() < 20) continue;
        var pl = t.GetProperty("itemLogStoList").EnumerateArray().Select(x => new Placement
        {
            Material = byId[x.GetProperty("itemId").GetProperty("sedId").GetString()],
            Side = (Side)x.GetProperty("poolType").GetInt32(),
            X = x.GetProperty("pos").GetProperty("x").GetInt32(),
            Y = x.GetProperty("pos").GetProperty("y").GetInt32(),
            Rotate = x.GetProperty("rotate").GetInt32()
        }).ToList();
        var (c0, q0) = ev.Evaluate(ev.BuildBoard(pl), baseCount, baseQuality);
        Console.WriteLine($"玩家配方 {t.GetProperty("id")}: {pl.Count} 件 → 预测成丹 {c0} 品质 {q0}");
    }
    var dump = Environment.GetEnvironmentVariable("DUMP");
    if (dump != null)
    {
        var lines = new List<string> { $"need {string.Join(",", Attr.FromDic(ParseDic(r.GetProperty("attrLimiteStr").GetString())))}", $"base {baseCount} {baseQuality}" };
        foreach (var x in st) lines.Add($"state {x.Id}\t{x.PoolType}\t{x.StateType}\t{x.Area}\t{x.Target1}\t{x.Target2}\t{x.Relation}\t{x.CalculateType}\t{x.BaseEff}");
        foreach (var m in mine) lines.Add($"mat {m.Id}\t{string.Join(",", m.Attrs)}\t{string.Join(";", m.Shape.Select(c => c.X + "," + c.Y))}\t{m.Stock}\t{m.Grade}\t{m.Price}\t{m.Element}");
        File.WriteAllLines(dump, lines);
    }
    foreach (int tl in new[] { 3000, 10000 })
    {
        var req = new DrugRequest { Materials = mine, Need = Attr.FromDic(ParseDic(r.GetProperty("attrLimiteStr").GetString())), Yang = yang, Yin = yin, TimeLimitMs = tl, Mode = DrugMode.MaxCount, States = st, BaseCount = baseCount, BaseQuality = baseQuality };
        var res = new DrugSolver(req).Solve();
        foreach (var sol in res.Solutions)
        {
            Console.WriteLine($"[{tl}ms] 求解: {sol.PieceCount} 件 成丹 {sol.Count} 品质 {sol.Quality} 复核 {DrugSolver.Verify(req, sol) ?? "OK"}  " + string.Join(" ", sol.Placements.GroupBy(p => (p.Material.Id, p.Side)).Select(g => $"{g.Key.Id}@{(int)g.Key.Side}x{g.Count()}")));
        }
        Console.WriteLine($"  用时 {res.ElapsedMs} ms");
    }
    return 0;
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
var equipAll = all.Where(m => m.TypeId == 361).ToList();
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

// 4. 炼器：单个符文逐个验证（全材料 99 = 最坏搜索量）
if (Want("equip"))
{
    RunEquip("全材料·大锤·最少材料", _ => 99, new Grid(10, 10), new Grid(9, 10), EquipmentMode.MinPieces, 2000);
    RunEquip("全材料·3x3·最少材料", _ => 99, new Grid(3, 3), new Grid(3, 3), EquipmentMode.MinPieces, 2000);
    RunEquip("全材料·大锤·指定符文优先", _ => 99, new Grid(10, 10), new Grid(9, 10), EquipmentMode.MaxRuneLevel, 2000);
    RunEquip("全材料·大锤·最低品阶", _ => 99, new Grid(10, 10), new Grid(9, 10), EquipmentMode.LowGrade, 2000);
    RunEquip("全材料·大锤·最高品阶", _ => 99, new Grid(10, 10), new Grid(9, 10), EquipmentMode.MaxGrade, 2000, requireActive: false);
    RunEquip("全材料·大锤·最大属性(金)", _ => 99, new Grid(10, 10), new Grid(9, 10), EquipmentMode.MaxAttr, 2000, targetAttr: 0, requireActive: false);
    // 多符文·满足最多符文
    var multiRunes = allRunes.Take(5).ToList();
    var multi = new EquipmentRequest { Materials = equipAll, Runes = multiRunes, Yang = new Grid(10, 10), Yin = new Grid(9, 10), Mode = EquipmentMode.MaxActiveRunes, TimeLimitMs = 3000, TopK = 1 };
    var mr = new EquipmentSolver(multi).Solve();
    Console.WriteLine($"[炼器·多符文·满足最多符文] 解 {mr.Solutions.Count} 个，{mr.ElapsedMs}ms，{(mr.Message ?? "")}");
    foreach (var s in mr.Solutions)
    {
        Console.WriteLine($"  {s.PieceCount}件 生效{s.Runes.ActiveCount}/{multiRunes.Count} 品阶分{s.Runes.GradeScore} 复核 {(EquipmentSolver.Verify(multi, s) ?? "OK")}");
    }
    // 指定符文优先：前 2 个优先
    var priRunes = allRunes.Take(4).ToList();
    var pri = new EquipmentRequest { Materials = equipAll, Runes = priRunes, Yang = new Grid(10, 10), Yin = new Grid(9, 10), Mode = EquipmentMode.MaxRuneLevel, TimeLimitMs = 3000, TopK = 1, Priority = new[] { true, true, false, false } };
    var pr = new EquipmentSolver(pri).Solve();
    Console.WriteLine($"[炼器·指定符文优先(前2优先)] 解 {pr.Solutions.Count} 个，{pr.ElapsedMs}ms，{(pr.Message ?? "")}");
    foreach (var s in pr.Solutions)
    {
        Console.WriteLine($"  {s.PieceCount}件 等级[{string.Join(",", s.Runes.Level)}] 优先级{s.Runes.Level.Take(2).Sum()}/非优先{s.Runes.Level.Skip(2).Sum()} 复核 {(EquipmentSolver.Verify(pri, s) ?? "OK")}");
    }
    // 飞剑5 配方复现（存档 typeId=151 的符文）
    var flyIds = new[] { "216", "266", "223", "234", "307", "143", "105" };
    var flyRunes = allRunes.Where(r => flyIds.Contains(r.Id)).ToList();
    Console.WriteLine($"[飞剑5] 找到符文 {flyRunes.Count}/{flyIds.Length} 个，满级需求 {string.Join(",", new EquipmentRequest { Runes = flyRunes }.Runes.SelectMany(r => r.Requirement).ToArray())}");
    foreach (var mode in new[] { EquipmentMode.MinPieces, EquipmentMode.LowGrade })
    {
        var fr = new EquipmentRequest { Materials = equipAll, Runes = flyRunes, Yang = new Grid(10, 10), Yin = new Grid(9, 10), Mode = mode, TimeLimitMs = 3000, TopK = 1 };
        var fres = new EquipmentSolver(fr).Solve();
        Console.WriteLine($"[飞剑5·{mode}] 解 {fres.Solutions.Count} 个，{fres.ElapsedMs}ms，{(fres.Message ?? "")}");
        foreach (var s in fres.Solutions)
        {
            Console.WriteLine($"  {s.PieceCount}件 等级[{string.Join(",", s.Runes.Level)}] 复核 {(EquipmentSolver.Verify(fr, s) ?? "OK")}");
            foreach (var g in s.Placements.GroupBy(p => p.Material).OrderByDescending(g => g.Key.Grade))
            {
                Console.WriteLine($"    品阶{g.Key.Grade} 价格{g.Key.Price} id={g.Key.Id} attrs=[{string.Join(",", g.Key.Attrs)}] ×{g.Count()}");
            }
        }
    }
}

// 5. CP-SAT 数量主问题速度测试（测试专用，跑在 net8.0）
if (Environment.GetEnvironmentVariable("CPSAT") != null)
{
    Console.WriteLine("===== CP-SAT 速度测试 =====");
    var flyIds = new[] { "216", "266", "223", "234", "307", "143", "105" };
    var flyRunes = allRunes.Where(r => flyIds.Contains(r.Id)).ToList();
    var fly = CpSatSolver.Solve(equipAll, flyRunes, new int[Attr.Count], new Grid(10, 10), new Grid(9, 10), 10.0);
    Console.WriteLine($"[飞剑5·CP-SAT] {fly.Message} 价{fly.TotalPrice} 件{fly.PieceCount} 数量{fly.QuantityMs}ms 装填{fly.PackMs}ms");
    if (fly.Ok)
    {
        foreach (var g in fly.Items.GroupBy(x => x.mat).OrderByDescending(g => g.Key.Grade))
        {
            Console.WriteLine($"    品阶{g.Key.Grade} 价格{g.Key.Price} id={g.Key.Id} attrs=[{string.Join(",", g.Key.Attrs)}] ×{g.Count()}");
        }
    }
    else
    {
        Console.WriteLine($"    详情: {fly.Message}");
    }
    // 全 311 符文（单符文最坏搜索）
    long maxQ = 0, totalQ = 0, maxP = 0, maxTotal = 0;
    int n = 0, ok = 0, infeas = 0;
    foreach (var r in allRunes)
    {
        var one = CpSatSolver.Solve(equipAll, new List<Rune> { r }, new int[Attr.Count], new Grid(10, 10), new Grid(9, 10), 5.0);
        totalQ += one.QuantityMs;
        maxQ = Math.Max(maxQ, one.QuantityMs);
        maxP = Math.Max(maxP, one.PackMs);
        maxTotal = Math.Max(maxTotal, one.QuantityMs + one.PackMs);
        n++;
        if (one.Ok) ok++;
        if (one.Status == CpSolverStatus.Infeasible) infeas++;
    }
    Console.WriteLine($"[CP-SAT·全311单符文] 成功{ok}/{n} 不可行{infeas} 平均数量{totalQ / Math.Max(1, n)}ms 最慢数量{maxQ}ms 最慢装填{maxP}ms 最慢合计{maxTotal}ms");
}

// 6. 用真实库存跑飞剑5（品阶门槛 vs CP-SAT）
if (Environment.GetEnvironmentVariable("INV") != null)
{
    Console.WriteLine("===== 真实库存·飞剑5 对比 =====");
    var invPath = Path.Combine(dir, "inventory.json");
    var inv = JsonDocument.Parse(File.ReadAllText(invPath)).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
    var invMats = mats.Where(m => m.TypeId == 361).Select(m => Copy(m, inv.TryGetValue(m.Id, out var c) ? c : 0)).ToList();
    var flyIds = new[] { "216", "266", "223", "234", "307", "143", "105" };
    var flyRunes = allRunes.Where(r => flyIds.Contains(r.Id)).ToList();
    var fr = new EquipmentRequest { Materials = invMats, Runes = flyRunes, Yang = new Grid(10, 10), Yin = new Grid(9, 10), Mode = EquipmentMode.LowGrade, TimeLimitMs = 3000, TopK = 1 };
    var fres = new EquipmentSolver(fr).Solve();
    Console.WriteLine($"[品阶门槛·真实库存] 解 {fres.Solutions.Count} 个，{fres.ElapsedMs}ms，{(fres.Message ?? "")}");
    foreach (var s in fres.Solutions)
    {
        Console.WriteLine($"  {s.PieceCount}件 价{s.Price} 等级[{string.Join(",", s.Runes.Level)}] 复核 {(EquipmentSolver.Verify(fr, s) ?? "OK")}");
        foreach (var g in s.Placements.GroupBy(p => p.Material).OrderByDescending(g => g.Key.Price))
        {
            Console.WriteLine($"    {g.Key.Id} 价{g.Key.Price} attrs=[{string.Join(",", g.Key.Attrs)}] ×{g.Count()}");
        }
    }
    var cp = CpSatSolver.Solve(invMats, flyRunes, new int[Attr.Count], new Grid(10, 10), new Grid(9, 10), 10.0);
    Console.WriteLine($"[CP-SAT·真实库存] {cp.Message} 价{cp.TotalPrice} 件{cp.PieceCount} 数量{cp.QuantityMs}ms");
    foreach (var g in cp.Items.GroupBy(x => x.mat).OrderByDescending(g => g.Key.Price))
    {
        Console.WriteLine($"    {g.Key.Id} 价{g.Key.Price} attrs=[{string.Join(",", g.Key.Attrs)}] ×{g.Count()}");
    }
}

// 7. P1 修复反例测试
if (Environment.GetEnvironmentVariable("P1FIX") != null)
{
    Console.WriteLine("===== P1 修复反例 =====");
    Material SM(string id, int[] attrs, int grade, int price) => new Material { Id = id, Name = id, Attrs = attrs, Shape = new List<Cell> { new Cell(0, 0) }, Stock = 99, Grade = grade, Price = price };
    // P1-1: 仅金+1材料，符文需金1，指定火 → 应无解（不能把金当火）
    var r1 = new List<Rune> { new Rune { Id = "r", Name = "r", Requirement = new[] { 1, 0, 0, 0, 0, 0, 0, 0 }, MaxStacks = 1 } };
    var q1 = new EquipmentRequest { Materials = new List<Material> { SM("金", new[] { 1, 0, 0, 0, 0, 0, 0, 0 }, 1, 1) }, Runes = r1, Yang = new Grid(3, 3), Yin = new Grid(3, 3), Mode = EquipmentMode.MaxAttr, TargetAttr = 3, TimeLimitMs = 2000, TopK = 1 };
    var s1 = new EquipmentSolver(q1).Solve();
    Console.WriteLine($"[P1-1 MaxAttr指定火无火] 解{s1.Solutions.Count}个 {(s1.Solutions.Count == 0 ? "✓ 正确无解" : "✗ 错误返回金")}");
    // P1-2: 阳2格，金3水3上限10，L(阶1金3水3)/G(阶2金15)/W(阶2水15) → 应选 L 一件
    var r2 = new List<Rune> { new Rune { Id = "r", Name = "r", Requirement = new[] { 3, 3, 0, 0, 0, 0, 0, 0 }, MaxStacks = 10 } };
    var q2 = new EquipmentRequest { Materials = new List<Material> { SM("L", new[] { 3, 3, 0, 0, 0, 0, 0, 0 }, 1, 1), SM("G", new[] { 15, 0, 0, 0, 0, 0, 0, 0 }, 2, 1), SM("W", new[] { 0, 15, 0, 0, 0, 0, 0, 0 }, 2, 1) }, Runes = r2, Yang = new Grid(2, 1), Yin = new Grid(1, 1), Mode = EquipmentMode.MinPieces, TimeLimitMs = 2000, TopK = 1 };
    var s2 = new EquipmentSolver(q2).Solve();
    var b2 = s2.Best;
    Console.WriteLine($"[P1-2 最少材料一层解] 件数{(b2 != null ? b2.PieceCount : 0)} {(b2 != null && b2.PieceCount == 1 ? "✓ 1件正确" : "✗ 未找到1件解")}");
    // P1-3: 指定符文优先，优先金+普通火各需1上限3，材料 A(金2火1阶2价10)/B(金1火2阶1价100)；优先金等级不应低于火
    var r3 = new List<Rune> { new Rune { Id = "金", Name = "金", Requirement = new[] { 1, 0, 0, 0, 0, 0, 0, 0 }, MaxStacks = 3 }, new Rune { Id = "火", Name = "火", Requirement = new[] { 0, 0, 0, 1, 0, 0, 0, 0 }, MaxStacks = 3 } };
    var q3 = new EquipmentRequest { Materials = new List<Material> { SM("A", new[] { 2, 0, 0, 1, 0, 0, 0, 0 }, 2, 10), SM("B", new[] { 1, 0, 0, 2, 0, 0, 0, 0 }, 1, 100) }, Runes = r3, Yang = new Grid(3, 3), Yin = new Grid(3, 3), Mode = EquipmentMode.MaxRuneLevel, Priority = new[] { true, false }, TimeLimitMs = 2000, TopK = 1 };
    var s3 = new EquipmentSolver(q3).Solve();
    var b3 = s3.Best;
    if (b3 != null) { int g = b3.Runes.Level[0], f = b3.Runes.Level[1]; Console.WriteLine($"[P1-3 指定符文优先] 金{g} 火{f} {(g >= f ? "✓ 优先不低" : "✗ 优先被破坏")}"); }
}

// 通用炼器测试集：不同符文数量 × 不同需求量级（低/中/高），所有对比/回归测试共用
(string name, string[] runeIds)[] EquipmentTestCases() => new[]
{
    ("单符文·低阶", new[] { "1" }),
    ("单符文·中阶", new[] { "85" }),
    ("单符文·高阶", new[] { "307" }),
    ("双符文·低+高", new[] { "1", "307" }),
    ("三符文·低中高", new[] { "1", "85", "307" }),
    ("五符文·混合", new[] { "1", "85", "216", "223", "307" }),
    ("七符文·飞剑5", new[] { "216", "266", "223", "234", "307", "143", "105" }),
    ("十符文·混合", new[] { "1", "85", "105", "143", "216", "220", "223", "234", "266", "307" }),
};

// 8. 对比：最少材料 启发式 vs CP-SAT（通用测试集，含单/多符文）
if (Environment.GetEnvironmentVariable("CMP") != null)
{
    var m361 = mats.Where(m => m.TypeId == 361).Select(m => Copy(m, 99)).ToList();
    Console.WriteLine("===== 对比·最少材料（通用测试集）=====");
    Console.WriteLine($"{"用例",-14} {"符文",-3} {"启发式件数",-7} {"启发式ms",-7} {"CP件数",-5} {"CP装填"}");
    foreach (var (name, ids) in EquipmentTestCases())
    {
        var runes = allRunes.Where(r => ids.Contains(r.Id)).ToList();
        var req = new EquipmentRequest { Materials = m361, Runes = runes, Yang = new Grid(10, 10), Yin = new Grid(9, 10), Mode = EquipmentMode.MinPieces, TimeLimitMs = 3000, TopK = 1 };
        var h = new EquipmentSolver(req).Solve();
        int hp = h.Best?.PieceCount ?? -1;
        var cpItems = CpSatQuantity.Solve(m361, runes, new int[Attr.Count], new Grid(10, 10), new Grid(9, 10), 10.0, null, minPieces: true);
        int cp = cpItems?.Count ?? -1;
        string cpPack = cpItems == null ? "无解" : (CpSatSolver.PackItems(m361, new Grid(10, 10), new Grid(9, 10), cpItems, 500) == null ? "装不下" : "OK");
        Console.WriteLine($"{name,-14} {runes.Count,-3} {hp,-7} {h.ElapsedMs,-7} {cp,-5} {cpPack}");
    }
}

// 9. 对比：最高品阶/指定属性最大/指定符文优先（等级建模 CP-SAT vs 启发式）
if (Environment.GetEnvironmentVariable("CMP2") != null)
{
    var m361 = mats.Where(m => m.TypeId == 361).Select(m => Copy(m, 99)).ToList();
    long ObjOf(EquipmentSolution s, EquipmentMode m, int ta, bool[] pri)
    {
        if (s == null || s.Runes == null)
        {
            return -1;
        }
        if (m == EquipmentMode.MaxGrade) return s.Runes.GradeScore;
        if (m == EquipmentMode.MaxAttr) return s.Runes.FinalAttrs[ta];
        long p = 0;
        for (int i = 0; i < s.Runes.Level.Length; i++) if (pri != null && i < pri.Length && pri[i]) p += s.Runes.Level[i];
        return p;
    }
    foreach (var mode in new[] { EquipmentMode.MaxGrade, EquipmentMode.MaxAttr, EquipmentMode.MaxRuneLevel })
    {
        int ta = 0;
        Console.WriteLine($"===== 对比·{mode} =====");
        Console.WriteLine($"{"用例",-14} {"符文",-3} {"启发式目标",-9} {"启发式ms",-7} {"CP目标",-7} {"CPms",-6} {"装填"}");
        foreach (var (name, ids) in EquipmentTestCases())
        {
            var runes = allRunes.Where(r => ids.Contains(r.Id)).ToList();
            var priority = Enumerable.Range(0, runes.Count).Select(i => i < (runes.Count + 1) / 2).ToArray();
            var req = new EquipmentRequest { Materials = m361, Runes = runes, Yang = new Grid(10, 10), Yin = new Grid(9, 10), Mode = mode, TimeLimitMs = 3000, TopK = 1, TargetAttr = ta, Priority = priority };
            var h = new EquipmentSolver(req).Solve();
            long hObj = ObjOf(h.Best, mode, ta, priority);
            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            var cpItems = CpSatQuantity.SolveLeveled(m361, runes, new int[Attr.Count], new Grid(10, 10), new Grid(9, 10), 10.0, mode, ta, priority);
            sw.Stop();
            long cpObj = -1;
            string cpPack = "无解";
            if (cpItems != null)
            {
                var sol = CpSatSolver.PackItems(m361, new Grid(10, 10), new Grid(9, 10), cpItems, 500);
                if (sol == null)
                {
                    cpPack = "装不下";
                }
                else
                {
                    cpPack = "OK";
                    var eval = new RuneEvaluator(runes, new int[Attr.Count]);
                    var rr = eval.Evaluate(sol.Placements);
                    if (mode == EquipmentMode.MaxGrade) cpObj = rr.GradeScore;
                    else if (mode == EquipmentMode.MaxAttr) cpObj = rr.FinalAttrs[ta];
                    else { long p = 0; for (int i = 0; i < rr.Level.Length; i++) if (priority[i]) p += rr.Level[i]; cpObj = p; }
                }
            }
            Console.WriteLine($"{name,-14} {runes.Count,-3} {hObj,-9} {h.ElapsedMs,-7} {cpObj,-7} {sw.ElapsedMilliseconds,-6} {cpPack}");
        }
    }
}

Console.WriteLine(failures == 0 ? "复核全部通过" : $"复核失败 {failures} 个");
return failures == 0 ? 0 : 1;
