using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using NuGet.Protocol.Core.Types;
using Xunit;

namespace Weikio.NugetDownloader.Tests
{
    /*
     * Note that these test depend on public NuGet feed and expect certain packages to be available.
     * This tests can fail without notice if feed is down or package is removed.
     */

    public class NugetDownloaderTests : IDisposable
    {
        private readonly string _packagesFolderInTestsBin;
        private const string _packageFromNugetOrgName = "Newtonsoft.Json";
        private const string _packageFromThirdPartyFeedName = "DummyProject";
        private const string _thirdPartyFeedName = "AdafyPublic";
        private const string _thirdPartyFeed = "https://pkgs.dev.azure.com/adafy/df962856-ce0c-4e96-8999-bee7c8b0582c/_packaging/AdafyPublic/nuget/v3/index.json";

        public NugetDownloaderTests()
        {
            var executingAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _packagesFolderInTestsBin = Path.Combine(executingAssemblyDir!, "FeedTestPackages");
        }

        public void Dispose()
        {
            if (Directory.Exists(_packagesFolderInTestsBin))
            {
                Directory.Delete(_packagesFolderInTestsBin, true);
            }
        }

        [Fact]
        public async Task DownloadsPackageFromNugetOrg()
        {
            // Arrange
            var packageFolder = Path.Combine(_packagesFolderInTestsBin, nameof(DownloadsPackageFromNugetOrg));
            var downloader = new NuGetDownloader();

            // Act
            var assemblies = await downloader.DownloadAsync(packageFolder, _packageFromNugetOrgName);

            // Assert
            assemblies.Should().NotBeEmpty("because return value should contain assembly name.");
            assemblies.Should().ContainMatch($"*{_packageFromNugetOrgName}*", "because package contains assembly with package name.");

            var directories = Directory.GetDirectories(packageFolder);
            directories.Should().ContainMatch($"*{_packageFromNugetOrgName}*", "because package is installed to the directory");

            var files = Directory.GetFiles(packageFolder, "*.*", SearchOption.TopDirectoryOnly);
            files.Should().NotBeEmpty("because NuGet package has been downloaded");
            files.Should().ContainMatch("*.dll", "because NuGet packages has dll file.");
        }

        [Fact]
        public async Task DownloadsOnlyPackageFromNugetOrg()
        {
            // Arrange
            var packageFolder = Path.Combine(_packagesFolderInTestsBin, nameof(DownloadsOnlyPackageFromNugetOrg));
            var downloader = new NuGetDownloader();

            // Act
            var packageFiles = await downloader.DownloadAsync(packageFolder, _packageFromNugetOrgName, null, false, null, true);

            // Assert
            packageFiles.Should().NotBeEmpty("because return value should contain listing of files in package.");

            var directories = Directory.GetDirectories(packageFolder);
            directories.Should().ContainMatch($"*{_packageFromNugetOrgName}*", "because package is installed to the directory");

            var files = Directory.GetFiles(packageFolder, "*.*", SearchOption.TopDirectoryOnly);
            files.Should().BeEmpty("because package is only download but not extracted");
        }

        [Fact]
        public async Task DownloadsCorrectVersionFromNugetOrg()
        {
            // Arrange
            var packageFolder = Path.Combine(_packagesFolderInTestsBin, nameof(DownloadsCorrectVersionFromNugetOrg));
            var version = "12.0.1";
            var downloader = new NuGetDownloader();

            // Act
            var assemblies = await downloader.DownloadAsync(packageFolder, _packageFromNugetOrgName, version, false, null, false);

            // Assert
            assemblies.Should().NotBeEmpty("because return value should contain assembly name.");
            assemblies.Should().ContainMatch($"*{_packageFromNugetOrgName}*", "because package contains assembly with package name.");

            var directories = Directory.GetDirectories(packageFolder);
            directories.Should().ContainMatch($"*{_packageFromNugetOrgName}.{version}*", "because package is installed to the directory");
        }

        [Fact]
        public async Task ExtractsCorrectFrameworkVersionFromNugetOrg()
        {
            // Arrange
            var dotnetFramework = GetDotnetFrameworkName();
            var packageFolder = Path.Combine(_packagesFolderInTestsBin, nameof(DownloadsCorrectVersionFromNugetOrg));
            var downloader = new NuGetDownloader();

            // Act
            await downloader.DownloadAsync(packageFolder, _packageFromNugetOrgName, null, false, null, true);

            // Assert
            var dllFiles = Directory.GetFiles(packageFolder, "*.dll", SearchOption.TopDirectoryOnly);
            var assembiles = dllFiles.Select(Assembly.Load);

            foreach (var assembly in assembiles)
            {
                AssertAssemblyFrameWork(dotnetFramework, assembly);
            }
        }

        [Fact]
        public async Task DownloadsPackageFromThirdPartyFeed()
        {
            // Arrange
            var packageFolder = Path.Combine(_packagesFolderInTestsBin, nameof(DownloadsPackageFromThirdPartyFeed));
            var downloader = new NuGetDownloader();

            // Act
            var assemblies = await downloader.DownloadAsync(packageFolder, _packageFromThirdPartyFeedName, null, false,
                new NuGetFeed(_thirdPartyFeedName, _thirdPartyFeed));

            // Assert
            assemblies.Should().NotBeEmpty("because return value should contain assembly name.");
            assemblies.Should().ContainMatch($"*{_packageFromThirdPartyFeedName}*", "because package contains assembly with package name.");

            var directories = Directory.GetDirectories(packageFolder);
            directories.Should().ContainMatch($"*{_packageFromThirdPartyFeedName}*", "because package is installed to the directory");

            var files = Directory.GetFiles(packageFolder, "*.*", SearchOption.TopDirectoryOnly);
            files.Should().NotBeEmpty("because NuGet package has been downloaded");
            files.Should().ContainMatch("*.dll", "because NuGet packages has dll file.");
        }


        [Fact]
        public async Task DownloadsPreleasePackageFromThirdpartyFeed()
        {
            // Arrange
            var version = "1.2.3-beta";
            var packageFolder = Path.Combine(_packagesFolderInTestsBin, nameof(DownloadsPreleasePackageFromThirdpartyFeed));
            var downloader = new NuGetDownloader();

            // Act
            var assemblies = await downloader.DownloadAsync(packageFolder, _packageFromThirdPartyFeedName, version, true,
                new NuGetFeed(_thirdPartyFeedName, _thirdPartyFeed));

            // Assert
            assemblies.Should().NotBeEmpty("because return value should contain assembly name.");
            assemblies.Should().ContainMatch($"*{_packageFromThirdPartyFeedName}*", "because package contains assembly with package name.");

            var directories = Directory.GetDirectories(packageFolder);
            directories.Should().ContainMatch($"*{_packageFromThirdPartyFeedName}.{version}*", "because package is installed to the directory");
        }

        [Fact]
        public async Task SearchesPackageFromNugetOrg()
        {
            // Arrange
            var downloader = new NuGetDownloader();

            // Act
            var packages = new List<IPackageSearchMetadata>();
            var results = downloader.SearchPackagesAsync(_packageFromNugetOrgName);
            await foreach (var result in results)
            {
                packages.Add(result.Package);
            }

            // Assert
            packages.Should().NotBeEmpty("because search should find some packages");
            packages.Select(p => p.Title).Should().ContainMatch($"*{_packageFromNugetOrgName}*", "because feed should contain the package.");
        }

        [Fact]
        public async Task SearchesPackageFromThirdPartyFeed()
        {
            // Arrange
            var downloader = new NuGetDownloader();

            // Act
            var packages = new List<IPackageSearchMetadata>();
            var results = downloader.SearchPackagesAsync(new NuGetFeed(_thirdPartyFeedName, _thirdPartyFeed), _packageFromThirdPartyFeedName);
            await foreach (var result in results)
            {
                packages.Add(result.Package);
            }

            // Assert
            packages.Should().NotBeEmpty("because search should find some packages");
            packages.Select(p => p.Title).Should().ContainMatch($"*{_packageFromThirdPartyFeedName}*", "because feed should contain the package.");
        }

        [Fact]
        public async Task SearchesPrereleasePackageFromThirdPartyFeed()
        {
            // Arrange
            var version = "1.2.3-beta";
            var downloader = new NuGetDownloader();

            // Act
            var packages = new List<IPackageSearchMetadata>();
            var results = downloader.SearchPackagesAsync(
                new NuGetFeed(_thirdPartyFeedName, _thirdPartyFeed), _packageFromThirdPartyFeedName, 128, true);
            await foreach (var result in results)
            {
                packages.Add(result.Package);
            }

            // Assert
            packages.Should().NotBeEmpty("because search should find some packages");
            packages.Select(p => p.Identity.Version.ToString()).Should().ContainMatch($"*{version}*",
                "because feed should contain the package.");
        }

        private string GetDotnetFrameworkName()
        {
            var dotNetFramework = Assembly
                .GetEntryAssembly()
                .GetCustomAttribute<TargetFrameworkAttribute>()?
                .FrameworkName;

            return dotNetFramework;
        }

        private void AssertAssemblyFrameWork(string targetFramework, Assembly assembly)
        {
            var assemblyFramework = assembly
                .GetCustomAttribute<TargetFrameworkAttribute>()?
                .FrameworkName;

            assemblyFramework.Should().Be(targetFramework, "because only target framework version should be extracted.");
        }
    }
}
