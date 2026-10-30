using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RevitMCP.Core.WindowFamily;

namespace MCP.Tests.WindowFamily
{
    /// <summary>
    /// 共用參數正本（C# 表）與磁碟上的 universal-window.txt 必須一致，且 GUID 凍結。
    /// </summary>
    [TestFixture]
    public class UniversalWindowParametersTests
    {
        private static string SharedParameterFilePath()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "MCP", "Resources", "shared-parameters", "universal-window.txt");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        [Test]
        public void SharedParameterFile_ExistsInRepo()
        {
            Assert.That(SharedParameterFilePath(), Is.Not.Null,
                "找不到 MCP/Resources/shared-parameters/universal-window.txt");
        }

        [Test]
        public void SharedParameterFile_MatchesCSharpSourceOfTruth()
        {
            string path = SharedParameterFilePath();
            Assert.That(path, Is.Not.Null);

            string onDisk = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            string generated = UniversalWindowParameters.BuildSharedParameterFileContent();

            Assert.That(Normalize(onDisk), Is.EqualTo(Normalize(generated)),
                "共用參數檔與 UniversalWindowParameters.cs 已漂移；請重新產生 txt。");
        }

        [Test]
        public void Guids_AreFrozenAndUnique()
        {
            var all = UniversalWindowParameters.All;

            Assert.That(all.Count, Is.EqualTo(18), "SPEC §7 共 18 個 UW_ 參數");
            Assert.That(all.Select(p => p.Guid).Distinct().Count(), Is.EqualTo(all.Count), "GUID 不可重複");
            Assert.That(all.Select(p => p.Name).Distinct().Count(), Is.EqualTo(all.Count), "參數名不可重複");
            Assert.That(all.All(p => p.Name.StartsWith("UW_")), Is.True, "全部以 UW_ 開頭");

            // 抽驗三個凍結值：改到就是換參數，明細表會斷
            Assert.That(all.Single(p => p.Name == UniversalWindowParameters.RowCount).Guid,
                        Is.EqualTo(new Guid("7a1e0001-9c4b-4f2a-9d3e-b0d500000001")));
            Assert.That(all.Single(p => p.Name == UniversalWindowParameters.DaylightArea).Guid,
                        Is.EqualTo(new Guid("7a1e0007-9c4b-4f2a-9d3e-b0d500000007")));
            Assert.That(all.Single(p => p.Name == UniversalWindowParameters.SolarFactor).Guid,
                        Is.EqualTo(new Guid("7a1e0012-9c4b-4f2a-9d3e-b0d500000012")));
        }

        [Test]
        public void DataTypes_MatchSpecSection7()
        {
            var byName = UniversalWindowParameters.All.ToDictionary(p => p.Name);

            Assert.That(byName[UniversalWindowParameters.SashCount].DataType, Is.EqualTo(SharedParamDataType.Integer));
            Assert.That(byName[UniversalWindowParameters.Operations].DataType, Is.EqualTo(SharedParamDataType.Text));
            Assert.That(byName[UniversalWindowParameters.GlassArea].DataType, Is.EqualTo(SharedParamDataType.Area));
            Assert.That(byName[UniversalWindowParameters.MoWidth].DataType, Is.EqualTo(SharedParamDataType.Length));
            Assert.That(byName[UniversalWindowParameters.WindLoad].DataType, Is.EqualTo(SharedParamDataType.Number));

            // 留白給使用者的欄位不由生成器填值
            Assert.That(byName[UniversalWindowParameters.GlassKind].FilledByGenerator, Is.False);
            Assert.That(byName[UniversalWindowParameters.FireRating].FilledByGenerator, Is.False);
            Assert.That(byName[UniversalWindowParameters.Uaf].FilledByGenerator, Is.False);
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").TrimEnd('\n');
        }
    }
}
