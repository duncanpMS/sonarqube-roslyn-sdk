//-----------------------------------------------------------------------
// <copyright file="ArchiveUpdater.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using SonarQube.Plugins.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SonarQube.Plugins.Roslyn
{
    /// <summary>
    /// Updates an existing archive (zip, jar) by inserting additional files
    /// </summary>
    public class ArchiveUpdater
    {
        private readonly string workingDirectory;
        private readonly IDictionary<string, string> fileMap;
        private readonly ILogger logger;

        private string inputArchiveFilePath;
        private string outputArchiveFilePath;

        #region Public methods

        public ArchiveUpdater(string workingDirectory, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new ArgumentNullException("workingDirectory");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            this.logger = logger;
            this.workingDirectory = workingDirectory;

            this.fileMap = new Dictionary<string, string>();
        }

        public ArchiveUpdater SetInputArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentNullException("filePath");
            }
            this.inputArchiveFilePath = filePath;
            return this;
        }

        public ArchiveUpdater SetOutputArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentNullException("filePath");
            }
            this.outputArchiveFilePath = filePath;
            return this;
        }

        public ArchiveUpdater AddFile(string sourceFilePath, string relativeTargetFilePath)
        {
            this.fileMap[relativeTargetFilePath] = sourceFilePath;
            return this;
        }

        public void UpdateArchive()
        {
            string unpackedDir = Utilities.CreateSubDirectory(this.workingDirectory, "unpacked");
            ZipFile.ExtractToDirectory(this.inputArchiveFilePath, unpackedDir);

            // Add in the new files
            foreach (KeyValuePair<string, string> kvp in this.fileMap)
            {
                string targetFilePath = Path.Combine(unpackedDir, kvp.Key);

                Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath));
                File.Copy(kvp.Value, targetFilePath);
            }

            // Re-zip
            if (string.IsNullOrWhiteSpace(this.outputArchiveFilePath))
            {
                this.outputArchiveFilePath = this.inputArchiveFilePath;
            }

            ZipFile.CreateFromDirectory(unpackedDir, this.outputArchiveFilePath);
        }

        #endregion
    }
}
