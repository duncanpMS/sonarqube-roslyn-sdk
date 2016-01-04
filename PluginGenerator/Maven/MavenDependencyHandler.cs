//-----------------------------------------------------------------------
// <copyright file="MavenDependencyHandler.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------
using SonarQube.Plugins.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace SonarQube.Plugins.Maven
{
    // TODO:
    // * parsing version ranges e.g. [1.0.0,)
    // * version conflict resolution
    // * checking for duplicates
    // * exclusions
    public class MavenDependencyHandler : IMavenDependencyHandler
    {
        private const string LocalMavenDirectory = ".maven";
        private const string POM_Extension = "pom";
        private const string JAR_Extension = "jar";

        private readonly string localCacheDirectory;
        private readonly ILogger logger;

        /// <summary>
        /// Maps coords to the corresponding POM
        /// </summary>
        private readonly Dictionary<MavenCoordinate, MavenPartialPOM> coordPomMap;

        public MavenDependencyHandler(ILogger logger)
            : this(null, logger)
        {
        }

        public MavenDependencyHandler(string localCacheDirectory, ILogger logger)
        {
            if (logger == null) { throw new ArgumentNullException("logger"); }

            this.localCacheDirectory = localCacheDirectory;
            this.logger = logger;

            if (string.IsNullOrWhiteSpace(this.localCacheDirectory))
            {
                this.localCacheDirectory = Utilities.CreateTempDirectory(LocalMavenDirectory);
            }

            this.coordPomMap = new Dictionary<MavenCoordinate, MavenPartialPOM>();
        }

        public string LocalCacheDirectory { get { return this.localCacheDirectory; } }

        #region IMavenDependencyHandler interface

        public IEnumerable<string> GetJarFilesOLD(MavenCoordinate coordinate, bool recurse)
        {
            if (coordinate == null) { throw new ArgumentNullException("coordinate"); }

            this.logger.LogDebug(MavenResources.MSG_ProcessingDependency, coordinate);

            List<string> jarFiles = new List<string>();
            List<MavenCoordinate> visited = new List<MavenCoordinate>(); // guard against recursion
            this.GetJarFilesOLD(coordinate, recurse, jarFiles, visited);

            return jarFiles;
        }

        public IEnumerable<string> GetJarFiles(MavenCoordinate coordinate, bool includeTransitive)
        {
            if (coordinate == null) { throw new ArgumentNullException("coordinate"); }

            this.logger.LogDebug(MavenResources.MSG_ProcessingDependency, coordinate);

            List<MavenCoordinate> visited = new List<MavenCoordinate>(); // guard against recursion
            List<MavenDependency> currentDependencies = new List<MavenDependency>();
            this.GetDependencies(coordinate, includeTransitive, currentDependencies, visited);

            IEnumerable<string> files = GetJarsForDependencies(currentDependencies);

            return files;
        }

        private IEnumerable<string> GetJarsForDependencies(IEnumerable<MavenDependency> dependencies)
        {
            List<string> files = new List<string>();

            foreach (MavenDependency dependency in dependencies)
            {
                MavenPartialPOM pom = TryGetPOM(dependency);
                if (pom != null)
                {
                    if (HasJar(pom))
                    {
                        string localJarFilePath = TryGetJar(dependency);

                        if (localJarFilePath != null)
                        {
                            if (files.Contains(localJarFilePath, StringComparer.OrdinalIgnoreCase))
                            {
                                this.logger.LogWarning(MavenResources.WARN_JarAddedByAnotherDependency, dependency, localJarFilePath);
                            }
                            else
                            {
                                files.Add(localJarFilePath);
                            }
                        }
                    }
                    else
                    {
                        this.logger.LogDebug(MavenResources.MSG_POMDoesNotContainAJar, pom, pom.Packaging);
                    }
                }
            }
            return files;
        }

        #endregion

        #region Private methods

        private void GetDependencies(MavenCoordinate resolvedCoordinate, bool includeTransitive, IList<MavenDependency> currentDependencies, List<MavenCoordinate> visited)
        {
            if (visited.Contains(resolvedCoordinate))
            {
                this.logger.LogDebug(MavenResources.MSG_DependencyAlreadyVisited, resolvedCoordinate);
                return;
            }
            visited.Add(resolvedCoordinate);

            Debug.Assert(!string.IsNullOrWhiteSpace(resolvedCoordinate.Version));

            MavenPartialPOM pom = this.TryGetPOM(resolvedCoordinate);
            if (pom == null)
            {
                return;
            }

            IEnumerable<MavenDependency> resolvedDependencies = GetResolvedProjectDependencies(pom);
            IEnumerable<MavenDependency> filtered = FilterDependenciesByScope(resolvedDependencies);

            MergeDependenciesUsingLatestVersion(filtered, currentDependencies);

            if (includeTransitive)
            {
                foreach (MavenDependency dependency in pom.Dependencies)
                {
                    MavenCoordinate resolved = GetResolvedCoordinate(dependency, pom);
                    if (resolved != null)
                    {
                        GetDependencies(resolved, true, currentDependencies, visited);
                    }
                }
            }
        }
        
        /// <summary>
        /// Returns all direct dependencies for the specified project that can be resolved, including
        /// inherited dependencies
        /// </summary>
        private IEnumerable<MavenDependency> GetResolvedProjectDependencies(MavenPartialPOM pom)
        {
            List<MavenDependency> allDependencies = new List<MavenDependency>();
            MavenPartialPOM current = pom;

            while (current != null)
            {
                this.logger.LogDebug(MavenResources.MSG_AddingDependenciesForPOM, pom);

                IEnumerable<MavenDependency> resolvedDependencies = GetResolvedDirectDependencies(current);

                AddInheritedDependencies(resolvedDependencies, allDependencies);

                if (current.Parent == null)
                {
                    current = null;
                }
                else
                {
                    MavenCoordinate resolvedCoordinate = GetResolvedCoordinate(current.Parent, pom);
                    if (resolvedCoordinate != null)
                    {
                        current = this.TryGetPOM(resolvedCoordinate);
                    }
                    else
                    {
                        current = null;
                    }
                }
            }

            return allDependencies;
        }

        private IEnumerable<MavenDependency> GetResolvedDirectDependencies(MavenPartialPOM pom)
        {
            List<MavenDependency> resolvedDependencies = new List<MavenDependency>();

            foreach (MavenDependency unresolved in pom.Dependencies)
            {
                MavenCoordinate resolved = GetResolvedCoordinate(unresolved, pom);
                if (resolved != null)
                {
                    // TODO: find a neater way of updating the identity data
                    unresolved.ArtifactId = resolved.ArtifactId;
                    unresolved.GroupId = resolved.GroupId;
                    unresolved.Version = resolved.Version;

                    resolvedDependencies.Add(unresolved);
                }
            }
            return resolvedDependencies;
        }

        private MavenCoordinate GetResolvedCoordinate(MavenCoordinate coordinate, MavenPartialPOM pom)
        {
            MavenDependency resolvedDependency = null;

            string resolvedGroupId = ExpandVariables(coordinate.GroupId, pom);
            string resolvedArtifactId = ExpandVariables(coordinate.ArtifactId, pom);
            string resolvedVersion = this.ResolveCoordinateVersion(resolvedGroupId, resolvedArtifactId, coordinate.Version, pom);

            if (resolvedVersion == null)
            {
                logger.LogWarning(MavenResources.WARN_FailedToResolveDependency, coordinate);
            }
            else
            {
                resolvedDependency = new MavenDependency(resolvedGroupId, resolvedArtifactId, resolvedVersion);
            }
            return resolvedDependency;
        }

        private IEnumerable<MavenDependency> FilterDependenciesByScope(IEnumerable<MavenDependency> dependencies)
        {
            List<MavenDependency> filtered = new List<MavenDependency>();

            foreach(MavenDependency dependency in dependencies)
            {
                if (ShouldIncludeDependency(dependency))
                {
                    filtered.Add(dependency);
                }
                else
                {
                    this.logger.LogDebug(MavenResources.MSG_SkippingScopedDependency, dependency, dependency.Scope);
                }
            }
            return filtered;
        }

        /// <summary>
        /// Merges the new dependencies into the current list.
        /// Conflict resolution: if an artifact exists in both lists the latest version is used
        /// </summary>
        private void MergeDependenciesUsingLatestVersion(IEnumerable<MavenDependency> newDependencies, IList<MavenDependency> currentDependencies)
        {
            foreach(MavenDependency newDependency in newDependencies)
            {
                MavenDependency existing = GetMatchingArtifact(currentDependencies, newDependency);
                if (existing == null)
                {
                    currentDependencies.Add(newDependency); // no conflict
                }
                else
                {
                    UseLatestDependency(currentDependencies, newDependency, existing);
                }
            }
        }

        private void UseLatestDependency(IList<MavenDependency> currentDependencies, MavenDependency newDependency, MavenDependency existingDependency)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(newDependency.Version));
            Debug.Assert(!string.IsNullOrWhiteSpace(existingDependency.Version));

            MavenDependency selected;

            if (newDependency.Equals(existingDependency))
            {
                this.logger.LogDebug("TODO: dependency already included");
            }
            else
            {
                //TODO: proper version comparison
                if (existingDependency.Version.CompareTo(newDependency.Version) > 0)
                {
                    selected = existingDependency;
                }
                else
                {
                    currentDependencies.Remove(existingDependency);
                    currentDependencies.Add(newDependency);
                    selected = newDependency;
                }
                this.logger.LogDebug("Resolving version conflict. Options: {0}, {1}. Selected: {2}",
                    newDependency, existingDependency, selected);
            }
        }

        private void GetJarFilesOLD(MavenCoordinate coordinate, bool includeDependencies, List<string> files, List<MavenCoordinate> visited)
        {
            if (visited.Contains(coordinate))
            {
                this.logger.LogDebug(MavenResources.MSG_DependencyAlreadyVisited, coordinate);
                return;
            }
            visited.Add(coordinate);

            Debug.Assert(!string.IsNullOrWhiteSpace(coordinate.Version));

            MavenPartialPOM pom = this.TryGetPOM(coordinate);
            if (pom == null)
            {
                return; // failed to retrieve the POM for the artifact
            }

            if (HasJar(pom))
            {
                string localJarFilePath = TryGetJar(coordinate);

                if (localJarFilePath != null)
                {
                    if (files.Contains(localJarFilePath, StringComparer.OrdinalIgnoreCase))
                    {
                        this.logger.LogWarning(MavenResources.WARN_JarAddedByAnotherDependency, coordinate, localJarFilePath);
                    }
                    else
                    {
                        files.Add(localJarFilePath);
                    }
                }
            }
            else
            {
                this.logger.LogDebug(MavenResources.MSG_POMDoesNotContainAJar, pom, pom.Packaging);
            }

            if (includeDependencies)
            {
                FetchDependencies(files, visited, pom);
            }
        }

        private static bool HasJar(MavenPartialPOM pom)
        {
            return (pom.Packaging == "jar" || pom.Packaging == null || pom.Packaging == "bundle");
        }

        private void FetchDependencies(List<string> files, List<MavenCoordinate> visited, MavenPartialPOM pom)
        {
            foreach (MavenDependency dependency in this.GetCurrentProjectDependencies(pom))
            {
                if (ShouldIncludeDependency(dependency))
                {
                    string resolvedVersion = this.ResolveCoordinateVersionOLD(dependency, pom);

                    if (resolvedVersion == null)
                    {
                        logger.LogWarning(MavenResources.WARN_FailedToResolveDependency, dependency);
                    }
                    else
                    {
                        MavenDependency resolvedDependency = new MavenDependency(dependency.GroupId, dependency.ArtifactId, resolvedVersion);
                        this.GetJarFilesOLD(resolvedDependency, true, files, visited);
                    }
                }
                else
                {
                    this.logger.LogDebug(MavenResources.MSG_SkippingScopedDependency, dependency, dependency.Scope);
                }
            }
        }

        private string GetArtifactUrl(MavenCoordinate coordinate, string extension)
        {
            // Example url: https://repo1.maven.org/maven2/aopalliance/aopalliance/1.0/aopalliance-1.0.pom
            // i.e. [root]/[groupdId with "/" instead of "."]/[artifactId]/[version]/[artifactId]-[version].pom
            string url = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                "https://repo1.maven.org/maven2/{0}/{1}/{2}/{1}-{2}.{3}",
                coordinate.GroupId.Replace(".", "/"),
                coordinate.ArtifactId,
                coordinate.Version,
                extension);
            return url;
        }

        /// <summary>
        /// Returns the path to the unique directory for the artifact
        /// </summary>
        private string GetArtifactFolderPath(MavenCoordinate coordinate)
        {
            string path = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}/{1}/{2}",
                coordinate.GroupId.Replace(".", "/"),
                coordinate.ArtifactId,
                coordinate.Version
                );

            path = Path.Combine(path, this.localCacheDirectory);
            return path;
        }

        private string GetFilePath(MavenCoordinate coordinate, string extension)
        {
            string filePath = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}-{1}.{2}",
                coordinate.ArtifactId,
                coordinate.Version,
                extension);
            filePath = Path.Combine(this.GetArtifactFolderPath(coordinate), filePath);
            return filePath;
        }

        private string TryGetJar(MavenCoordinate coordinate)
        {
            Debug.Assert(coordinate != null, "Expecting a valid coordinate");
            Debug.Assert(!string.IsNullOrWhiteSpace(coordinate.Version));

            string localJarFilePath = this.GetFilePath(coordinate, JAR_Extension);

            if (File.Exists(localJarFilePath))
            {
                this.logger.LogDebug(MavenResources.MSG_UsingCachedFile, localJarFilePath);
            }
            else
            {
                string url = this.GetArtifactUrl(coordinate, JAR_Extension);
                this.DownloadFile(url, localJarFilePath);

                if (!File.Exists(localJarFilePath))
                {
                    localJarFilePath = null;
                }
            }
            return localJarFilePath;
        }

        private MavenPartialPOM TryGetPOM(MavenCoordinate resolvedCoordinate)
        {
            Debug.Assert(resolvedCoordinate != null, "Expecting a valid coordinate");
            Debug.Assert(!string.IsNullOrWhiteSpace(resolvedCoordinate.Version));

            MavenPartialPOM pom;

            // See if we have already loaded this pom
            if (this.coordPomMap.TryGetValue(resolvedCoordinate, out pom))
            {
                return pom;
            }

            string localPOMFilePath = this.GetFilePath(resolvedCoordinate, POM_Extension);

            if (File.Exists(localPOMFilePath))
            {
                this.logger.LogDebug(MavenResources.MSG_UsingCachedFile, localPOMFilePath);
                pom = MavenPartialPOM.Load(localPOMFilePath);
            }
            else
            {
                pom = DownloadPOM(resolvedCoordinate);
            }

            this.coordPomMap[resolvedCoordinate] = pom; // cache the result to avoid further lookups
            return pom;
        }

        private MavenPartialPOM DownloadPOM(MavenCoordinate descriptor)
        {
            string url = this.GetArtifactUrl(descriptor, POM_Extension);
            string localPOMFilePath = this.GetFilePath(descriptor, POM_Extension);
            this.DownloadFile(url, localPOMFilePath);

            MavenPartialPOM pomFile = null;
            if (File.Exists(localPOMFilePath))
            {
                pomFile = MavenPartialPOM.Load(localPOMFilePath);
            }
            return pomFile;
        }

        /// <summary>
        /// Returns all direct dependencies for the specified project, including
        /// inherited dependencies
        /// </summary>
        private IEnumerable<MavenDependency> GetCurrentProjectDependencies(MavenPartialPOM pom)
        {
            List<MavenDependency> allDependencies = new List<MavenDependency>();
            while(pom != null)
            {
                this.logger.LogDebug(MavenResources.MSG_AddingDependenciesForPOM, pom);
                AddInheritedDependencies(pom.Dependencies, allDependencies);

                if (pom.Parent != null)
                {
                    pom = this.TryGetPOM(pom.Parent);
                }
                else
                {
                    pom = null;
                }
            }

            return allDependencies;
        }

        /// <summary>
        /// Adds inherited dependencies to the list of current dependencies.
        /// Conflict resolution: an inherited dependency will be ignored if there is
        /// already a current dependency for the specified artifact, regardless of artifact version.
        /// </summary>
        private void AddInheritedDependencies(IEnumerable<MavenDependency> inherited, List<MavenDependency> current)
        {
            foreach(MavenDependency sourceItem in inherited)
            {
                // Ignore inherited artifacts that are already in the list
                if (ContainsArtifact(current, sourceItem))
                {
                    this.logger.LogDebug(MavenResources.MSG_SkippingInheritedDependency, sourceItem);
                }
                else
                {
                    current.Add(sourceItem);
                }
            }
        }

        private static bool ContainsArtifact(IEnumerable<MavenCoordinate> coords, MavenCoordinate item)
        {
            return coords.Any(c => MavenCoordinate.IsSameArtifact(item, c));
        }

        private static MavenDependency GetMatchingArtifact(IEnumerable<MavenDependency> coords, MavenDependency item)
        {
            // Should be zero or one
            return coords.SingleOrDefault(c => MavenCoordinate.IsSameArtifact(item, c));
        }


        private static bool ShouldIncludeDependency(MavenDependency dependency)
        {
            string[] scopesToInclude = { null, "", "compile", "runtime" };

            bool include = scopesToInclude.Any(s => string.Equals(dependency.Scope, s, MavenPartialPOM.PomComparisonType));

            return include;
        }

        private string ResolveCoordinateVersion(string resolvedGroupId, string resolvedArtifactId, string rawVersion, MavenPartialPOM currentPom)
        {
            string effectiveVersion = ExpandVariables(rawVersion, currentPom);

            if (effectiveVersion == null)
            {
                effectiveVersion = this.TryGetVersionFromDependencyManagement(resolvedGroupId, resolvedArtifactId, currentPom);
            }

            if (effectiveVersion == null && currentPom.Parent != null)
            {
                MavenCoordinate resolvedParent = GetResolvedCoordinate(currentPom.Parent, currentPom);

                if (resolvedParent != null)
                {
                    this.logger.LogDebug(MavenResources.MSG_AttemptingToResolveFromParentPOM, currentPom.Parent);
                    MavenPartialPOM parentPOM = this.TryGetPOM(resolvedParent);
                    if (parentPOM != null)
                    {
                        effectiveVersion = ResolveCoordinateVersion(resolvedGroupId, resolvedArtifactId, rawVersion, parentPOM);

                        if (effectiveVersion != null)
                        {
                            logger.LogDebug(MavenResources.MSG_ResolvedVersionInPom, parentPOM);
                        }
                    }
                }
            }

            return effectiveVersion;
        }

        private string ResolveCoordinateVersionOLD(MavenCoordinate coordinate, MavenPartialPOM currentPom)
        {
            string effectiveVersion = ExpandVariables(coordinate.Version, currentPom);

            if (coordinate.Version == null)
            {
                effectiveVersion = this.TryGetVersionFromDependencyManagementOLD(coordinate, currentPom);
            }

            if (effectiveVersion == null && currentPom.Parent != null)
            {
                this.logger.LogDebug(MavenResources.MSG_AttemptingToResolveFromParentPOM, currentPom.Parent);
                MavenPartialPOM parentPOM = this.TryGetPOM(currentPom.Parent);
                if (parentPOM != null)
                {
                    effectiveVersion = ResolveCoordinateVersionOLD(coordinate, parentPOM);

                    if (effectiveVersion != null)
                    {
                        logger.LogDebug(MavenResources.MSG_ResolvedVersionInPom, parentPOM);
                    }
                }
            }

            return effectiveVersion;
        }

        private string ExpandVariables(string rawValue, MavenPartialPOM pom)
        {
            if (rawValue == null) { return null; }

            string expandedValue = rawValue;

            // Match strings "${xxx}" and extract the "xxx"
            Match match = Regex.Match(rawValue, "\\A\\${([\\S]+)}$");
            if (match.Success)
            {
                // Try to resolve the variable
                Debug.Assert(match.Groups.Count == 2);
                string variable = match.Groups[1].Value;

                if (string.Equals("project.version", variable, MavenPartialPOM.PomComparisonType) ||
                    // Support the obsolete variable formats
                    string.Equals("pom.version", variable, MavenPartialPOM.PomComparisonType) ||
                    string.Equals("version", variable, MavenPartialPOM.PomComparisonType))
                {
                    this.logger.LogDebug(MavenResources.MSG_ExpandedProjectVariable, rawValue);
                    expandedValue = pom.Version;
                    if (expandedValue == null && pom.Parent != null)
                    {
                        expandedValue = pom.Parent.Version;
                    }
                }
                else if (string.Equals("project.groupId", variable, MavenPartialPOM.PomComparisonType) ||
                    // Support the obsolete variable formats
                    string.Equals("pom.groupId", variable, MavenPartialPOM.PomComparisonType) ||
                    string.Equals("groupId", variable, MavenPartialPOM.PomComparisonType))
                {
                    this.logger.LogDebug(MavenResources.MSG_ExpandedProjectVariable, rawValue);
                    expandedValue = pom.GroupId;
                    if (expandedValue == null && pom.Parent != null)
                    {
                        expandedValue = pom.Parent.GroupId;
                    }
                }
                else if (string.Equals("project.artifactId", variable, MavenPartialPOM.PomComparisonType) ||
                    // Support the obsolete variable formats
                    string.Equals("pom.artifactId", variable, MavenPartialPOM.PomComparisonType) ||
                    string.Equals("artifactId", variable, MavenPartialPOM.PomComparisonType))
                {
                    this.logger.LogDebug(MavenResources.MSG_ExpandedProjectVariable, rawValue);
                    expandedValue = pom.ArtifactId;
                }
                else if (pom.Properties != null && pom.Properties.ContainsKey(variable))
                {
                    this.logger.LogDebug(MavenResources.MSG_ExpandedProjectVariable, rawValue);
                    expandedValue = pom.Properties[variable];
                }
                else
                {
                    this.logger.LogWarning(MavenResources.WARN_UnrecognizedProjectVariable, rawValue);
                    expandedValue = null;
                }
            }

            return expandedValue;
        }

        private string TryGetVersionFromDependencyManagement(string resolvedGroupId, string resolvedArtifactId, MavenPartialPOM pom)
        {
            string effectiveVersion = null;
            if (pom.DependencyManagement != null && pom.DependencyManagement.Dependencies != null)
            {
                MavenDependency match = pom.DependencyManagement.Dependencies.FirstOrDefault(d =>
                    string.Equals(resolvedGroupId, d.GroupId, MavenPartialPOM.PomComparisonType) &&
                    string.Equals(resolvedArtifactId, d.ArtifactId, MavenPartialPOM.PomComparisonType)
                );

                if (match != null)
                {
                    effectiveVersion = ExpandVariables(match.Version, pom);
                    this.logger.LogDebug(MavenResources.MSG_ResolvedVersionFromDependencyManagement, effectiveVersion);
                }
            }
            return effectiveVersion;
        }


        private string TryGetVersionFromDependencyManagementOLD(MavenCoordinate coordinate, MavenPartialPOM pom)
        {
            Debug.Assert(coordinate.Version == null);

            string effectiveVersion = null;
            if (pom.DependencyManagement != null && pom.DependencyManagement.Dependencies != null)
            {
                MavenDependency match = pom.DependencyManagement.Dependencies.FirstOrDefault(d =>
                    string.Equals(coordinate.GroupId, d.GroupId, MavenPartialPOM.PomComparisonType) &&
                    string.Equals(coordinate.ArtifactId, d.ArtifactId, MavenPartialPOM.PomComparisonType)
                );

                if (match != null)
                {
                    effectiveVersion = ExpandVariables(match.Version, pom);
                    this.logger.LogDebug(MavenResources.MSG_ResolvedVersionFromDependencyManagement, effectiveVersion);
                }
            }
            return effectiveVersion;
        }

        private void DownloadFile(string url, string localFilePath)
        {
            this.logger.LogDebug(MavenResources.MSG_DownloadingFile, url);
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath));

            using (HttpClient httpClient = new HttpClient())
            using (HttpResponseMessage response = httpClient.GetAsync(url).Result)
            {
                if (response.IsSuccessStatusCode)
                {
                    using (FileStream fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write))
                    {
                        response.Content.CopyToAsync(fileStream).Wait();
                    }
                    this.logger.LogDebug(MavenResources.MSG_FileDownloaded, localFilePath);
                }
                else
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        this.logger.LogWarning(MavenResources.WARN_DependencyWasNotFound, url);
                    }
                    else
                    {
                        this.logger.LogError(MavenResources.ERROR_FailedToDownloadDependency, url, response.StatusCode, response.ReasonPhrase);
                    }
                }
            }

        }

        #endregion
    }
}
