﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NuGet.Versioning;
using Squirrel.NuGet;
using Squirrel.SimpleSplat;

namespace Squirrel
{
    /// <summary>
    /// A base class describing where Squirrel can find key folders and files.
    /// </summary>
    public abstract class AppDesc : IEnableLogger
    {
        /// <summary>
        /// Auto-detect the platform from the current operating system.
        /// </summary>
        public static AppDesc GetCurrentPlatform()
        {
            if (SquirrelRuntimeInfo.IsWindows)
                return new AppDescWindows();

            if (SquirrelRuntimeInfo.IsOSX)
                return new AppDescOsx();

            throw new NotSupportedException($"OS platform '{SquirrelRuntimeInfo.SystemOsName}' is not supported.");
        }

        /// <summary>
        /// Instantiate base class <see cref="AppDesc"/>.
        /// </summary>
        protected AppDesc()
        {
        }

        /// <summary> The unique application Id. This is used in various app paths. </summary>
        public abstract string AppId { get; }

        /// <summary> 
        /// The root directory of the application. On Windows, this folder contains all 
        /// the application files, but that may not be the case on other operating systems. 
        /// </summary>
        public abstract string RootAppDir { get; }

        /// <summary> The directory in which nupkg files are stored for this application. </summary>
        public abstract string PackagesDir { get; }

        /// <summary> The temporary directory for this application. </summary>
        public abstract string AppTempDir { get; }

        /// <summary> True if the current binary is Update.exe within the specified application. </summary>
        public abstract bool IsUpdateExe { get; }

        /// <summary> The directory where new versions are stored, before they are applied. </summary>
        public abstract string VersionStagingDir { get; }

        /// <summary> 
        /// The directory where the current version of the application is stored.
        /// This directory will be swapped out for a new version in <see cref="VersionStagingDir"/>.
        /// </summary>
        public abstract string CurrentVersionDir { get; }

        /// <summary> The path to the current Update.exe or similar on other operating systems. </summary>
        public abstract string UpdateExePath { get; }

        /// <summary> The path to the RELEASES index detailing the local packages. </summary>
        public virtual string ReleasesFilePath => Path.Combine(PackagesDir, "RELEASES");

        /// <summary> The path to the .betaId file which contains a unique GUID for this user. </summary>
        public virtual string BetaIdFilePath => Path.Combine(PackagesDir, ".betaId");

        /// <summary> The currently installed version of the application. </summary>
        public abstract SemanticVersion CurrentlyInstalledVersion { get; }

        /// <summary>
        /// Gets a 
        /// </summary>
        /// <param name="version">The application version</param>
        /// <returns>The full path to the version staging directory</returns>
        public virtual string GetVersionStagingPath(SemanticVersion version)
        {
            return Path.Combine(VersionStagingDir, "app-" + version);
        }

        internal List<(string PackagePath, SemanticVersion PackageVersion, bool IsDelta)> GetLocalPackages()
        {
            var query = from x in Directory.EnumerateFiles(PackagesDir, "*.nupkg")
                let re = ReleaseEntry.ParseEntryFileName(x)
                where re.Version != null
                select (x, re.Version, re.IsDelta);
            return query.ToList();
        }

        internal string UpdateAndRetrieveCurrentFolder(bool force)
        {
            try {
                var releases = GetVersions();
                var latestVer = releases.OrderByDescending(m => m.Version).First();
                var currentVer = releases.FirstOrDefault(f => f.IsCurrent);

                // if the latest ver is already current, or it does not support
                // being in a current directory.
                if (latestVer.IsCurrent) {
                    this.Log().Info($"Current directory already pointing to latest version.");
                    return latestVer.DirectoryPath;
                }

                if (force) {
                    this.Log().Info($"Killing running processes in '{RootAppDir}'.");
                    Utility.KillProcessesInDirectory(RootAppDir);
                }

                // 'current' does exist, and it's wrong, so lets get rid of it
                if (currentVer != default) {
                    string legacyVersionDir = GetVersionStagingPath(currentVer.Version);
                    this.Log().Info($"Moving '{currentVer.DirectoryPath}' to '{legacyVersionDir}'.");
                    Utility.Retry(() => Directory.Move(currentVer.DirectoryPath, legacyVersionDir));
                }

                // this directory does not support being named 'current'
                if (latestVer.Manifest == null) {
                    this.Log().Info($"Cannot promote {latestVer.Version} as current as it has no manifest");
                    return latestVer.DirectoryPath;
                }

                // 'current' doesn't exist right now, lets move the latest version
                var latestDir = CurrentVersionDir;
                this.Log().Info($"Moving '{latestVer.DirectoryPath}' to '{latestDir}'.");
                Utility.Retry(() => Directory.Move(latestVer.DirectoryPath, latestDir));

                this.Log().Info("Running app in: " + latestDir);
                return latestDir;
            } catch (Exception e) {
                var releases = GetVersions();
                string fallback = releases.OrderByDescending(m => m.Version).First().DirectoryPath;
                var currentVer = releases.FirstOrDefault(f => f.IsCurrent);
                if (currentVer != default && Directory.Exists(currentVer.DirectoryPath)) {
                    fallback = currentVer.DirectoryPath;
                }

                this.Log().WarnException("Unable to update 'current' directory", e);
                this.Log().Info("Running app in: " + fallback);
                return fallback;
            }
        }

        /// <summary>
        /// Given a base dir and a directory name, will create a new sub directory of that name.
        /// Will return null if baseDir is null, or if baseDir does not exist. 
        /// </summary>
        protected static string CreateSubDirIfDoesNotExist(string baseDir, string newDir)
        {
            if (String.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(newDir)) return null;
            var infoBase = new DirectoryInfo(baseDir);
            if (!infoBase.Exists) return null;
            var info = new DirectoryInfo(Path.Combine(baseDir, newDir));
            if (!info.Exists) info.Create();
            return info.FullName;
        }

        /// <summary>
        /// Starts Update.exe with the correct arguments to restart this process.
        /// Update.exe will wait for this process to exit, and apply any pending version updates
        /// before re-launching the latest version.
        /// </summary>
        public virtual Process StartRestartingProcess(string exeToStart = null, string arguments = null)
        {
            // NB: Here's how this method works:
            //
            // 1. We're going to pass the *name* of our EXE and the params to 
            //    Update.exe
            // 2. Update.exe is going to grab our PID (via getting its parent), 
            //    then wait for us to exit.
            // 3. Return control and new Process back to caller and allow them to Exit as desired.
            // 4. After our process exits, Update.exe unblocks, then we launch the app again, possibly 
            //    launching a different version than we started with (this is why
            //    we take the app's *name* rather than a full path)

            exeToStart = exeToStart ?? Path.GetFileName(SquirrelRuntimeInfo.EntryExePath);

            List<string> args = new() {
                "--forceLatest",
                "--processStartAndWait",
                exeToStart,
            };

            if (arguments != null) {
                args.Add("-a");
                args.Add(arguments);
            }

            return ProcessUtil.StartNonBlocking(UpdateExePath, args, Path.GetDirectoryName(UpdateExePath));
        }

        internal VersionDirInfo GetLatestVersion()
        {
            return GetLatestVersion(GetVersions());
        }

        internal VersionDirInfo GetLatestVersion(IEnumerable<VersionDirInfo> versions)
        {
            return versions.OrderByDescending(r => r.Version).FirstOrDefault();
        }

        internal VersionDirInfo GetVersionInfoFromDirectory(string d)
        {
            bool isCurrent = Utility.FullPathEquals(d, CurrentVersionDir);
            var directoryName = Path.GetFileName(d);
            bool isExecuting = Utility.IsFileInDirectory(SquirrelRuntimeInfo.EntryExePath, d);
            var manifest = Utility.ReadManifestFromVersionDir(d);

            if (manifest != null) {
                return new(manifest, manifest.Version, d, isCurrent, isExecuting);
            }

            if (Utility.PathPartStartsWith(directoryName, "app-") && NuGetVersion.TryParse(directoryName.Substring(4), out var ver)) {
                return new(null, ver, d, isCurrent, isExecuting);
            }

            return null;
        }

        internal record VersionDirInfo(IPackage Manifest, SemanticVersion Version, string DirectoryPath, bool IsCurrent, bool IsExecuting);

        internal VersionDirInfo[] GetVersions()
        {
            return Directory.EnumerateDirectories(RootAppDir, "app-*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(VersionStagingDir, "app-*", SearchOption.TopDirectoryOnly))
                .Concat(new[] { CurrentVersionDir })
                .Select(Utility.NormalizePath)
                .Distinct(SquirrelRuntimeInfo.PathStringComparer)
                .Select(GetVersionInfoFromDirectory)
                .Where(d => d != null)
                .ToArray();
        }
    }

    /// <summary>
    /// An implementation for Windows which uses the Squirrel defaults and installs to
    /// local app data.
    /// </summary>
    public class AppDescWindows : AppDesc
    {
        /// <inheritdoc />
        public override string AppId { get; }

        /// <inheritdoc />
        public override string RootAppDir { get; }

        /// <inheritdoc />
        public override string UpdateExePath { get; }

        /// <inheritdoc />
        public override bool IsUpdateExe { get; }

        /// <inheritdoc />
        public override SemanticVersion CurrentlyInstalledVersion { get; }

        /// <inheritdoc />
        public override string PackagesDir => CreateSubDirIfDoesNotExist(RootAppDir, "packages");

        /// <inheritdoc />
        public override string AppTempDir => CreateSubDirIfDoesNotExist(PackagesDir, "SquirrelClowdTemp");


        /// <inheritdoc />
        public override string VersionStagingDir => CreateSubDirIfDoesNotExist(RootAppDir, "staging");

        /// <inheritdoc />
        public override string CurrentVersionDir => CreateSubDirIfDoesNotExist(RootAppDir, "current");

        /// <summary>
        /// Creates a new Platform and tries to auto-detect the application details from
        /// the current context.
        /// </summary>
        public AppDescWindows() : this(SquirrelRuntimeInfo.EntryExePath)
        {
        }

        /// <summary>
        /// Creates a new windows application platform at the specified app directory.
        /// </summary>
        /// <param name="appDir">The location of the application.</param>
        /// <param name="appId">The unique ID of the application.</param>
        public AppDescWindows(string appDir, string appId)
        {
            if (!SquirrelRuntimeInfo.IsWindows)
                throw new NotSupportedException("Cannot instantiate AppDescWindows on a non-Windows system.");
            
            AppId = appId;
            RootAppDir = appDir;
            var updateExe = Path.Combine(appDir, "Update.exe");
            var ver = GetLatestVersion();

            if (File.Exists(updateExe) && ver != null) {
                UpdateExePath = updateExe;
                CurrentlyInstalledVersion = ver.Version;
            }
        }

        internal AppDescWindows(string ourPath)
        {
            if (!SquirrelRuntimeInfo.IsWindows)
                throw new NotSupportedException("Cannot instantiate AppDescWindows on a non-Windows system.");
            
            var myDir = Path.GetDirectoryName(ourPath);

            // Am I update.exe at the application root?
            if (ourPath != null &&
                Path.GetFileName(ourPath).Equals("update.exe", StringComparison.InvariantCultureIgnoreCase) &&
                ourPath.IndexOf("app-", StringComparison.InvariantCultureIgnoreCase) == -1 &&
                ourPath.IndexOf("SquirrelClowdTemp", StringComparison.InvariantCultureIgnoreCase) == -1) {
                UpdateExePath = ourPath;
                RootAppDir = myDir;
                var ver = GetLatestVersion();
                if (ver != null) {
                    AppId = ver.Manifest?.Id ?? Path.GetFileName(myDir);
                    CurrentlyInstalledVersion = ver.Version;
                    IsUpdateExe = true;
                }
            }

            // Am I running from within an app-* or current dir?
            var info = GetVersionInfoFromDirectory(myDir);
            if (info != null) {
                var updateExe = Path.Combine(myDir, "..\\Update.exe");
                var updateExe2 = Path.Combine(myDir, "..\\..\\Update.exe");
                string updateLocation = null;

                if (File.Exists(updateExe)) {
                    updateLocation = Path.GetFullPath(updateExe);
                } else if (File.Exists(updateExe2)) {
                    updateLocation = Path.GetFullPath(updateExe2);
                }

                if (updateLocation != null) {
                    UpdateExePath = updateLocation;
                    RootAppDir = Path.GetDirectoryName(updateLocation);
                    AppId = info.Manifest?.Id ?? Path.GetFileName(Path.GetDirectoryName(updateLocation));
                    CurrentlyInstalledVersion = info.Version;
                    IsUpdateExe = false;
                }
            }
        }
    }

    /// <summary>
    /// The default for OSX. All application files will remain in the '.app'.
    /// All additional files (log, etc) will be placed in a temporary directory.
    /// </summary>
    public class AppDescOsx : AppDesc
    {
        /// <inheritdoc />
        public override string AppId { get; }

        /// <inheritdoc />
        public override string RootAppDir { get; }

        /// <inheritdoc />
        public override string UpdateExePath { get; }

        /// <inheritdoc />
        public override bool IsUpdateExe { get; }

        /// <inheritdoc />
        public override string CurrentVersionDir => RootAppDir;

        /// <inheritdoc />
        public override SemanticVersion CurrentlyInstalledVersion { get; }

        /// <inheritdoc />
        public override string AppTempDir => CreateSubDirIfDoesNotExist(Utility.GetDefaultTempBaseDirectory(), AppId);

        /// <inheritdoc />
        public override string PackagesDir => CreateSubDirIfDoesNotExist(AppTempDir, "packages");

        /// <inheritdoc />
        public override string VersionStagingDir => CreateSubDirIfDoesNotExist(AppTempDir, "staging");

        /// <summary>
        /// Creates a new <see cref="AppDescOsx"/> and auto-detects the
        /// app information from metadata embedded in the .app.
        /// </summary>
        public AppDescOsx()
        {
            if (!SquirrelRuntimeInfo.IsOSX)
                throw new NotSupportedException("Cannot instantiate AppDescOsx on a non-osx system.");

            // are we inside a .app?
            var ourPath = SquirrelRuntimeInfo.EntryExePath;
            var ix = ourPath.IndexOf(".app/", StringComparison.InvariantCultureIgnoreCase);
            if (ix < 0) return;

            var appPath = ourPath.Substring(0, ix + 5);
            var contentsDir = Path.Combine(appPath, "Contents");
            var updateExe = Path.Combine(contentsDir, "UpdateMac");
            var info = GetVersionInfoFromDirectory(contentsDir);

            if (File.Exists(updateExe) && info?.Manifest != null) {
                AppId = info.Manifest.Id;
                RootAppDir = appPath;
                UpdateExePath = updateExe;
                CurrentlyInstalledVersion = info.Version;
                IsUpdateExe = Utility.FullPathEquals(updateExe, ourPath);
            }
        }
    }
}