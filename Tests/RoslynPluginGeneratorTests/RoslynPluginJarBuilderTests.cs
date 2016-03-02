//-----------------------------------------------------------------------
// <copyright file="RoslynPluginJarBuilderTests.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SonarQube.Plugins.Test.Common;
using System.IO;

namespace SonarQube.Plugins.Roslyn.RoslynPluginGeneratorTests
{
    [TestClass]
    public class RoslynPluginJarBuilderTests
    {
        public TestContext TestContext { get; set; }

        #region Tests

        [TestMethod]
        public void RoslynPlugin_Test()
        {
            // Arrange
            string testDir = TestUtils.CreateTestDirectory(this.TestContext);
            string workingDir = TestUtils.CreateTestDirectory(this.TestContext, ".working");
            string outputJarFilePath = Path.Combine(testDir, "created.jar");

            string dummyRulesFile = TestUtils.CreateTextFile("rules.txt", testDir, "<rules />");
            string dummySqaleFile = TestUtils.CreateTextFile("sqale.txt", testDir, "<sqale />");
            string dummyZipFile = TestUtils.CreateTextFile("payload.txt", testDir, "zip");

            RoslynPluginDefinition defn = new RoslynPluginDefinition();
            defn.Manifest = new PluginManifest()
            {
                Description = "description",
                Name = "name"
            };

            defn.PackageId = "package.id";
            defn.PackageVersion = "1.0.0";
            defn.Language = "cs";
            defn.RulesFilePath = dummyRulesFile;
            defn.SqaleFilePath = dummySqaleFile;
            defn.StaticResourceName = "static\\foo.zip";
            defn.SourceZipFilePath = dummyZipFile;

            RoslynPluginJarBuilder builder = new RoslynPluginJarBuilder(new TestLogger());

            // Act
            builder.BuildJar(defn, workingDir, outputJarFilePath);

            // Assert
            ZipFileChecker checker = new ZipFileChecker(this.TestContext, outputJarFilePath);

            checker.AssertZipContainsFiles(
                "META-INF\\MANIFEST.MF",
                "static\\foo.zip",
                "org\\sonar\\plugins\\roslynsdk\\configuration.xml",
                "org\\sonar\\plugins\\roslynsdk\\sqale.xml",
                "org\\sonar\\plugins\\roslynsdk\\rules.xml"
                );
        }

        #endregion

    }
}
