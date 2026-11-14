using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SightoHear.Services
{
    /// <summary>
    /// ★ Native AOT 安全的 JsonNode 辅助方法。
    ///
    /// 背景：裁剪 / AOT 后 <c>JsonSerializer.IsReflectionEnabledByDefault == false</c>，
    /// 此时 <c>JsonArray.Add(T)</c> / <c>JsonObject[..] = T</c>（泛型重载）内部会调用
    /// <c>JsonNode.ConvertFromValue&lt;T&gt;</c> → <c>JsonSerializerOptions.Default</c>，
    /// 而 Default 在 AOT 中解析为 EmptyJsonTypeInfoResolver，于是抛：
    ///   NotSupportedException: JsonTypeInfo metadata for type 'System.String' was not provided...
    /// 非泛型的 <c>JsonValue.Create(...)</c> 不走这条路径，AOT 下可正常工作。
    ///
    /// 因此本项目所有运行时构造 JSON 节点的地方统一走本类，禁止再用 <c>JsonArray.Add("str")</c>。
    /// </summary>
    internal static class JsonNodeHelper
    {
        /// <summary>
        /// 读写 JsonNode 时使用的序列化选项：绑定源生成上下文，AOT 下零反射。
        /// （JsonNode.ToJsonString 若不带 TypeInfoResolver，AOT 下同样会失败。）
        /// </summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            TypeInfoResolver = AppJsonContext.Default
        };

        /// <summary>以 AOT 安全的方式向 <see cref="JsonArray"/> 追加一个字符串。</summary>
        public static void AddString(JsonArray array, string? value)
        {
            if (value is null)
            {
                array.Add((JsonNode?)null);
                return;
            }

            // ★ 必须显式转型为 JsonNode 再 Add。
            // JsonArray 有两个重载：Add(JsonNode?) 与 Add<T>(T)。
            // JsonValue.Create 返回的静态类型是 JsonValue?，直接传参会命中泛型重载
            // Add<JsonValue>(...)——它在 AOT 下仍会走 ConvertFromValue → JsonSerializerOptions.Default，
            // 抛 NotSupportedException（构建期表现为 IL2026 + IL3050 两条警告）。
            // 转成 JsonNode? 后命中非泛型重载，AOT 安全。
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        /// <summary>以 AOT 安全的方式向 <see cref="JsonObject"/> 写入一个字符串数组属性。</summary>
        public static void SetStringArray(JsonObject node, string propertyName, System.Collections.Generic.IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (string value in values)
                AddString(array, value);
            node[propertyName] = array;
        }

        /// <summary>把节点序列化为 JSON 文本（AOT 安全）。</summary>
        public static string ToJson(JsonNode? node)
            => node?.ToJsonString(Options) ?? "null";
    }
}
