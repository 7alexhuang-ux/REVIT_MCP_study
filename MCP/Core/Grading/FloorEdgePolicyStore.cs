#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;

namespace RevitMCP.Core.Grading
{
    /// <summary>邊坡率政策的持久化形（JSON）；座標/長度為 Revit 內部英呎，SlopeRatio 為 "1:n" 字串。</summary>
    public sealed class StoredEdgePolicy
    {
        public double MidX { get; set; }
        public double MidY { get; set; }
        public double DirX { get; set; }
        public double DirY { get; set; }
        public double Length { get; set; }
        public string SlopeRatio { get; set; }
    }

    /// <summary>
    /// 樓板邊坡率政策存取：政策屬於「板」（板的設計屬性），存於樓板的 Extensible Storage，
    /// 整地（slope_transition）時消費並快照進方案登記簿。
    /// </summary>
    public static class FloorEdgePolicyStore
    {
        private static readonly Guid PolicySchemaGuid =
            new Guid("A6C31D84-52E7-4B9F-8D15-7E2A94C6F310");

        private const int CurrentSchemaVersion = 1;

        public static IReadOnlyList<StoredEdgePolicy> Read(Floor floor)
        {
            if (floor == null)
            {
                return Array.Empty<StoredEdgePolicy>();
            }

            var schema = Schema.Lookup(PolicySchemaGuid);
            if (schema == null)
            {
                return Array.Empty<StoredEdgePolicy>();
            }

            var entity = floor.GetEntity(schema);
            if (entity == null || !entity.IsValid())
            {
                return Array.Empty<StoredEdgePolicy>();
            }

            var json = entity.Get<string>("PoliciesJson");
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<StoredEdgePolicy>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<StoredEdgePolicy>>(json)
                    ?? (IReadOnlyList<StoredEdgePolicy>)Array.Empty<StoredEdgePolicy>();
            }
            catch (JsonException)
            {
                return Array.Empty<StoredEdgePolicy>();
            }
        }

        /// <summary>覆寫政策清單（須在交易內）。空清單＝清除。</summary>
        public static void Write(Floor floor, IReadOnlyList<StoredEdgePolicy> policies)
        {
            if (floor == null)
            {
                throw new ArgumentNullException(nameof(floor));
            }

            var schema = GetOrCreateSchema();
            if (policies == null || policies.Count == 0)
            {
                floor.DeleteEntity(schema);
                return;
            }

            var entity = new Entity(schema);
            entity.Set("SchemaVersion", CurrentSchemaVersion);
            entity.Set("PoliciesJson", JsonConvert.SerializeObject(policies));
            floor.SetEntity(entity);
        }

        /// <summary>轉為純幾何匹配輸入（RunPerRise 由 "1:n" 解析）。</summary>
        public static IReadOnlyList<EdgeSlopePolicy> ToGeometryPolicies(IReadOnlyList<StoredEdgePolicy> stored)
        {
            if (stored == null || stored.Count == 0)
            {
                return Array.Empty<EdgeSlopePolicy>();
            }

            return stored
                .Select(policy => new EdgeSlopePolicy(
                    policy.MidX, policy.MidY, policy.DirX, policy.DirY, policy.Length,
                    TransitionSettings.ParseSlopeRatio(policy.SlopeRatio)))
                .ToList();
        }

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(PolicySchemaGuid);
            if (schema != null)
            {
                if (schema.GetField("SchemaVersion") == null || schema.GetField("PoliciesJson") == null)
                {
                    throw new InvalidOperationException("既有 RevitMCP_EdgeSlopePolicies schema 欄位不相容。");
                }

                return schema;
            }

            var builder = new SchemaBuilder(PolicySchemaGuid);
            builder.SetSchemaName("RevitMCP_EdgeSlopePolicies");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("SchemaVersion", typeof(int));
            builder.AddSimpleField("PoliciesJson", typeof(string));
            return builder.Finish();
        }
    }
}
#endif
