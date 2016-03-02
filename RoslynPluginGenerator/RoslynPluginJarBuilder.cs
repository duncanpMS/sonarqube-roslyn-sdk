//-----------------------------------------------------------------------
// <copyright file="RoslynPluginJarBuilder.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------
using SonarQube.Plugins.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SonarQube.Plugins.Roslyn
{
    public class RoslynPluginJarBuilder
    {
        private const string EmptyTemplateJarResourceName = "SonarQube.Plugins.Roslyn.Resources.sonar-roslyn-sdk-template-plugin-1.0-empty.jar";

        private const string RelativeManifestResourcePath = "META-INF\\MANIFEST.MF";
        private const string RelativeConfigurationResourcePath = "org\\sonar\\plugins\\roslynsdk\\configuration.xml";

        private const string RelativeRulesXmlResourcePath = "org\\sonar\\plugins\\roslynsdk\\rules.xml";
        private const string RelativeSqaleXmlResourcePath = "org\\sonar\\plugins\\roslynsdk\\sqale.xml";

        private const string PluginClassName = "org.sonar.plugins.roslynsdk.RoslynSdkGeneratedPlugin";



        private readonly ILogger logger;

        #region Public methods

        public RoslynPluginJarBuilder(ILogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            this.logger = logger;
        }

        public void BuildJar(RoslynPluginDefinition definition, string workingDirectory, string outputFilePath)
        {
            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new ArgumentNullException("workingDirectory");
            }
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentNullException("outputFilePath");
            }

            // Create the config and manifest files
            string configFilePath = BuildConfigFile(definition, workingDirectory);

            string manifestFilePath = Path.Combine(workingDirectory, "manifest.txt");

            //TODO: decide where to set this
            definition.Manifest.Class = PluginClassName;
            definition.Manifest.Save(manifestFilePath);

            // Update the jar
            string templateJarFilePath = ExtractTemplateJarFile(workingDirectory);
            ArchiveUpdater updater = new ArchiveUpdater(workingDirectory, this.logger);

            updater.SetInputArchive(templateJarFilePath)
                .SetOutputArchive(outputFilePath)
                .AddFile(manifestFilePath, RelativeManifestResourcePath)
                .AddFile(configFilePath, RelativeConfigurationResourcePath)
                .AddFile(definition.RulesFilePath, RelativeRulesXmlResourcePath)
                .AddFile(definition.SourceZipFilePath, definition.StaticResourceName);

            if (!string.IsNullOrWhiteSpace(definition.SqaleFilePath))
            {
                updater.AddFile(definition.SqaleFilePath, RelativeSqaleXmlResourcePath);
            }

            updater.UpdateArchive();
        }

        #endregion

        #region Private methods

        private static string ExtractTemplateJarFile(string workingDirectory)
        {
            string templateJarFilePath = Path.Combine(workingDirectory, "template.jar");

            using (Stream resourceStream = typeof(RoslynPluginJarBuilder).Assembly.GetManifestResourceStream(EmptyTemplateJarResourceName))
            {
                using (FileStream file = new FileStream(templateJarFilePath, FileMode.CreateNew, FileAccess.Write))
                {
                    resourceStream.CopyTo(file);
                    file.Flush();
                }
            }

            return templateJarFilePath;
        }

        private string BuildConfigFile(RoslynPluginDefinition definition, string workingDirectory)
        {
            string configFilePath = Path.Combine(workingDirectory, "config.xml");

            RoslynSdkConfiguration config = new RoslynSdkConfiguration();

            // TODO:
            config.RepositoryKey = "TODO";
            config.RepositoryName = "TODO";
            config.RepositoryLanguage = definition.Language;
            config.RulesXmlResourcePath = RelativeRulesXmlResourcePath;
            config.SqaleXmlResourcePath = RelativeSqaleXmlResourcePath;

            // TODO: add properties

            config.Save(configFilePath);
            return configFilePath;
        }

        #endregion

    }
}
