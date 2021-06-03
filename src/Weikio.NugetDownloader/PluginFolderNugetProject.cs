using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyModel;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;

namespace Weikio.NugetDownloader
{
    public class PluginFolderNugetProject : FolderNuGetProject
    {
        private const string PluginAssemblyFilesFileName = "pluginAssemblyFiles.txt";

        private readonly string _root;
        private readonly IPackageSearchMetadata _pluginNuGetPackage;
        private readonly NuGetFramework _targetFramework;
        private readonly bool _onlyDownload;
        private CompatibilityProvider _compProvider;
        private FrameworkReducer _reducer;

        public string Rid { get; set; }
        public List<string> SupportedRids { get; }
        public List<DllInfo> InstalledDlls { get; } = new List<DllInfo>();
        public List<RunTimeDll> RuntimeDlls { get; } = new List<RunTimeDll>();
        public List<string> InstalledPackages { get; } = new List<string>();

        public PluginFolderNugetProject(string root, IPackageSearchMetadata pluginNuGetPackage, NuGetFramework targetFramework, bool onlyDownload = false,
            string targetRid = null) :
            base(root, new PackagePathResolver(root), targetFramework)
        {
            _root = root;
            _pluginNuGetPackage = pluginNuGetPackage;
            _targetFramework = targetFramework;
            _onlyDownload = onlyDownload;

            _compProvider = new CompatibilityProvider(new DefaultFrameworkNameProvider());
            _reducer = new FrameworkReducer(new DefaultFrameworkNameProvider(), _compProvider);
            SupportedRids = GetSupportedRids(targetRid);
        }

        public override async Task<IEnumerable<PackageReference>> GetInstalledPackagesAsync(CancellationToken token)
        {
            return await base.GetInstalledPackagesAsync(token);
        }

        public override async Task<bool> InstallPackageAsync(PackageIdentity packageIdentity, DownloadResourceResult downloadResourceResult,
            INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            var result = await base.InstallPackageAsync(packageIdentity, downloadResourceResult, nuGetProjectContext, token);

            if (_onlyDownload)
            {
                return result;
            }

            InstalledPackages.Add(packageIdentity.ToString());

            using (var zipArchive = new ZipArchive(downloadResourceResult.PackageStream))
            {
                var zipArchiveEntries = zipArchive.Entries
                    .Where(e => e.Name.EndsWith(".dll") || e.Name.EndsWith(".exe")).ToList();

                await HandleManagedDlls(packageIdentity, zipArchiveEntries);
                HandleRuntimeDlls(packageIdentity, zipArchiveEntries);
            }

            return result;
        }

        private void HandleRuntimeDlls(PackageIdentity packageIdentity, List<ZipArchiveEntry> zipArchiveEntries)
        {
            var runtimeEntries = zipArchiveEntries
                .Where(e => string.Equals(e.FullName.Split('/')[0], "runtimes", StringComparison.InvariantCultureIgnoreCase))
                .Select(e => new { Rid = e.FullName.Split('/')[1], Entry = e }).ToList();

            foreach (var runtime in runtimeEntries)
            {
                var runtimeDll = new RunTimeDll()
                {
                    FileName = runtime.Entry.Name,
                    FullFilePath = Path.Combine(_root, packageIdentity.ToString(), runtime.Entry.FullName),
                    RelativeFilePath = Path.Combine(packageIdentity.ToString(), runtime.Entry.FullName),
                    PackageIdentity = packageIdentity.Id,
                };

                var runtimeDllDetails = ParseRuntimeDllDetails(runtime.Entry.FullName);
                runtimeDll.RID = runtimeDllDetails.Rid;
                runtimeDll.TargetFramework = runtimeDllDetails.Target;
                runtimeDll.TargetFrameworkShortName = runtimeDllDetails.TargetShortName;
                runtimeDll.TargetVersion = runtimeDllDetails.TargetVersion;

                RuntimeDlls.Add(runtimeDll);
            }

            var supportedRunTimeDlls = RuntimeDlls.Where(x => SupportedRids.Contains(x.RID)).ToList();

            var runtimeLibFiles = supportedRunTimeDlls.Where(x => x.IsLib).GroupBy(x => x.FileName).ToList();

            foreach (var fileGroup in runtimeLibFiles)
            {
                var targetFrameworks = fileGroup.Select(x =>
                        NuGetFramework.ParseFrameworkName(x.TargetFramework, new DefaultFrameworkNameProvider()))
                    .ToList();

                var compatibleFrameworks =
                    targetFrameworks.Where(x => _compProvider.IsCompatible(_targetFramework, x)).ToList();

                foreach (var runTimeDll in fileGroup)
                {
                    if (compatibleFrameworks.Any(x => string.Equals(x.DotNetFrameworkName, runTimeDll.TargetFramework)))
                    {
                        runTimeDll.IsSupported = true;
                    }
                }

                var mostMatching = _reducer.GetNearest(_targetFramework, targetFrameworks);

                if (mostMatching == null)
                {
                    continue;
                }

                foreach (var runTimeDll in fileGroup)
                {
                    if (string.Equals(runTimeDll.TargetFramework, mostMatching.DotNetFrameworkName))
                    {
                        runTimeDll.IsRecommended = true;
                    }
                }
            }

            var runtimeNativeFiles = supportedRunTimeDlls.Where(x => x.IsNative).GroupBy(x => x.FileName).ToList();

            foreach (var fileGroup in runtimeNativeFiles)
            {
                foreach (var runTimeDll in fileGroup)
                {
                    runTimeDll.IsSupported = true;
                }

                // The Rids are already ordered from best match to the least matching
                var recommededFound = false;
                foreach (var supportedRid in SupportedRids)
                {
                    foreach (var runTimeDll in fileGroup)
                    {
                        if (string.Equals(runTimeDll.RID, supportedRid))
                        {
                            runTimeDll.IsRecommended = true;
                            recommededFound = true;
                            
                            break;
                        }

                        if (recommededFound)
                        {
                            break;
                        }
                    }
                }
            }
        }

        private async Task HandleManagedDlls(PackageIdentity packageIdentity, List<ZipArchiveEntry> zipArchiveEntries)
        {
            var entriesWithTargetFramework = zipArchiveEntries
                .Select(e => new { TargetFramework = NuGetFramework.Parse(e.FullName.Split('/')[1]), Entry = e }).ToList();

            var compatibleEntries = entriesWithTargetFramework.Where(e => _compProvider.IsCompatible(_targetFramework, e.TargetFramework)).ToList();
            var mostCompatibleFramework = _reducer.GetNearest(_targetFramework, compatibleEntries.Select(x => x.TargetFramework));

            if (mostCompatibleFramework == null)
            {
                return;
            }

            var matchingEntries = entriesWithTargetFramework
                .Where(e => e.TargetFramework == mostCompatibleFramework).ToList();

            if (matchingEntries.Any())
            {
                var pluginAssemblies = new List<string>();

                foreach (var e in matchingEntries)
                {
                    ZipFileExtensions.ExtractToFile(e.Entry, Path.Combine(_root, e.Entry.Name), overwrite: true);

                    var installedDllInfo = new DllInfo
                    {
                        RelativeFilePath = Path.Combine(packageIdentity.ToString(), e.Entry.FullName),
                        FullFilePath = Path.Combine(_root, packageIdentity.ToString(), e.Entry.FullName),
                        FileName = e.Entry.Name,
                        TargetFrameworkName = e.TargetFramework.ToString(),
                        TargetFrameworkShortName = e.TargetFramework.GetShortFolderName(),
                        PackageIdentity = packageIdentity.Id,
                        TargetVersion = e.TargetFramework.Version.ToString()
                    };

                    InstalledDlls.Add(installedDllInfo);

                    if (packageIdentity.Id == _pluginNuGetPackage.Identity.Id)
                    {
                        pluginAssemblies.Add(e.Entry.Name);
                    }
                }

                await File.WriteAllLinesAsync(Path.Combine(_root, PluginAssemblyFilesFileName), pluginAssemblies);
            }
        }

        private (string Rid, string Target, string TargetShortName, string TargetVersion) ParseRuntimeDllDetails(string path)
        {
            var parts = path.Split('/');
            var rid = parts[1];
            var target = parts[2];

            if (string.Equals(target, "native", StringComparison.InvariantCultureIgnoreCase))
            {
                target = "native";

                return (rid, target, null, null);
            }

            // lib
            var libPath = parts[3];

            var tf = NuGetFramework.ParseFolder(libPath);

            return (rid, tf.DotNetFrameworkName, libPath, tf.Version.ToString());
        }

        private List<string> GetSupportedRids(string targetRid)
        {
            Rid = string.IsNullOrWhiteSpace(targetRid) ? Microsoft.DotNet.PlatformAbstractions.RuntimeEnvironment.GetRuntimeIdentifier() : targetRid;

            var dependencyContext = DependencyContext.Default;

            var fallbacks = dependencyContext.RuntimeGraph.Single(x =>
                string.Equals(x.Runtime, Rid, StringComparison.InvariantCultureIgnoreCase));

            var result = new List<string> { Rid };

            foreach (var runtimeFallback in fallbacks.Fallbacks)
            {
                result.Add(runtimeFallback);
            }

            return result;
        }

        public async Task<string[]> GetPluginAssemblyFilesAsync()
        {
            return await File.ReadAllLinesAsync(Path.Combine(_root, PluginAssemblyFilesFileName));
        }

        public override async Task<bool> UninstallPackageAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext,
            CancellationToken token)
        {
            return await base.UninstallPackageAsync(packageIdentity, nuGetProjectContext, token);
        }
    }

    public class DllInfo
    {
        public string PackageIdentity { get; set; }
        public string FileName { get; set; }
        public string RelativeFilePath { get; set; }
        public string FullFilePath { get; set; }
        public string TargetFrameworkName { get; set; }
        public string TargetFrameworkShortName { get; set; }
        public string TargetVersion { get; set; }
    }

    public class RunTimeDll
    {
        public string PackageIdentity { get; set; }
        public string FileName { get; set; }
        public string RelativeFilePath { get; set; }
        public string FullFilePath { get; set; }
        public string RID { get; set; }

        public bool IsNative
        {
            get
            {
                return string.Equals(TargetFramework, "native", StringComparison.InvariantCultureIgnoreCase);
            }
        }

        public bool IsLib
        {
            get
            {
                return !IsNative;
            }
        }

        public string TargetFramework { get; set; }
        public string TargetFrameworkShortName { get; set; }
        public string TargetVersion { get; set; }
        public bool IsSupported { get; set; }
        public bool IsRecommended { get; set; }
    }
}
