using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.PackageExtraction;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Weikio.NugetDownloader
{
    public class NuGetDownloader
    {
        private readonly ILogger _logger;

        public NuGetDownloader(ILogger logger = null)
        {
            _logger = logger ?? new ConsoleLogger();
        }

        public async Task<string[]> DownloadAsync(string packageFolder, string packageName, string packageVersion = null, bool includePrerelease = false,
            NuGetFeed packageFeed = null, bool onlyDownload = false)
        {
            IPackageSearchMetadata package = null;
            SourceRepository sourceRepo = null;

            var providers = GetNugetResourceProviders();
            var settings = Settings.LoadDefaultSettings(packageFolder, null, new MachineWideSettings());
            var packageSourceProvider = new PackageSourceProvider(settings);
            var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, providers);

            if (!string.IsNullOrWhiteSpace(packageFeed?.Feed))
            {
                sourceRepo = GetSourceRepo(packageFeed, providers);

                package = await SearchPackageAsync(packageName, packageVersion, includePrerelease, sourceRepo);
            }
            else
            {
                foreach (var repo in sourceRepositoryProvider.GetRepositories())
                {
                    if (packageFeed != null && repo.PackageSource.Name != packageFeed.Name)
                    {
                        continue;
                    }

                    package = await SearchPackageAsync(packageName, packageVersion, includePrerelease, repo);

                    if (package != null)
                    {
                        sourceRepo = repo;

                        break;
                    }
                }
            }

            if (package == null)
            {
                throw new PackageNotFoundException($"Couldn't find package '{packageVersion}'.{packageVersion}.");
            }

            return await DownloadAsync(package, sourceRepo, packageFolder, onlyDownload);
        }

        public async Task<string[]> DownloadAsync(IPackageSearchMetadata packageIdentity, SourceRepository repository,
            string downloadFolder, bool onlyDownload = false)
        {
            if (packageIdentity == null)
            {
                throw new ArgumentNullException(nameof(packageIdentity));
            }

            var providers = GetNugetResourceProviders();
            var settings = Settings.LoadDefaultSettings(downloadFolder, null, new MachineWideSettings());
            var packageSourceProvider = new PackageSourceProvider(settings);
            var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, providers);

            var dotNetFramework = Assembly
                .GetEntryAssembly()
                .GetCustomAttribute<TargetFrameworkAttribute>()?
                .FrameworkName;

            var frameworkNameProvider = new FrameworkNameProvider(
                new[] { DefaultFrameworkMappings.Instance },
                new[] { DefaultPortableFrameworkMappings.Instance });

            var nuGetFramework = NuGetFramework.ParseFrameworkName(dotNetFramework, frameworkNameProvider);

            var project = new PluginFolderNugetProject(downloadFolder, packageIdentity, nuGetFramework, onlyDownload);

            var packageManager = new NuGetPackageManager(sourceRepositoryProvider, settings, downloadFolder) { PackagesFolderNuGetProject = project };

            var clientPolicyContext = ClientPolicyContext.GetClientPolicy(settings, _logger);

            var projectContext = new FolderProjectContext(_logger)
            {
                PackageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode.Defaultv2,
                    PackageExtractionBehavior.XmlDocFileSaveMode,
                    clientPolicyContext,
                    _logger)
            };

            var resolutionContext = new ResolutionContext(
                DependencyBehavior.Lowest,
                true,
                includeUnlisted: false,
                VersionConstraints.None);

            var downloadContext = new PackageDownloadContext(
                resolutionContext.SourceCacheContext,
                downloadFolder,
                resolutionContext.SourceCacheContext.DirectDownload);

            await packageManager.InstallPackageAsync(
                project,
                packageIdentity.Identity,
                resolutionContext,
                projectContext,
                downloadContext,
                repository,
                new List<SourceRepository>(),
                CancellationToken.None);

            if (onlyDownload)
            {
                var versionFolder = Path.Combine(downloadFolder, packageIdentity.Identity.ToString());

                return Directory.GetFiles(versionFolder, "*.*", SearchOption.AllDirectories);
            }

            return await project.GetPluginAssemblyFilesAsync();
        }

        private static List<Lazy<INuGetResourceProvider>> GetNugetResourceProviders()
        {
            var providers = new List<Lazy<INuGetResourceProvider>>();
            providers.AddRange(Repository.Provider.GetCoreV3()); // Add v3 API s

            return providers;
        }

        private static SourceRepository GetSourceRepo(NuGetFeed packageFeed, List<Lazy<INuGetResourceProvider>> providers)
        {
            SourceRepository sourceRepo;
            var packageSource = new PackageSource(packageFeed.Feed);

            if (!string.IsNullOrWhiteSpace(packageFeed.Username))
            {
                packageSource.Credentials = new PackageSourceCredential(packageFeed.Name, packageFeed.Username, packageFeed.Password, true, null);
            }

            sourceRepo = new SourceRepository(packageSource, providers);

            return sourceRepo;
        }

        public async IAsyncEnumerable<(SourceRepository Repository, IPackageSearchMetadata Package)> SearchPackagesAsync(string searchTerm,
            int maxResults = 128,
            bool includePrerelease = false,
            string nugetConfigFilePath = "")
        {
            var providers = GetNugetResourceProviders();

            var packageRootFolder = "";

            if (!string.IsNullOrWhiteSpace(Assembly.GetEntryAssembly()?.Location))
            {
                packageRootFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
            }

            ISettings settings;

            if (!string.IsNullOrWhiteSpace(nugetConfigFilePath))
            {
                settings = Settings.LoadSettingsGivenConfigPaths(new List<string>() { nugetConfigFilePath });
            }
            else
            {
                settings = Settings.LoadDefaultSettings(packageRootFolder ?? "", null, new MachineWideSettings());
            }

            var packageSourceProvider = new PackageSourceProvider(settings);
            var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, providers);

            var repositories = sourceRepositoryProvider.GetRepositories();

            var packages = GetPackages(searchTerm, maxResults, includePrerelease, repositories);

            await foreach (var package in packages)
            {
                yield return package;
            }
        }

        private async IAsyncEnumerable<(SourceRepository Repository, IPackageSearchMetadata Package)> GetPackages(string searchTerm,
            int maxResults, bool includePrerelease, IEnumerable<SourceRepository> repositories)
        {
            foreach (var repository in repositories)
            {
                PackageSearchResource packageSearchResource;

                try
                {
                    packageSearchResource = await repository.GetResourceAsync<PackageSearchResource>();
                }
                catch (FatalProtocolException ex)
                {
                    _logger.LogError($"Failed to download package search resource: {ex}");
                    continue;
                }

                SearchFilter searchFilter;

                if (includePrerelease)
                {
                    searchFilter = new SearchFilter(true, SearchFilterType.IsAbsoluteLatestVersion);
                }
                else
                {
                    searchFilter = new SearchFilter(false);
                }

                var items = await packageSearchResource.SearchAsync(searchTerm, searchFilter, 0, maxResults, _logger, CancellationToken.None);

                foreach (var packageSearchMetadata in items)
                {
                    yield return (repository, packageSearchMetadata);
                }
            }
        }

        public async IAsyncEnumerable<(SourceRepository Repository, IPackageSearchMetadata Package)> SearchPackagesAsync(NuGetFeed packageFeed,
            string searchTerm, int maxResults = 128,
            bool includePrerelease = false)
        {
            var providers = GetNugetResourceProviders();
            var sourceRepo = GetSourceRepo(packageFeed, providers);

            var packages = GetPackages(searchTerm, maxResults, includePrerelease, new List<SourceRepository> { sourceRepo });

            await foreach (var package in packages)
            {
                yield return package;
            }
        }

        private async Task<IPackageSearchMetadata> SearchPackageAsync(string packageName, string version, bool includePrerelease,
            SourceRepository sourceRepository)
        {
            var packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();
            var sourceCacheContext = new SourceCacheContext();

            IPackageSearchMetadata packageMetaData = null;

            if (!string.IsNullOrEmpty(version) && !version.Contains('*'))
            {
                if (NuGetVersion.TryParse(version, out var nugetversion))
                {
                    var packageIdentity = new PackageIdentity(packageName, NuGetVersion.Parse(version));

                    packageMetaData = await packageMetadataResource.GetMetadataAsync(
                        packageIdentity,
                        sourceCacheContext,
                        _logger,
                        CancellationToken.None);
                }
            }
            else
            {
                // Can't use await as we seem to lose the thread
                var searchResults = packageMetadataResource.GetMetadataAsync(
                    packageName,
                    includePrerelease,
                    includeUnlisted: false,
                    sourceCacheContext,
                    _logger,
                    CancellationToken.None).Result;

                searchResults = searchResults
                    .OrderByDescending(p => p.Identity.Version);

                if (!string.IsNullOrEmpty(version))
                {
                    var searchPattern = version.Replace("*", ".*");
                    searchResults = searchResults.Where(p => Regex.IsMatch(p.Identity.Version.ToString(), searchPattern));
                }

                packageMetaData = searchResults.FirstOrDefault();
            }

            return packageMetaData;
        }
    }
}
