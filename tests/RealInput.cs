using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ZaohuaAutoCraft.Solver;

namespace SolverTests
{
    /// <summary>
    /// 真实存档输入加载器。
    ///
    /// 数据由审查方从游戏存档提取（input.json），结构：
    ///   materials: [{ id, stock, attrs:{gold:2, wood:-1, ...}, shape:[[x,y],...], grade, price, element }]
    ///   runes:     [{ id, cap, requirements:{ice:100, wind:100, ...}, weight, defaultWeight }]
    ///   yang: [7,10]  yin: [7,10]  bonus: [0]*8  targetAttr: -1  priorityGroups: [...]
    ///
    /// 与 tests/Program.cs 里旧的 unpacked/data/inventory.json + 硬编码符文一一对应：
    /// 设置环境变量 REALINV=<input.json 路径> 时，SHOE / SHOE3 / PARALLELTEST 三个测试块改用这里的数据。
    /// 独立命名空间是必需的：Program.cs 是顶层语句（编译成隐式 Main 类），
    /// 同工程其它文件可以有自己的命名空间，测试里用 SolverTests.RealInput.XXX 全名调用。
    /// </summary>
    public static class RealInput
    {
        /// <summary>读取 material 属性字典（五行名 → 下标），未识别的键忽略（与 Attr.FromDic 一致）。</summary>
        static int[] ReadAttrs(JsonElement obj, string propertyName)
        {
            var attrs = new int[Attr.Count];
            if (!obj.TryGetProperty(propertyName, out var dic) || dic.ValueKind != JsonValueKind.Object)
            {
                return attrs;
            }
            foreach (var p in dic.EnumerateObject())
            {
                int i = Attr.IndexOf(p.Name);
                if (i >= 0)
                {
                    attrs[i] += p.Value.GetInt32();
                }
            }
            return attrs;
        }

        /// <summary>把 input.json 的 materials 转成求解器 Material（全部按 TypeId=361 的炼器材料处理）。</summary>
        public static List<Material> LoadMaterials(string inputJsonPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(inputJsonPath));
            var result = new List<Material>();
            foreach (var e in doc.RootElement.GetProperty("materials").EnumerateArray())
            {
                string id = e.GetProperty("id").ToString();
                var shape = new List<Cell>();
                if (e.TryGetProperty("shape", out var shapeJson) && shapeJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in shapeJson.EnumerateArray())
                    {
                        if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2)
                        {
                            shape.Add(new Cell(pair[0].GetInt32(), pair[1].GetInt32()));
                        }
                    }
                }
                result.Add(new Material
                {
                    Id = id,
                    Name = id,
                    Stock = e.TryGetProperty("stock", out var st) ? st.GetInt64() : 0,
                    Attrs = ReadAttrs(e, "attrs"),
                    Shape = shape,
                    Grade = e.TryGetProperty("grade", out var gr) ? gr.GetInt32() : 0,
                    Price = e.TryGetProperty("price", out var pr) ? pr.GetInt32() : 0,
                    Element = e.TryGetProperty("element", out var el) ? el.GetInt32() : 0,
                    TypeId = 361,
                });
            }
            return result;
        }

        /// <summary>
        /// 把 input.json 的 runes 转成求解器 Rune。
        /// 需求字典的键是五行名（gold/water/wood/fire/soil/ice/wind/thunder），用 Attr.IndexOf 转下标；
        /// MaxStacks = cap。Weight/DefaultWeight 映射规则未要求（这三个测试块只比等级/批次），保持默认 0。
        /// </summary>
        public static List<Rune> LoadRunes(string inputJsonPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(inputJsonPath));
            var result = new List<Rune>();
            foreach (var e in doc.RootElement.GetProperty("runes").EnumerateArray())
            {
                string id = e.GetProperty("id").ToString();
                result.Add(new Rune
                {
                    Id = id,
                    Name = id,
                    MaxStacks = e.TryGetProperty("cap", out var cap) ? cap.GetInt32() : 0,
                    Requirement = ReadAttrs(e, "requirements"),
                });
            }
            return result;
        }
    }
}
