using System.Reflection;
using Chandlery.Services;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Chandlery.Sync.Tests;

public class ServiceRegistrationTests
{
    private static string ProgramSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new FileNotFoundException("Program.cs not found above the test folder."), "Program.cs"));
    }

    [Fact]
    public void EveryServiceAPageInjects_IsRegisteredInProgram()
    {
        var program = ProgramSource();
        var injected = typeof(CrewService).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null && p.PropertyType.Namespace == typeof(CrewService).Namespace && p.PropertyType.IsClass)
            .Select(p => p.PropertyType).Distinct().ToList();

        Assert.NotEmpty(injected);
        // A page that injects a service nobody registered fails when it is opened, and the menu injects several, so every page would fail.
        var missing = injected.Where(t => !program.Contains($"<{t.Name}>")).Select(t => t.Name).OrderBy(n => n).ToList();
        Assert.True(missing.Count == 0, "Not registered in Program.cs: " + string.Join(", ", missing));
    }
}
