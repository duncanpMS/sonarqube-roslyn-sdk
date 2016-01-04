//-----------------------------------------------------------------------
// <copyright file="RoslynGenTests.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SonarQube.Common;
using SonarQube.Plugins.Common;
using SonarQube.Plugins.Maven;
using SonarQube.Plugins.Test.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SonarQube.Plugins.IntegrationTests
{
    [TestClass]
    public class RoslynGenTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        //[Ignore] // WIP
        public void RoslynGen()
        {
            TestLogger logger = new TestLogger();

            string tempDir = TestUtils.CreateTestDirectory(this.TestContext, "pluginInsp");
            PluginInspector inspector = new PluginInspector();
            string jarFilePath = @"C:\Users\duncanp\Source\Repos\sonarqube-roslyn-sdk\RoslynPluginGenerator\bin\Debug\Wintellect.Analyzers-plugin.1.0.5.jar"; // TODO
            object description = inspector.GetPluginDescription(jarFilePath, tempDir, logger);

            Assert.IsNotNull(description);

            // Build the Java inspector class
            string testDir = TestUtils.CreateTestDirectory(this.TestContext);


            bool result = RunRoslynPluginGenerator(logger, "/a:Wintellect.Analyzer:1.0.4");
            Assert.IsTrue(result, "Roslyn generator exe did not complete successfully");

            jarFilePath = AssertPluginJarExists(testDir);

        }

        private static bool RunRoslynPluginGenerator(ILogger logger, params string[] args)
        {
            string exePath = typeof(Roslyn.AnalyzerPluginGenerator).Assembly.Location;
            ProcessRunner runner = new ProcessRunner();

            ProcessRunnerArguments prArgs = new ProcessRunnerArguments(exePath, logger);

            prArgs.CmdLineArgs = args;
            prArgs.WorkingDirectory = Path.GetDirectoryName(exePath);

            bool result = runner.Execute(prArgs);
            return result;
        }

        private static string AssertPluginJarExists(string rootDir)
        {
            // TODO
            return null;
        }
    }
}
