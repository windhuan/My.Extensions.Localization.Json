using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace My.Extensions.Localization.Json.Internal;

public class JsonResourceManager
{
    private readonly JsonFileWatcher _jsonFileWatcher;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _resourcesCache = new();
    private readonly object _debounceLock = new();
    private CancellationTokenSource _debounce_cts = null;
    public JsonResourceManager(string resourcesPath, string resourceName = null, bool autoCreateMissingKey = false)
    {
        ResourcesPath = resourcesPath;
        ResourceName = resourceName;
        AutoCreateMissingKey = autoCreateMissingKey;
        if (!System.IO.Directory.Exists(resourcesPath))
        {
            System.IO.Directory.CreateDirectory(resourcesPath);
        }
        _jsonFileWatcher = new(resourcesPath);
        _jsonFileWatcher.Changed += RefreshResourcesCache;
    }

    public string ResourceName { get; }

    public string ResourcesPath { get; }

    public string ResourcesFilePath { get; private set; }

    public bool AutoCreateMissingKey { get; set; }


    public virtual ConcurrentDictionary<string, string> GetResourceSet(CultureInfo culture, bool tryParents)
    {
        TryLoadResourceSet(culture);

        var key = $"{ResourceName}.{culture.Name}";

        if (!AutoCreateMissingKey)
        {
            if (!_resourcesCache.ContainsKey(key))
            {
                return null;
            }
        }
        else
        {
            _resourcesCache.GetOrAdd(key, (key) =>
            {
                return new ConcurrentDictionary<string, string>();
            });
        }

        if (tryParents)
        {
            var allResources = new ConcurrentDictionary<string, string>();
            do
            {
                if (_resourcesCache.TryGetValue(key, out var resources))
                {
                    foreach (var entry in resources)
                    {
                        allResources.TryAdd(entry.Key, entry.Value);
                    }
                }

                culture = culture.Parent;
            } while (culture != CultureInfo.InvariantCulture);

            return allResources;
        }
        else
        {
            _resourcesCache.TryGetValue(key, out var resources);

            return resources;
        }
    }

    public virtual string GetString(string name)
    {
        return GetString(name, CultureInfo.CurrentUICulture, tryParents: true);
    }

    public virtual string GetString(string name, CultureInfo culture, bool tryParents = false)
    {
        if (string.IsNullOrEmpty(culture.Name))
        {
            culture = new CultureInfo("zh-CN");
        }
        var original_key = $"{ResourceName}.{culture.Name}";
        GetResourceSet(culture, tryParents: true);

        if (_resourcesCache.IsEmpty)
        {
            return null;
        }

        do
        {
            var key = $"{ResourceName}.{culture.Name}";
            if (_resourcesCache.TryGetValue(key, out var resources))
            {
                if (resources.TryGetValue(name, out var value))
                {
                    return value.ToString();
                }
            }
            culture = culture.Parent;
        } while (tryParents && culture != culture.Parent);

        if (AutoCreateMissingKey)
        {
            if (_resourcesCache.TryGetValue(original_key, out var dict))
            {
                return dict.GetOrAdd(name, (n) =>
                {
                    Debounce(1000, () =>
                    {
                        var filePath = Path.Combine(ResourcesPath, original_key + ".json");
                        _jsonFileWatcher.Changed -= RefreshResourcesCache;
                        string jsonStr = System.Text.Json.JsonSerializer.Serialize(dict, options: new System.Text.Json.JsonSerializerOptions()
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                        File.WriteAllText(filePath, jsonStr);
                        _jsonFileWatcher.Changed += RefreshResourcesCache;
                    });
                    return name;
                });
            }
        }
        return null;
    }

    private void TryLoadResourceSet(CultureInfo culture)
    {
        if (string.IsNullOrEmpty(ResourceName))
        {
            var file = Path.Combine(ResourcesPath, $"{culture.Name}.json");

            TryAddResources(file);
        }
        else
        {
            var resourceFiles = Enumerable.Empty<string>();
            var rootCulture = culture.Name[..2];
            if (ResourceName.Contains('.'))
            {
                resourceFiles = Directory.EnumerateFiles(ResourcesPath, $"{ResourceName}.{rootCulture}*.json");

                if (!resourceFiles.Any())
                {
                    resourceFiles = GetResourceFiles(rootCulture);
                }
            }
            else
            {
                resourceFiles = GetResourceFiles(rootCulture);
            }

            foreach (var file in resourceFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var cultureName = fileName[(fileName.LastIndexOf('.') + 1)..];

                culture = CultureInfo.GetCultureInfo(cultureName);

                TryAddResources(file);
            }
        }

        IEnumerable<string> GetResourceFiles(string culture)
        {
            var resourcePath = ResourceName.Replace('.', Path.AltDirectorySeparatorChar);
            var resourcePathLastDirectorySeparatorIndex = resourcePath.LastIndexOf(Path.AltDirectorySeparatorChar);
            var resourceName = resourcePath[(resourcePathLastDirectorySeparatorIndex + 1)..];
            var resourcesPath = resourcePathLastDirectorySeparatorIndex == -1
                ? ResourcesPath
                : Path.Combine(ResourcesPath, resourcePath[..resourcePathLastDirectorySeparatorIndex]);

            return Directory.Exists(resourcesPath)
                ? Directory.EnumerateFiles(resourcesPath, $"{resourceName}.{culture}*.json")
                : new string[0];
        }

        void TryAddResources(string resourceFile)
        {
            var key = $"{ResourceName}.{culture.Name}";
            if (!_resourcesCache.ContainsKey(key))
            {
                var resources = JsonResourceLoader.Load(resourceFile);

                _resourcesCache.TryAdd(key, new ConcurrentDictionary<string, string>(resources));
            }
        }
    }

    private void RefreshResourcesCache(object sender, FileSystemEventArgs e)
    {
        var key = Path.GetFileNameWithoutExtension(e.FullPath);
        if (_resourcesCache.TryGetValue(key, out var resources))
        {
            if (!resources.IsEmpty)
            {
                resources.Clear();

                var freshResources = JsonResourceLoader.Load(e.FullPath);

                foreach (var item in freshResources)
                {
                    _resourcesCache[key].TryAdd(item.Key, item.Value);
                }
            }
        }
    }

    /// <summary>
    /// 延迟执行指定操作，若在延迟期间再次调用则重置延迟。
    /// </summary>
    /// <param name="delay">延迟时间（毫秒）</param>
    /// <param name="action">要执行的操作</param>
    public void Debounce(int delay, Action action)
    {
        lock (_debounceLock)
        {
            _debounce_cts?.Cancel();
            _debounce_cts = new CancellationTokenSource();
            var token = _debounce_cts.Token;

            Task.Delay(delay, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    action();
                }
            }, TaskScheduler.Default);
        }
    }
}