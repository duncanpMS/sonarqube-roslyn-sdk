//-----------------------------------------------------------------------
// <copyright file="PluginInspector.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SonarQube.Common;
using SonarQube.Plugins.Common;
using SonarQube.Plugins.Maven;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SonarQube.Plugins.IntegrationTests
{
    /// <summary>
    /// Wrapper around a Java class that loads a plugin from a jar and extracts information
    /// from it. The plugin information is returned as an XML file
    /// </summary>
    internal class PluginInspector
    {
        private const string PluginInspectorFullClassName = "PluginInspector";

        private readonly IJdkWrapper jdkWrapper;

        private string inspectorClassFilePath;
        
        public PluginInspector()
        {
            this.jdkWrapper = new JdkWrapper();
        }

        public object GetPluginDescription(string jarFilePath, string tempDir, ILogger logger)
        {
            Assert.IsTrue(File.Exists(jarFilePath), "Jar file does not exist");

            this.Build(jarFilePath, tempDir, logger);

            string reportFilePath = this.RunPluginInspector(jarFilePath, tempDir, logger);

            return reportFilePath;
        }

        private void CheckJdkIsInstalled()
        {
            if (!this.jdkWrapper.IsJdkInstalled())
            {
                Assert.Inconclusive("Test requires the JDK to be installed");
            }
        }

        private void Build(string jarFilePath, string tempDir, ILogger logger)
        {
            Assert.IsTrue(File.Exists(jarFilePath), "Jar file does not exist");

            // Get the java source files
            string srcDir = CreateSubDir(tempDir, "src");
            string outDir = CreateSubDir(tempDir, "out");
            string xxxDir = CreateSubDir(tempDir, "xxx");

            SourceGenerator.CreateSourceFiles(this.GetType().Assembly,
                "SonarQube.Plugins.IntegrationTests.Roslyn.Resources",
                srcDir,
                new Dictionary<string, string>());

            JavaCompilationBuilder builder = new JavaCompilationBuilder(this.jdkWrapper);
            foreach (string source in Directory.GetFiles(srcDir, "*.java", SearchOption.AllDirectories))
            {
                builder.AddSources(source);
            }

            // Add the jars required to compile the Java code
            IEnumerable<string> jarFiles = GetCompileDependencies(logger);

            foreach (string jar in jarFiles)
            {
                builder.AddClassPath(jar);
            }

            bool result = builder.Compile(xxxDir, outDir, logger);

            if (!result)
            {
                Assert.Inconclusive("Test setup error: failed to build the Java inspector");
            }

            this.inspectorClassFilePath = GetPluginInspectorClassFilePath(outDir);
        }

        private static IEnumerable<string> GetCompileDependencies(ILogger logger)
        {
            MavenDependencyHandler mavenHandler = new MavenDependencyHandler(logger);
            IEnumerable<string> jarFiles = mavenHandler.GetJarFiles(new MavenCoordinate("org.codehaus.sonar", "sonar-plugin-api", "4.5.2"), false);
            return jarFiles;
        }

        private static IEnumerable<string> GetRuntimeDependencies(ILogger logger)
        {
            MavenDependencyHandler mavenHandler = new MavenDependencyHandler(logger);
            IEnumerable<string> jarFiles = mavenHandler.GetJarFiles(new MavenCoordinate("org.codehaus.sonar", "sonar-plugin-api", "4.5.2"), true);
            return jarFiles;
        }

        private static string CreateSubDir(string rootDir, string subDirName)
        {
            string fullName = Path.Combine(rootDir, subDirName);
            Directory.CreateDirectory(fullName);
            return fullName;
        }

        private static string GetPluginInspectorClassFilePath(string rootDir)
        {
            IEnumerable<string> classFilePaths = Directory.GetFiles(rootDir, "*.class");

            Assert.AreEqual(1, classFilePaths.Count());
            return classFilePaths.First();
        }

        private string RunPluginInspector(string jarFilePath, string tempDir, ILogger logger)
        {
            Debug.Assert(!string.IsNullOrEmpty(this.inspectorClassFilePath));

            string reportFilePath = Path.Combine(tempDir, "report.xml");

            IEnumerable<string> jarFiles = GetRuntimeDependencies(logger);

            // Construct the class path argument
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("-cp \"{0}\"", Path.GetDirectoryName(this.inspectorClassFilePath));
            foreach (string dependencyFilePath in jarFiles)
            {
                sb.AppendFormat(";{0}", dependencyFilePath);
            }

            IList<string> cmdLineArgs = new List<string>();
            cmdLineArgs.Add(sb.ToString()); // options must preceed the name of the class to execute
            cmdLineArgs.Add(PluginInspectorFullClassName);
            cmdLineArgs.Add(jarFilePath); // parameter(s) to pass to the class
            cmdLineArgs.Add(reportFilePath);

            ProcessRunnerArguments runnerArgs = new ProcessRunnerArguments(GetJavaExePath(), logger);
            runnerArgs.CmdLineArgs = cmdLineArgs;

            ProcessRunner runner = new ProcessRunner();
            bool success = runner.Execute(runnerArgs);

            Assert.IsTrue(success, "Test error: failed to execute the PluginInspector");
            Assert.IsTrue(File.Exists(reportFilePath), "Test error: failed to create the PluginInspector report");
            return reportFilePath;
        }

        private static string GetJavaExePath()
        {
            string javaExeFilePath = Environment.GetEnvironmentVariable("JAVA_HOME");
            Assert.IsFalse(string.IsNullOrWhiteSpace(javaExeFilePath), "Test setup error: cannot locate java.exe because JAVA_HOME is not set");

            javaExeFilePath = Path.Combine(javaExeFilePath, "bin", "java.exe");
            Assert.IsTrue(File.Exists(javaExeFilePath), "Test setup error: failed to locate java.exe - does not exist at '{0}'", javaExeFilePath);
            return javaExeFilePath;
        }
    }
}
