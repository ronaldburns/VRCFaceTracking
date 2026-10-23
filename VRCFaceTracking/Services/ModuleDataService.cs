using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Helpers;
using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Services;

public class ModuleDataService : IModuleDataService
{
    private IEnumerable<InstallableTrackingModule>? _remoteModules;
    private readonly Dictionary<Guid, int> _ratingCache = new();

    private readonly IIdentityService _identityService;
    private readonly ILogger<ModuleDataService> _logger;
    private readonly HttpClient _httpClient;

    private const string BaseUrl = "https://registry.vrcft.io/";

    // Modules that aren't in the official registry yet, listed from a GitHub release. Each is pinned to a release that
    // was reviewed, and its zip has to match the SHA256, so a new or replaced upload doesn't install itself.
    // The repo needs a module.json at the release tag, and the release a zip with the module's DLL at its root
    private static readonly GithubModule[] GithubModules =
    [
        // Reviewed 2026-10-23: only receives on its UDP port and reads its optional config file
        new("sasaken1102r/VRCFT-FrameEyeOSC", "v1.0.1", "6c4a5916432f82645cedb0af2ca63ea25b09f40988ea30124513ada8d76340ba"),
    ];

    private record GithubModule(string Repo, string Tag, string Sha256);

    public ModuleDataService(IIdentityService identityService, ILogger<ModuleDataService> logger)
    {
        _identityService = identityService;
        _logger = logger;
        _httpClient = HappyEyeballsHttp.CreateHttpClient();
        _httpClient.BaseAddress = new Uri(BaseUrl);
    }

    private async Task<IEnumerable<InstallableTrackingModule>> AllModules()
    {
        try
        {
            // This is where we make the actual request to the API at modules and get the list of modules.
            var response = await _httpClient.GetAsync("modules");
            if (!response.IsSuccessStatusCode)
            {
                return new List<InstallableTrackingModule>();
            }
            
            var content = await response.Content.ReadAsStringAsync();
            return await Json.ToObjectAsync<List<InstallableTrackingModule>>(content);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Exception trying to get modules from module registry: {e}", e.Message);
            return new List<InstallableTrackingModule>();
        }
    }

    private async Task<InstallableTrackingModule?> GetGithubModule(GithubModule pin)
    {
        var (repo, tag, sha256) = pin;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VRCFaceTracking", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get release {Tag} of {Repo}. Status code: {StatusCode}", tag, repo, response.StatusCode);
                return null;
            }

            var release = JObject.Parse(await response.Content.ReadAsStringAsync());
            var zipUrl = release["assets"]?
                .Select(asset => (string?)asset["browser_download_url"])
                .FirstOrDefault(url => url?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false);
            if (zipUrl == null)
            {
                _logger.LogWarning("Release {Tag} of {Repo} has no zip to install", tag, repo);
                return null;
            }

            var moduleJson = await _httpClient.GetStringAsync($"https://raw.githubusercontent.com/{repo}/{tag}/module.json");
            var module = JsonConvert.DeserializeObject<InstallableTrackingModule>(moduleJson);
            if (module == null)
            {
                return null;
            }

            module.DownloadUrl = zipUrl;
            module.FileSha256 = sha256;
            module.LastUpdated = (DateTime?)release["published_at"] ?? module.LastUpdated;
            module.ModulePageUrl ??= $"https://github.com/{repo}";
            return module;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Exception trying to get module {Repo} from GitHub: {e}", repo, e.Message);
            return null;
        }
    }

    public async Task<IEnumerable<InstallableTrackingModule>> GetRemoteModules()
    {
        if (_remoteModules == null)
        {
            var modules = new List<InstallableTrackingModule>(await AllModules());

            // Once a module is in the official registry, its entry there is used instead
            var githubModules = await Task.WhenAll(GithubModules.Select(GetGithubModule));
            modules.AddRange(githubModules.OfType<InstallableTrackingModule>()
                .Where(githubModule => modules.All(m => m.ModuleId != githubModule.ModuleId)));

            _remoteModules = modules;
        }

        return new List<InstallableTrackingModule>(_remoteModules);
    }

    public async Task IncrementDownloadsAsync(TrackingModuleMetadata moduleMetadata)
    {
        // Send a PATCH request to the downloads endpoint with the module ID in the body
        var rating = new RatingObject
            { UserId = _identityService.GetUniqueUserId(), ModuleId = moduleMetadata.ModuleId.ToString() };
        var content = new StringContent(JsonConvert.SerializeObject(rating), Encoding.UTF8, "application/json");
        var response = await _httpClient.PatchAsync("downloads", content);
        
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        _logger.LogError("Failed to increment downloads for {ModuleId}. Status code: {StatusCode}", moduleMetadata.ModuleId, response.StatusCode);
    }

    public IEnumerable<InstallableTrackingModule> GetLegacyModules()
    {
        if (!Directory.Exists(Core.Utils.CustomLibsDirectory))
        {
            Directory.CreateDirectory(Core.Utils.CustomLibsDirectory);
        }

        var moduleDlls = Directory.GetFiles(Core.Utils.CustomLibsDirectory, "*.dll");

        return moduleDlls.Select(moduleDll => new InstallableTrackingModule
        {
            AssemblyLoadPath = moduleDll,
            DllFileName = Path.GetFileName(moduleDll),
            ModuleId = Guid.Empty,
            ModuleName = Path.GetFileNameWithoutExtension(moduleDll),
            ModuleDescription = "Legacy module",
            AuthorName = "Local",
            ModulePageUrl = "file:///" + Path.GetDirectoryName(moduleDll)
        });
    }

    public async Task<int?> GetMyRatingAsync(TrackingModuleMetadata moduleMetadata)
    {
        try
        {
            if (_ratingCache.TryGetValue(moduleMetadata.ModuleId, out var async))
            {
                _logger.LogDebug("Rating for {ModuleId} was cached as {Rating}", moduleMetadata.ModuleId, async);
                return async;
            }

            var rating = new RatingObject
                { UserId = _identityService.GetUniqueUserId(), ModuleId = moduleMetadata.ModuleId.ToString() };
        
            var response = await _httpClient.SendAsync(new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri("rating", UriKind.Relative),
                Content = new StringContent(JsonConvert.SerializeObject(rating), Encoding.UTF8, "application/json"),
            });

            // Deserialize the input content but extract the Rating property. Be careful though, we might 404 if the user hasn't rated the module yet.
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogDebug("Failed to get user rating for module {ModuleId}", moduleMetadata.ModuleId);
                return null;
            }

            var ratingResponse = await Json.ToObjectAsync<RatingObject>(await response.Content.ReadAsStringAsync());
            
            _logger.LogDebug("Rating for {ModuleId} was {Rating}. Caching...", moduleMetadata.ModuleId, ratingResponse.Rating);
            _ratingCache[moduleMetadata.ModuleId] = ratingResponse.Rating;
            return ratingResponse.Rating;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to get user rating for module {ModuleId}. Exception: {Exception}", moduleMetadata.ModuleId, e.Message);
            return null;
        }
    }

    public async Task SetMyRatingAsync(TrackingModuleMetadata moduleMetadata, int rating)
    {
        try
        {
            // Same format as get but we PUT this time
            var ratingObject = new RatingObject
            {
                UserId = _identityService.GetUniqueUserId(), ModuleId = moduleMetadata.ModuleId.ToString(),
                Rating = rating
            };

            var content = new StringContent(JsonConvert.SerializeObject(ratingObject), Encoding.UTF8,
                "application/json");
            var response = await _httpClient.PutAsync("rating", content);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("Failed to set rating for {ModuleId} to {Rating}. Status code: {StatusCode}",
                    moduleMetadata.ModuleId, rating, response.StatusCode);
                return;
            }

            _logger.LogDebug("Rating for {ModuleId} was set to {Rating}. Caching...", moduleMetadata.ModuleId, rating);
            _ratingCache[moduleMetadata.ModuleId] = rating;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to set rating for module {ModuleId}. Exception: {Exception}", moduleMetadata.ModuleId, e.Message);
        }
    }

    public IEnumerable<InstallableTrackingModule> GetInstalledModules()
    {
        if (!Directory.Exists(Core.Utils.CustomLibsDirectory))
        {
            Directory.CreateDirectory(Core.Utils.CustomLibsDirectory);
        }

        // Check each folder in our CustomModulesDir folder and see if it has a module.json file.
        // If it does, deserialize it and add it to the list of installed modules.
        var installedModules = new List<InstallableTrackingModule>();
        var moduleFolders = Directory.GetDirectories(Core.Utils.CustomLibsDirectory);
        foreach (var moduleFolder in moduleFolders)
        {
            var moduleJsonPath = Path.Combine(moduleFolder, "module.json");
            if (!File.Exists(moduleJsonPath))
            {
                continue;
            }

            var moduleJson = File.ReadAllText(moduleJsonPath);
            try
            {
                var module = JsonConvert.DeserializeObject<InstallableTrackingModule>(moduleJson);
                module.AssemblyLoadPath = Path.Combine(moduleFolder, module.DllFileName);
                installedModules.Add(module);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to deserialize module.json for {ModuleFolder}", moduleFolder);
            }
        }

        return installedModules;
    } 
}