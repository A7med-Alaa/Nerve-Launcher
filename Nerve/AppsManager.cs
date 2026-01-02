using IWshRuntimeLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Nerve
{
    /// <summary>
    /// Represents information about an installed application.
    /// Used for both Start Menu shortcuts and UWP apps.
    /// </summary>
    public struct AppInfo
    {
        public string Name { get; set; }
        public string ShortcutPath { get; set; }
        public string ExecutablePath { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string InstallLocation { get; set; }
        public ImageSource Icon { get; set; }
        public string IconPath { get; set; }
        public bool IsUWP { get; set; }
        public string PackageFamilyName { get; set; }
    }

    /// <summary>
    /// Serializable version of AppInfo for JSON caching (excludes ImageSource).
    /// </summary>
    public struct CachedAppInfo
    {
        public string Name { get; set; }
        public string ShortcutPath { get; set; }
        public string ExecutablePath { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string InstallLocation { get; set; }
        public string IconPath { get; set; }
        public bool IsUWP { get; set; }
        public string PackageFamilyName { get; set; }
    }

    /// <summary>
    /// Manages app cache for faster startup.
    /// Saves app list to JSON on disk and loads icons on startup.
    /// </summary>
    public static class AppCache
    {
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nerve");
        private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "apps_cache.json");

        /// <summary>
        /// Loads cached app list from disk.
        /// Returns null if cache doesn't exist or is corrupted.
        /// </summary>
        public static List<CachedAppInfo> LoadFromCache()
        {
            try
            {
                if (!System.IO.File.Exists(CacheFilePath))
                    return null;
                return JsonSerializer.Deserialize<List<CachedAppInfo>>(System.IO.File.ReadAllText(CacheFilePath));
            }
            catch { return null; }
        }

        /// <summary>
        /// Saves app list to disk cache for faster next startup.
        /// </summary>
        public static void SaveToCache(List<AppInfo> apps)
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                    Directory.CreateDirectory(CacheDirectory);

                var cachedApps = apps.Select(a => new CachedAppInfo
                {
                    Name = a.Name,
                    ShortcutPath = a.ShortcutPath,
                    ExecutablePath = a.ExecutablePath,
                    Publisher = a.Publisher,
                    Version = a.Version,
                    InstallLocation = a.InstallLocation,
                    IconPath = a.IconPath,
                    IsUWP = a.IsUWP,
                    PackageFamilyName = a.PackageFamilyName
                }).ToList();

                System.IO.File.WriteAllText(CacheFilePath, 
                    JsonSerializer.Serialize(cachedApps, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { }
        }

        /// <summary>
        /// Loads icons for cached apps in parallel for fast startup.
        /// </summary>
        public static List<AppInfo> LoadIconsFromCache(List<CachedAppInfo> cachedApps)
        {
            var results = new ConcurrentBag<AppInfo>();

            Parallel.ForEach(cachedApps, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, cached =>
            {
                ImageSource icon = null;
                try
                {
                    if (!string.IsNullOrEmpty(cached.IconPath) && System.IO.File.Exists(cached.IconPath))
                        icon = IconHelper.LoadImageFromPath(cached.IconPath);
                    else if (!string.IsNullOrEmpty(cached.ExecutablePath) && System.IO.File.Exists(cached.ExecutablePath))
                        icon = IconHelper.GetExecutableIcon(cached.ExecutablePath);
                    else if (cached.IsUWP && !string.IsNullOrEmpty(cached.InstallLocation))
                        icon = UwpAppsProvider.GetIconFromInstallLocation(cached.InstallLocation).Icon;
                }
                catch { }

                results.Add(new AppInfo
                {
                    Name = cached.Name,
                    ShortcutPath = cached.ShortcutPath,
                    ExecutablePath = cached.ExecutablePath,
                    Publisher = cached.Publisher,
                    Version = cached.Version,
                    InstallLocation = cached.InstallLocation,
                    Icon = icon,
                    IconPath = cached.IconPath,
                    IsUWP = cached.IsUWP,
                    PackageFamilyName = cached.PackageFamilyName
                });
            });

            return results.OrderBy(a => a.Name).ToList();
        }
    }

    /// <summary>
    /// Utility class for extracting icons from executables and shortcuts.
    /// </summary>
    internal static class IconHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Extracts icon from an .exe file using Shell32 API.
        /// </summary>
        public static ImageSource GetExecutableIcon(string exePath, int iconIndex = 0)
        {
            if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                return null;

            IntPtr hIcon = ExtractIcon(IntPtr.Zero, exePath, iconIndex);
            if (hIcon == IntPtr.Zero || hIcon == new IntPtr(1))
                return null;

            try
            {
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                imageSource.Freeze();
                return imageSource;
            }
            finally { DestroyIcon(hIcon); }
        }

        /// <summary>
        /// Resolves target path from a .lnk shortcut file.
        /// </summary>
        public static string GetShortcutTarget(string shortcutPath)
        {
            try
            {
                var shell = new WshShell();
                var shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);
                return shortcut.TargetPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gets custom icon location from a shortcut (if set).
        /// Returns path and icon index.
        /// </summary>
        public static (string Path, int Index) GetShortcutIconLocation(string shortcutPath)
        {
            try
            {
                var shell = new WshShell();
                var shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);
                string iconLocation = shortcut.IconLocation;

                if (!string.IsNullOrEmpty(iconLocation))
                {
                    int lastComma = iconLocation.LastIndexOf(',');
                    if (lastComma > 0 && int.TryParse(iconLocation.Substring(lastComma + 1), out int index))
                        return (iconLocation.Substring(0, lastComma), index);
                    return (iconLocation, 0);
                }
            }
            catch { }
            return (null, 0);
        }

        /// <summary>
        /// Loads an image file (PNG, ICO) and decodes at specified size.
        /// </summary>
        public static ImageSource LoadImageFromPath(string path, int decodeSize = 256)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = decodeSize;
                bitmap.DecodePixelHeight = decodeSize;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Retrieves applications from the Windows Start Menu.
    /// Scans .lnk shortcut files and extracts icons from target executables.
    /// </summary>
    public static class StartMenuAppsProvider
    {
        private static readonly string[] ExcludePatterns = {
            "uninstall", "setup", "install", "remove", "readme", "help",
            "documentation", "license", "changelog", "release notes"
        };

        /// <summary>
        /// Scans Start Menu folders and returns list of apps with icons.
        /// </summary>
        public static List<AppInfo> GetApps()
        {
            var apps = new List<AppInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] startMenuPaths = {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            };

            foreach (string startMenuPath in startMenuPaths)
            {
                if (!Directory.Exists(startMenuPath)) continue;

                try
                {
                    foreach (string lnkFile in Directory.GetFiles(startMenuPath, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string appName = Path.GetFileNameWithoutExtension(lnkFile);

                            // Skip uninstallers and duplicates
                            if (ExcludePatterns.Any(p => appName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            if (seenNames.Contains(appName))
                                continue;

                            string targetPath = IconHelper.GetShortcutTarget(lnkFile);
                            if (string.IsNullOrEmpty(targetPath) || !System.IO.File.Exists(targetPath) ||
                                !targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Try shortcut's custom icon first, then fall back to exe icon
                            ImageSource icon = null;
                            string iconPath = null;

                            var iconLocation = IconHelper.GetShortcutIconLocation(lnkFile);
                            if (!string.IsNullOrEmpty(iconLocation.Path) && System.IO.File.Exists(iconLocation.Path))
                            {
                                if (iconLocation.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                                    iconLocation.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                {
                                    icon = IconHelper.LoadImageFromPath(iconLocation.Path);
                                    iconPath = iconLocation.Path;
                                }
                                else
                                {
                                    icon = IconHelper.GetExecutableIcon(iconLocation.Path, iconLocation.Index);
                                }
                            }

                            if (icon == null)
                                icon = IconHelper.GetExecutableIcon(targetPath);

                            if (icon != null)
                            {
                                seenNames.Add(appName);
                                apps.Add(new AppInfo
                                {
                                    Name = appName,
                                    ShortcutPath = lnkFile,
                                    ExecutablePath = targetPath,
                                    Icon = icon,
                                    IconPath = iconPath,
                                    IsUWP = false
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return apps;
        }
    }

    /// <summary>
    /// Retrieves UWP/Microsoft Store applications.
    /// Uses PackageManager API and extracts high-resolution icons from package assets.
    /// </summary>
    public static class UwpAppsProvider
    {
        /// <summary>
        /// Gets all UWP apps installed for current user.
        /// </summary>
        public static async Task<List<AppInfo>> GetAppsAsync()
        {
            var apps = new List<AppInfo>();
            var packageManager = new PackageManager();

            try
            {
                foreach (var package in packageManager.FindPackagesForUser(string.Empty))
                {
                    try
                    {
                        if (package.IsFramework || package.IsResourcePackage)
                            continue;

                        foreach (var appEntry in await package.GetAppListEntriesAsync())
                        {
                            string displayName = appEntry.DisplayInfo.DisplayName;
                            if (string.IsNullOrEmpty(displayName) || displayName.StartsWith("ms-resource:"))
                                continue;

                            string installPath = package.InstalledLocation?.Path;
                            var iconResult = !string.IsNullOrEmpty(installPath) 
                                ? GetIconFromInstallLocation(installPath) 
                                : (null, null);

                            // Fallback to DisplayInfo API if no icon found
                            var icon = iconResult.Icon ?? await GetLogoFromDisplayInfo(appEntry);

                            apps.Add(new AppInfo
                            {
                                Name = displayName,
                                Publisher = package.Id.Publisher,
                                Version = $"{package.Id.Version.Major}.{package.Id.Version.Minor}.{package.Id.Version.Build}",
                                InstallLocation = installPath,
                                Icon = icon,
                                IconPath = iconResult.IconPath,
                                IsUWP = true,
                                PackageFamilyName = package.Id.FamilyName
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return apps;
        }

        /// <summary>
        /// Gets the best available icon from a UWP package install location.
        /// Tries both manifest parsing and asset folder search, picks the larger icon.
        /// </summary>
        public static (ImageSource Icon, string IconPath) GetIconFromInstallLocation(string installPath)
        {
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                return (null, null);

            var manifestResult = (Icon: (ImageSource)null, IconPath: (string)null);
            var assetsResult = (Icon: (ImageSource)null, IconPath: (string)null);

            // Try manifest-based icon search
            string manifestPath = Path.Combine(installPath, "AppxManifest.xml");
            if (System.IO.File.Exists(manifestPath))
                manifestResult = GetIconFromManifest(installPath, manifestPath);

            // Try asset folder search (may find larger icons not in manifest)
            assetsResult = SearchAssetsFolder(installPath);

            // Compare file sizes and return the larger icon
            long manifestSize = GetFileSize(manifestResult.IconPath);
            long assetsSize = GetFileSize(assetsResult.IconPath);

            if (assetsSize > manifestSize && assetsResult.Icon != null)
                return assetsResult;
            if (manifestResult.Icon != null)
                return manifestResult;
            return assetsResult;
        }

        private static long GetFileSize(string path)
        {
            try { return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        /// <summary>
        /// Parses AppxManifest.xml to find logo references, then finds the best scaled version.
        /// </summary>
        private static (ImageSource Icon, string IconPath) GetIconFromManifest(string installPath, string manifestPath)
        {
            try
            {
                string content = System.IO.File.ReadAllText(manifestPath);
                string[] logoPatterns = { "Square310x310Logo=\"", "Square150x150Logo=\"", "Square71x71Logo=\"", "Square44x44Logo=\"", "Logo=\"" };

                var allCandidates = new List<(string Path, int Priority, long Size)>();

                foreach (string pattern in logoPatterns)
                {
                    int startIndex = content.IndexOf(pattern);
                    if (startIndex == -1) continue;

                    startIndex += pattern.Length;
                    int endIndex = content.IndexOf("\"", startIndex);
                    if (endIndex == -1) continue;

                    string relativePath = content.Substring(startIndex, endIndex - startIndex);
                    if (!string.IsNullOrWhiteSpace(relativePath))
                        allCandidates.AddRange(FindAllScaledIcons(installPath, relativePath));
                }

                // Pick best icon by priority (scale/targetsize), then file size
                var best = allCandidates.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Size).FirstOrDefault();

                if (!string.IsNullOrEmpty(best.Path))
                {
                    var icon = IconHelper.LoadImageFromPath(best.Path);
                    if (icon != null) return (icon, best.Path);
                }
            }
            catch { }

            return (null, null);
        }

        /// <summary>
        /// Finds all scaled versions of an icon file (e.g., Logo.scale-200.png, Logo.targetsize-256.png).
        /// </summary>
        private static List<(string Path, int Priority, long Size)> FindAllScaledIcons(string installPath, string relativePath)
        {
            var results = new List<(string Path, int Priority, long Size)>();
            try
            {
                string basePath = Path.Combine(installPath, relativePath);
                string directory = Path.GetDirectoryName(basePath);
                string fileName = Path.GetFileNameWithoutExtension(relativePath);
                string extension = Path.GetExtension(relativePath);

                if (!Directory.Exists(directory)) return results;

                foreach (var file in Directory.GetFiles(directory, $"{fileName}*{extension}", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.Contains("contrast", StringComparison.OrdinalIgnoreCase)))
                {
                    try { results.Add((file, GetScalePriority(file), new FileInfo(file).Length)); }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// Searches Assets folder for icon files (Logo, AppList, Store, Icon patterns).
        /// </summary>
        private static (ImageSource Icon, string IconPath) SearchAssetsFolder(string installPath)
        {
            try
            {
                var allIconFiles = Directory.GetFiles(installPath, "*.png", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("contrast", StringComparison.OrdinalIgnoreCase) &&
                               !f.Contains("SplashScreen", StringComparison.OrdinalIgnoreCase) &&
                               !f.Contains("Wide", StringComparison.OrdinalIgnoreCase) &&
                               (f.Contains("Logo", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("AppList", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("Icon", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("Store", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (allIconFiles.Count == 0) return (null, null);

                var bestFile = allIconFiles
                    .Select(f => new { Path = f, Priority = GetScalePriority(f), Size = new FileInfo(f).Length })
                    .OrderByDescending(x => x.Priority)
                    .ThenByDescending(x => x.Size)
                    .FirstOrDefault();

                if (bestFile != null)
                {
                    var icon = IconHelper.LoadImageFromPath(bestFile.Path);
                    if (icon != null) return (icon, bestFile.Path);
                }
            }
            catch { }
            return (null, null);
        }

        /// <summary>
        /// Returns priority score based on icon scale/targetsize suffix.
        /// Higher priority = larger icon. targetsize-256 is best (actual 256px).
        /// </summary>
        private static int GetScalePriority(string path)
        {
            if (path.Contains("targetsize-256")) return 500;
            if (path.Contains("targetsize-128")) return 300;
            if (path.Contains("scale-400")) return 280;
            if (path.Contains("scale-200")) return 200;
            if (path.Contains("scale-150")) return 150;
            if (path.Contains("scale-125")) return 125;
            if (path.Contains("scale-100")) return 100;
            if (path.Contains("targetsize-96")) return 96;
            if (path.Contains("targetsize-64")) return 64;
            if (path.Contains("targetsize-48")) return 48;
            return 0;
        }

        /// <summary>
        /// Gets logo from UWP DisplayInfo API (fallback when asset search fails).
        /// </summary>
        private static async Task<ImageSource> GetLogoFromDisplayInfo(Windows.ApplicationModel.Core.AppListEntry appEntry)
        {
            try
            {
                IRandomAccessStreamReference logoRef = null;
                try { logoRef = appEntry.DisplayInfo.GetLogo(new Windows.Foundation.Size(256, 256)); }
                catch
                {
                    try { logoRef = appEntry.DisplayInfo.GetLogo(new Windows.Foundation.Size(150, 150)); }
                    catch { logoRef = appEntry.DisplayInfo.GetLogo(new Windows.Foundation.Size(44, 44)); }
                }

                if (logoRef != null)
                {
                    var stream = await logoRef.OpenReadAsync();
                    return await ConvertStreamToImageSource(stream);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Converts UWP stream to WPF ImageSource.
        /// </summary>
        private static async Task<ImageSource> ConvertStreamToImageSource(IRandomAccessStream stream)
        {
            if (stream == null) return null;
            try
            {
                using var memStream = new MemoryStream();
                var buffer = new byte[stream.Size];
                await stream.ReadAsync(buffer.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
                await memStream.WriteAsync(buffer, 0, buffer.Length);
                memStream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memStream;
                bitmap.DecodePixelWidth = 256;
                bitmap.DecodePixelHeight = 256;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Main entry point for retrieving all installed applications.
    /// Combines Start Menu and UWP apps into a single deduplicated list.
    /// </summary>
    public class InstalledAppsHelper
    {
        /// <summary>
        /// Gets all installed apps (Start Menu + UWP), sorted by name.
        /// </summary>
        public static async Task<List<AppInfo>> GetAllInstalledAppsAsync()
        {
            var allApps = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);

            // Add Start Menu apps
            foreach (var app in StartMenuAppsProvider.GetApps())
            {
                if (!allApps.ContainsKey(app.Name))
                    allApps[app.Name] = app;
            }

            // Add UWP apps (prefer UWP icon if Start Menu has none)
            foreach (var app in await UwpAppsProvider.GetAppsAsync())
            {
                if (!allApps.ContainsKey(app.Name))
                    allApps[app.Name] = app;
                else if (allApps[app.Name].Icon == null && app.Icon != null)
                    allApps[app.Name] = app;
            }

            return allApps.Values.OrderBy(a => a.Name).ToList();
        }

        /// <summary>
        /// Public helper for cache to get UWP icon from install path.
        /// </summary>
        public static (ImageSource Icon, string IconPath) GetUWPIconFromInstallLocation(string installLocation)
            => UwpAppsProvider.GetIconFromInstallLocation(installLocation);
    }
}
