using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>
    /// 測試專用的 JSON Schema（draft-07 子集）驗證器：只實作 docs/schemas/bundle-manifest.schema.json 用到的關鍵字
    /// （type、const、enum、required、properties、additionalProperties、items、minItems、maxItems、minimum、minLength、pattern、$ref/definitions）。
    /// 遇到沒實作的關鍵字會丟例外，避免 schema 加了新規則卻被靜默略過。
    /// 另有 Node 端的 ajv 測試（tools/bundle-manifest.schema.test.mjs）用同一份 schema 與 fixture 交叉驗證。
    /// </summary>
    public sealed class MiniJsonSchema
    {
        private static readonly HashSet<string> Supported = new HashSet<string>
        {
            "$schema", "$id", "title", "description", "type", "const", "enum", "required", "properties", "additionalProperties",
            "items", "minItems", "maxItems", "minimum", "minLength", "pattern", "$ref", "definitions"
        };

        private readonly JObject _root;
        public MiniJsonSchema(JObject root) { _root = root; }

        public List<string> Validate(JToken instance)
        {
            var errors = new List<string>();
            Check(_root, instance, "$", errors);
            return errors;
        }

        private JObject Resolve(JObject schema)
        {
            var reference = (string)schema["$ref"];
            if (reference == null) return schema;
            const string prefix = "#/definitions/";
            if (!reference.StartsWith(prefix, StringComparison.Ordinal)) throw new NotSupportedException("$ref " + reference);
            return (JObject)_root["definitions"][reference.Substring(prefix.Length)];
        }

        private void Check(JObject schemaNode, JToken value, string path, List<string> errors)
        {
            var schema = Resolve(schemaNode);
            foreach (var property in schema.Properties())
            {
                if (!Supported.Contains(property.Name)) throw new NotSupportedException("schema 關鍵字未實作：" + property.Name);
            }

            if (schema["const"] != null && !JToken.DeepEquals(schema["const"], value)) errors.Add(path + " 必須等於 " + schema["const"]);
            if (schema["enum"] != null && !((JArray)schema["enum"]).Any(e => JToken.DeepEquals(e, value))) errors.Add(path + " 不在列舉內：" + value);

            var type = (string)schema["type"];
            if (type != null && !TypeMatches(type, value))
            {
                errors.Add(path + " 型別應為 " + type + "，實際 " + value.Type);
                return;
            }

            if (value.Type == JTokenType.String)
            {
                var text = (string)value;
                if (schema["minLength"] != null && text.Length < (int)schema["minLength"]) errors.Add(path + " 長度小於 " + schema["minLength"]);
                if (schema["pattern"] != null && !Regex.IsMatch(text, (string)schema["pattern"])) errors.Add(path + " 不符 pattern " + schema["pattern"]);
            }

            if ((value.Type == JTokenType.Integer || value.Type == JTokenType.Float) && schema["minimum"] != null && (double)value < (double)schema["minimum"])
                errors.Add(path + " 小於最小值 " + schema["minimum"]);

            var obj = value as JObject;
            if (obj != null)
            {
                foreach (var required in (schema["required"] as JArray ?? new JArray()).Select(r => (string)r))
                {
                    if (obj[required] == null) errors.Add(path + " 缺少必填欄位 " + required);
                }
                var properties = schema["properties"] as JObject;
                var additional = schema["additionalProperties"];
                foreach (var member in obj.Properties())
                {
                    var childSchema = properties == null ? null : properties[member.Name] as JObject;
                    if (childSchema != null) Check(childSchema, member.Value, path + "." + member.Name, errors);
                    else if (additional != null && additional.Type == JTokenType.Boolean && !(bool)additional) errors.Add(path + " 含未定義欄位 " + member.Name);
                }
            }

            var array = value as JArray;
            if (array != null)
            {
                if (schema["minItems"] != null && array.Count < (int)schema["minItems"]) errors.Add(path + " 項目數少於 " + schema["minItems"]);
                if (schema["maxItems"] != null && array.Count > (int)schema["maxItems"]) errors.Add(path + " 項目數多於 " + schema["maxItems"]);
                var items = schema["items"] as JObject;
                if (items != null)
                {
                    for (var i = 0; i < array.Count; i++) Check(items, array[i], path + "[" + i + "]", errors);
                }
            }
        }

        private static bool TypeMatches(string type, JToken value)
        {
            switch (type)
            {
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "integer": return value.Type == JTokenType.Integer;
                case "number": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
                default: throw new NotSupportedException("type " + type);
            }
        }
    }
}
