namespace Chandlery.Services;

/// <summary>Where uploaded files live. The app only talks to this interface, so disk can later be swapped for blob storage.</summary>
public interface IFileStore
{
    /// <summary>Saves the stream and returns the key to read it back with.</summary>
    Task<string> SaveAsync(int companyId, Stream content, CancellationToken ct = default);
    Stream? OpenRead(string key);
    void Delete(string key);
}

/// <summary>Stores files under App_Data/invoices/{companyId}/{guid}, outside wwwroot so nothing is served without a permission check.</summary>
public class DiskFileStore(IWebHostEnvironment environment) : IFileStore
{
    private string Root => Path.Combine(environment.ContentRootPath, "App_Data", "invoices");

    public async Task<string> SaveAsync(int companyId, Stream content, CancellationToken ct = default)
    {
        var key = $"{companyId}/{Guid.NewGuid():N}";
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Stream? OpenRead(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Keys are generated here, but resolve defensively so a bad key can never leave the invoices folder.</summary>
    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(Root, key));
        if (!full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid file key.");
        return full;
    }
}
