using System.Text.RegularExpressions;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class PrefabContractTests
{
    [TestMethod]
    public void PrefabAndModuleUseTheSameParameterContract()
    {
        string prefab = File.ReadAllText(RepoFile("Modules", "OSCLeash", "Unity", "OSCLeash.prefab"));
        string module = File.ReadAllText(RepoFile("Modules", "OSCLeash", "OSCLeashModule.cs"));
        string[] parameters = ["Leash_Z+", "Leash_Z-", "Leash_X+", "Leash_X-", "Leash_Y+", "Leash_Y-"];

        Assert.IsTrue(prefab.Contains("parameter: Leash", StringComparison.Ordinal));
        foreach (string parameter in parameters)
        {
            Assert.AreEqual(1, Regex.Matches(prefab, $"parameter: {Regex.Escape(parameter)}(?:\\r?\\n)").Count);
            Assert.IsTrue(module.Contains($"\"{parameter}\"", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void InstallationContractPointsToReleasePackageAndDocumentsAutoSetup()
    {
        string readme = File.ReadAllText(RepoFile("Modules", "OSCLeash", "README.md"));
        string module = File.ReadAllText(RepoFile("Modules", "OSCLeash", "OSCLeashModule.cs"));

        Assert.IsTrue(readme.Contains("Leash Start Bone", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("Auto Setup", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains(
            "https://github.com/CrookedToe/CrookedToe-s-Modules/releases/latest",
            StringComparison.Ordinal));
        Assert.IsTrue(module.Contains(
            "https://github.com/CrookedToe/CrookedToe-s-Modules/releases/latest",
            StringComparison.Ordinal));
    }

    private static string RepoFile(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate repository file: {Path.Combine(parts)}");
    }
}
