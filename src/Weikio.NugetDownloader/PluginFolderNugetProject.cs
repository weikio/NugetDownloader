using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
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
        public List<DllInfo> InstalledDlls { get; } = new List<DllInfo>();
        public List<RunTimeDll> RuntimeDlls { get; } = new List<RunTimeDll>();
        public List<string> InstalledPackages { get; } = new List<string>();

        public PluginFolderNugetProject(string root, IPackageSearchMetadata pluginNuGetPackage, NuGetFramework targetFramework, bool onlyDownload = false) :
            base(root, new PackagePathResolver(root), targetFramework)
        {
            _root = root;
            _pluginNuGetPackage = pluginNuGetPackage;
            _targetFramework = targetFramework;
            _onlyDownload = onlyDownload;
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

                var entriesWithTargetFramework = zipArchiveEntries
                    .Select(e => new { TargetFramework = NuGetFramework.Parse(e.FullName.Split('/')[1]), Entry = e }).ToList();

                var matchingEntries = entriesWithTargetFramework
                    .Where(e => e.TargetFramework.Version.Major > 0 &&
                                e.TargetFramework.Version <= _targetFramework.Version).ToList();

                var orderedEntries = matchingEntries
                    .OrderBy(e => e.TargetFramework.GetShortFolderName()).ToList();

                if (orderedEntries.Any())
                {
                    var dllEntries = orderedEntries
                        .GroupBy(e => e.TargetFramework.GetShortFolderName())
                        .Last()
                        .Select(e => e)
                        .ToArray();

                    var pluginAssemblies = new List<string>();

                    foreach (var e in dllEntries)
                    {
                        e.Entry.ExtractToFile(Path.Combine(_root, e.Entry.Name), overwrite: true);

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
            }

            return result;
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
            var libPath  = parts[3];

            var tf = NuGetFramework.ParseFolder(libPath);

            return (rid, tf.DotNetFrameworkName, libPath, tf.Version.ToString());
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
                return string.Equals(TargetFrameworkShortName, "native", StringComparison.InvariantCultureIgnoreCase);
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
    }
}
