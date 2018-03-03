using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Metadata;
using StardewValley;

namespace StardewModdingAPI.Framework
{
    /// <summary>A thread-safe content manager which intercepts assets being loaded to let SMAPI mods inject or edit them.</summary>
    /// <remarks>
    /// This is the centralised content manager which manages all game assets. The game and mods don't use this class
    /// directly; instead they use one of several <see cref="ContentManagerShim"/> instances, which proxy requests to
    /// this class. That ensures that when the game disposes one content manager, the others can continue unaffected.
    /// That notably requires this class to be thread-safe, since the content managers can be disposed asynchronously.
    /// 
    /// Note that assets in the cache have two identifiers: the asset name (like "bundles") and key (like "bundles.pt-BR").
    /// For English and non-translatable assets, these have the same value. The underlying cache only knows about asset
    /// keys, and the game and mods only know about asset names. The content manager handles resolving them.
    /// </remarks>
    internal class SContentManager : LocalizedContentManager
    {
        /*********
        ** Properties
        *********/
        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The underlying asset cache.</summary>
        private readonly ContentCache Cache;

        /// <summary>The locale codes used in asset keys indexed by enum value.</summary>
        private readonly IDictionary<LanguageCode, string> Locales;

        /// <summary>The language enum values indexed by locale code.</summary>
        private readonly IDictionary<string, LanguageCode> LanguageCodes;

        /// <summary>Provides metadata for core game assets.</summary>
        private readonly CoreAssets CoreAssets;

        /// <summary>The assets currently being intercepted by <see cref="IAssetLoader"/> instances. This is used to prevent infinite loops when a loader loads a new asset.</summary>
        private readonly ContextHash<string> AssetsBeingLoaded = new ContextHash<string>();

        /// <summary>A lookup of the content managers which loaded each asset.</summary>
        private readonly IDictionary<string, HashSet<ContentManager>> ContentManagersByAssetKey = new Dictionary<string, HashSet<ContentManager>>();

        /// <summary>The path prefix for assets in mod folders.</summary>
        private readonly string ModContentPrefix;

        /// <summary>A lock used to prevents concurrent changes to the cache while data is being read.</summary>
        private readonly ReaderWriterLockSlim Lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);


        /*********
        ** Accessors
        *********/
        /// <summary>Interceptors which provide the initial versions of matching assets.</summary>
        internal IDictionary<IModMetadata, IList<IAssetLoader>> Loaders { get; } = new Dictionary<IModMetadata, IList<IAssetLoader>>();

        /// <summary>Interceptors which edit matching assets after they're loaded.</summary>
        internal IDictionary<IModMetadata, IList<IAssetEditor>> Editors { get; } = new Dictionary<IModMetadata, IList<IAssetEditor>>();

        /// <summary>The absolute path to the <see cref="ContentManager.RootDirectory"/>.</summary>
        internal string FullRootDirectory => Path.Combine(Constants.ExecutionPath, this.RootDirectory);


        /*********
        ** Public methods
        *********/
        /****
        ** Constructor
        ****/
        /// <summary>Construct an instance.</summary>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        /// <param name="currentCulture">The current culture for which to localise content.</param>
        /// <param name="languageCodeOverride">The current language code for which to localise content.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        public SContentManager(IServiceProvider serviceProvider, string rootDirectory, CultureInfo currentCulture, string languageCodeOverride, IMonitor monitor, Reflector reflection)
            : base(serviceProvider, rootDirectory, currentCulture, languageCodeOverride)
        {
            // init
            this.Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            this.Cache = new ContentCache(this, reflection);
            this.ModContentPrefix = this.GetAssetNameFromFilePath(Constants.ModPath);

            // get asset data
            this.CoreAssets = new CoreAssets(this.NormaliseAssetName, reflection);
            this.Locales = this.GetKeyLocales(reflection);
            this.LanguageCodes = this.Locales.ToDictionary(p => p.Value, p => p.Key, StringComparer.InvariantCultureIgnoreCase);
        }

        /****
        ** Asset key/name handling
        ****/
        /// <summary>Normalise path separators in a file path. For asset keys, see <see cref="NormaliseAssetName"/> instead.</summary>
        /// <param name="path">The file path to normalise.</param>
        [Pure]
        public string NormalisePathSeparators(string path)
        {
            return this.Cache.NormalisePathSeparators(path);
        }

        /// <summary>Normalise an asset name so it's consistent with the underlying cache.</summary>
        /// <param name="assetName">The asset key.</param>
        [Pure]
        public string NormaliseAssetName(string assetName)
        {
            return this.Cache.NormaliseKey(assetName);
        }

        /// <summary>Assert that the given key has a valid format.</summary>
        /// <param name="key">The asset key to check.</param>
        /// <exception cref="ArgumentException">The asset key is empty or contains invalid characters.</exception>
        [SuppressMessage("ReSharper", "ParameterOnlyUsedForPreconditionCheck.Local", Justification = "Parameter is only used for assertion checks by design.")]
        public void AssertValidAssetKeyFormat(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("The asset key or local path is empty.");
            if (key.Intersect(Path.GetInvalidPathChars()).Any())
                throw new ArgumentException("The asset key or local path contains invalid characters.");
        }

        /// <summary>Convert an absolute file path into a appropriate asset name.</summary>
        /// <param name="absolutePath">The absolute path to the file.</param>
        public string GetAssetNameFromFilePath(string absolutePath)
        {
#if SMAPI_FOR_WINDOWS
            // XNA doesn't allow absolute asset paths, so get a path relative to the content folder
            return this.GetRelativePath(absolutePath);
#else
            // MonoGame is weird about relative paths on Mac, but allows absolute paths
            return absolutePath;
#endif
        }

        /****
        ** Content loading
        ****/
        /// <summary>Get the current content locale.</summary>
        public string GetLocale()
        {
            return this.Locales[this.GetCurrentLanguage()];
        }

        /// <summary>Get whether the content manager has already loaded and cached the given asset.</summary>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        public bool IsLoaded(string assetName)
        {
            assetName = this.Cache.NormaliseKey(assetName);
            return this.WithReadLock(() => this.IsNormalisedKeyLoaded(assetName));
        }

        /// <summary>Get the cached asset keys.</summary>
        public IEnumerable<string> GetAssetKeys()
        {
            return this.WithReadLock(() =>
                this.Cache.Keys
                    .Select(this.GetAssetName)
                    .Distinct()
            );
        }

        /// <summary>Load an asset through the content pipeline. When loading a <c>.png</c> file, this must be called outside the game's draw loop.</summary>
        /// <typeparam name="T">The expected asset type.</typeparam>
        /// <param name="assetName">The asset path relative to the content directory.</param>
        public override T Load<T>(string assetName)
        {
            return this.LoadFor<T>(assetName, this);
        }

        /// <summary>Load an asset through the content pipeline. When loading a <c>.png</c> file, this must be called outside the game's draw loop.</summary>
        /// <typeparam name="T">The expected asset type.</typeparam>
        /// <param name="assetName">The asset path relative to the content directory.</param>
        /// <param name="instance">The content manager instance for which to load the asset.</param>
        /// <exception cref="ArgumentException">The <paramref name="assetName"/> is empty or contains invalid characters.</exception>
        /// <exception cref="ContentLoadException">The content asset couldn't be loaded (e.g. because it doesn't exist).</exception>
        public T LoadFor<T>(string assetName, ContentManager instance)
        {
            // normalise asset key
            this.AssertValidAssetKeyFormat(assetName);
            assetName = this.NormaliseAssetName(assetName);

            // load game content
            if (!assetName.StartsWith(this.ModContentPrefix))
                return this.LoadImpl<T>(assetName, instance);

            // load mod content
            SContentLoadException GetContentError(string reasonPhrase) => new SContentLoadException($"Failed loading content asset '{assetName}': {reasonPhrase}");
            try
            {
                return this.WithWriteLock(() =>
                {
                    // try cache
                    if (this.IsLoaded(assetName))
                        return this.LoadImpl<T>(assetName, instance);

                    // get file
                    FileInfo file = this.GetModFile(assetName);
                    if (!file.Exists)
                        throw GetContentError("the specified path doesn't exist.");

                    // load content
                    switch (file.Extension.ToLower())
                    {
                        // XNB file
                        case ".xnb":
                            return this.LoadImpl<T>(assetName, instance);

                        // unpacked map
                        case ".tbin":
                            throw GetContentError($"can't read unpacked map file '{assetName}' directly from the underlying content manager. It must be loaded through the mod's {typeof(IModHelper)}.{nameof(IModHelper.Content)} helper.");

                        // unpacked image
                        case ".png":
                            // validate
                            if (typeof(T) != typeof(Texture2D))
                                throw GetContentError($"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Texture2D)}'.");

                            // fetch & cache
                            using (FileStream stream = File.OpenRead(file.FullName))
                            {
                                Texture2D texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
                                texture = this.PremultiplyTransparency(texture);
                                this.InjectWithoutLock(assetName, texture, instance);
                                return (T)(object)texture;
                            }

                        default:
                            throw GetContentError($"unknown file extension '{file.Extension}'; must be one of '.png', '.tbin', or '.xnb'.");
                    }
                });
            }
            catch (Exception ex) when (!(ex is SContentLoadException))
            {
                if (ex.GetInnermostException() is DllNotFoundException dllEx && dllEx.Message == "libgdiplus.dylib")
                    throw GetContentError("couldn't find libgdiplus, which is needed to load mod images. Make sure Mono is installed and you're running the game through the normal launcher.");
                throw new SContentLoadException($"The content manager failed loading content asset '{assetName}'.", ex);
            }
        }

        /// <summary>Inject an asset into the cache.</summary>
        /// <typeparam name="T">The type of asset to inject.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="value">The asset value.</param>
        /// <param name="instance">The content manager instance for which to load the asset.</param>
        public void Inject<T>(string assetName, T value, ContentManager instance)
        {
            this.WithWriteLock(() => this.InjectWithoutLock(assetName, value, instance));
        }

        /****
        ** Cache invalidation
        ****/
        /// <summary>Purge assets from the cache that match one of the interceptors.</summary>
        /// <param name="editors">The asset editors for which to purge matching assets.</param>
        /// <param name="loaders">The asset loaders for which to purge matching assets.</param>
        /// <returns>Returns whether any cache entries were invalidated.</returns>
        public bool InvalidateCacheFor(IAssetEditor[] editors, IAssetLoader[] loaders)
        {
            if (!editors.Any() && !loaders.Any())
                return false;

            // get CanEdit/Load methods
            MethodInfo canEdit = typeof(IAssetEditor).GetMethod(nameof(IAssetEditor.CanEdit));
            MethodInfo canLoad = typeof(IAssetLoader).GetMethod(nameof(IAssetLoader.CanLoad));
            if (canEdit == null || canLoad == null)
                throw new InvalidOperationException("SMAPI could not access the interceptor methods."); // should never happen

            // invalidate matching keys
            return this.InvalidateCache(asset =>
            {
                // check loaders
                MethodInfo canLoadGeneric = canLoad.MakeGenericMethod(asset.DataType);
                if (loaders.Any(loader => (bool)canLoadGeneric.Invoke(loader, new object[] { asset })))
                    return true;

                // check editors
                MethodInfo canEditGeneric = canEdit.MakeGenericMethod(asset.DataType);
                return editors.Any(editor => (bool)canEditGeneric.Invoke(editor, new object[] { asset }));
            });
        }

        /// <summary>Purge matched assets from the cache.</summary>
        /// <param name="predicate">Matches the asset keys to invalidate.</param>
        /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
        /// <returns>Returns whether any cache entries were invalidated.</returns>
        public bool InvalidateCache(Func<IAssetInfo, bool> predicate, bool dispose = false)
        {
            string locale = this.GetLocale();
            return this.InvalidateCache((assetName, type) =>
            {
                IAssetInfo info = new AssetInfo(locale, assetName, type, this.NormaliseAssetName);
                return predicate(info);
            });
        }

        /// <summary>Purge matched assets from the cache.</summary>
        /// <param name="predicate">Matches the asset keys to invalidate.</param>
        /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
        /// <returns>Returns whether any cache entries were invalidated.</returns>
        public bool InvalidateCache(Func<string, Type, bool> predicate, bool dispose = false)
        {
            return this.WithWriteLock(() =>
            {
                // invalidate matching keys
                HashSet<string> removeKeys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                HashSet<string> removeAssetNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                this.Cache.Remove((key, type) =>
                {
                    this.ParseCacheKey(key, out string assetName, out _);
                    if (removeAssetNames.Contains(assetName) || predicate(assetName, type))
                    {
                        removeAssetNames.Add(assetName);
                        removeKeys.Add(key);
                        return true;
                    }
                    return false;
                });

                // update reference tracking
                foreach (string key in removeKeys)
                    this.ContentManagersByAssetKey.Remove(key);

                // reload core game assets
                int reloaded = 0;
                foreach (string key in removeAssetNames)
                {
                    if (this.CoreAssets.ReloadForKey(this, key))
                        reloaded++;
                }

                // report result
                if (removeKeys.Any())
                {
                    this.Monitor.Log($"Invalidated {removeAssetNames.Count} asset names: {string.Join(", ", removeKeys.OrderBy(p => p, StringComparer.InvariantCultureIgnoreCase))}. Reloaded {reloaded} core assets.", LogLevel.Trace);
                    return true;
                }
                this.Monitor.Log("Invalidated 0 cache entries.", LogLevel.Trace);
                return false;
            });
        }

        /****
        ** Disposal
        ****/
        /// <summary>Dispose assets for the given content manager shim.</summary>
        /// <param name="shim">The content manager whose assets to dispose.</param>
        internal void DisposeFor(ContentManagerShim shim)
        {
            this.Monitor.Log($"Content manager '{shim.Name}' disposed, disposing assets that aren't needed by any other asset loader.", LogLevel.Trace);

            this.WithWriteLock(() =>
            {
                foreach (var entry in this.ContentManagersByAssetKey)
                    entry.Value.Remove(shim);
                this.InvalidateCache((key, type) => !this.ContentManagersByAssetKey[key].Any(), dispose: true);
            });
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Disposal
        ****/
        /// <summary>Dispose held resources.</summary>
        /// <param name="disposing">Whether the content manager is disposing (rather than finalising).</param>
        protected override void Dispose(bool disposing)
        {
            this.Monitor.Log("Disposing SMAPI's main content manager. It will no longer be usable after this point.", LogLevel.Trace);
            base.Dispose(disposing);
        }

        /****
        ** Asset name/key handling
        ****/
        /// <summary>Get a directory or file path relative to the content root.</summary>
        /// <param name="targetPath">The target file path.</param>
        private string GetRelativePath(string targetPath)
        {
            return PathUtilities.GetRelativePath(this.FullRootDirectory, targetPath);
        }

        /// <summary>Get the locale codes (like <c>ja-JP</c>) used in asset keys.</summary>
        /// <param name="reflection">Simplifies access to private game code.</param>
        private IDictionary<LanguageCode, string> GetKeyLocales(Reflector reflection)
        {
            string previousOverride = this.LanguageCodeOverride;
            try
            {
                // temporarily disable language override
                this.LanguageCodeOverride = null;

                // create locale => code map
                IReflectedMethod languageCodeString = reflection.GetMethod(this, "languageCodeString");
                IDictionary<LanguageCode, string> map = new Dictionary<LanguageCode, string>();
                foreach (LanguageCode code in Enum.GetValues(typeof(LanguageCode)))
                    map[code] = languageCodeString.Invoke<string>(code);
                return map;
            }
            finally
            {
                // restore previous settings
                this.LanguageCodeOverride = previousOverride;
            }
        }

        /// <summary>Get the asset name from a cache key.</summary>
        /// <param name="cacheKey">The input cache key.</param>
        private string GetAssetName(string cacheKey)
        {
            this.ParseCacheKey(cacheKey, out string assetName, out string _);
            return assetName;
        }

        /// <summary>Parse a cache key into its component parts.</summary>
        /// <param name="cacheKey">The input cache key.</param>
        /// <param name="assetName">The original asset name.</param>
        /// <param name="localeCode">The asset locale code (or <c>null</c> if not localised).</param>
        private void ParseCacheKey(string cacheKey, out string assetName, out string localeCode)
        {
            // handle localised key
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                int lastSepIndex = cacheKey.LastIndexOf(".", StringComparison.InvariantCulture);
                if (lastSepIndex >= 0)
                {
                    string suffix = cacheKey.Substring(lastSepIndex + 1, cacheKey.Length - lastSepIndex - 1);
                    if (this.LanguageCodes.ContainsKey(suffix))
                    {
                        assetName = cacheKey.Substring(0, lastSepIndex);
                        localeCode = cacheKey.Substring(lastSepIndex + 1, cacheKey.Length - lastSepIndex - 1);
                        return;
                    }
                }
            }

            // handle simple key
            assetName = cacheKey;
            localeCode = null;
        }

        /****
        ** Cache handling
        ****/
        /// <summary>Get whether an asset has already been loaded.</summary>
        /// <param name="normalisedAssetName">The normalised asset name.</param>
        private bool IsNormalisedKeyLoaded(string normalisedAssetName)
        {
            return this.Cache.ContainsKey(normalisedAssetName)
                || this.Cache.ContainsKey($"{normalisedAssetName}.{this.Locales[this.GetCurrentLanguage()]}"); // translated asset
        }

        /// <summary>Track that a content manager loaded an asset.</summary>
        /// <param name="key">The asset key that was loaded.</param>
        /// <param name="manager">The content manager that loaded the asset.</param>
        private void TrackAssetLoader(string key, ContentManager manager)
        {
            if (!this.ContentManagersByAssetKey.TryGetValue(key, out HashSet<ContentManager> hash))
                hash = this.ContentManagersByAssetKey[key] = new HashSet<ContentManager>();
            hash.Add(manager);
        }

        /****
        ** Content loading
        ****/
        /// <summary>Load an asset name without heuristics to support mod content.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="instance">The content manager instance for which to load the asset.</param>
        private T LoadImpl<T>(string assetName, ContentManager instance)
        {
            return this.WithWriteLock(() =>
            {
                // skip if already loaded
                if (this.IsNormalisedKeyLoaded(assetName))
                {
                    this.TrackAssetLoader(assetName, instance);
                    return base.Load<T>(assetName);
                }

                // load asset
                T data;
                if (this.AssetsBeingLoaded.Contains(assetName))
                {
                    this.Monitor.Log($"Broke loop while loading asset '{assetName}'.", LogLevel.Warn);
                    this.Monitor.Log($"Bypassing mod loaders for this asset. Stack trace:\n{Environment.StackTrace}", LogLevel.Trace);
                    data = base.Load<T>(assetName);
                }
                else
                {
                    data = this.AssetsBeingLoaded.Track(assetName, () =>
                    {
                        IAssetInfo info = new AssetInfo(this.GetLocale(), assetName, typeof(T), this.NormaliseAssetName);
                        IAssetData asset = this.ApplyLoader<T>(info) ?? new AssetDataForObject(info, base.Load<T>(assetName), this.NormaliseAssetName);
                        asset = this.ApplyEditors<T>(info, asset);
                        return (T)asset.Data;
                    });
                }

                // update cache & return data
                this.InjectWithoutLock(assetName, data, instance);
                return data;
            });
        }

        /// <summary>Inject an asset into the cache without acquiring a write lock. This should only be called from within a write lock.</summary>
        /// <typeparam name="T">The type of asset to inject.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="value">The asset value.</param>
        /// <param name="instance">The content manager instance for which to load the asset.</param>
        private void InjectWithoutLock<T>(string assetName, T value, ContentManager instance)
        {
            assetName = this.NormaliseAssetName(assetName);
            this.Cache[assetName] = value;
            this.TrackAssetLoader(assetName, instance);
        }

        /// <summary>Get a file from the mod folder.</summary>
        /// <param name="path">The asset path relative to the content folder.</param>
        private FileInfo GetModFile(string path)
        {
            // try exact match
            FileInfo file = new FileInfo(Path.Combine(this.FullRootDirectory, path));

            // try with default extension
            if (!file.Exists && file.Extension.ToLower() != ".xnb")
            {
                FileInfo result = new FileInfo(file.FullName + ".xnb");
                if (result.Exists)
                    file = result;
            }

            return file;
        }

        /// <summary>Load the initial asset from the registered <see cref="Loaders"/>.</summary>
        /// <param name="info">The basic asset metadata.</param>
        /// <returns>Returns the loaded asset metadata, or <c>null</c> if no loader matched.</returns>
        private IAssetData ApplyLoader<T>(IAssetInfo info)
        {
            // find matching loaders
            var loaders = this.GetInterceptors(this.Loaders)
                .Where(entry =>
                {
                    try
                    {
                        return entry.Value.CanLoad<T>(info);
                    }
                    catch (Exception ex)
                    {
                        entry.Key.LogAsMod($"Mod failed when checking whether it could load asset '{info.AssetName}', and will be ignored. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                        return false;
                    }
                })
                .ToArray();

            // validate loaders
            if (!loaders.Any())
                return null;
            if (loaders.Length > 1)
            {
                string[] loaderNames = loaders.Select(p => p.Key.DisplayName).ToArray();
                this.Monitor.Log($"Multiple mods want to provide the '{info.AssetName}' asset ({string.Join(", ", loaderNames)}), but an asset can't be loaded multiple times. SMAPI will use the default asset instead; uninstall one of the mods to fix this. (Message for modders: you should usually use {typeof(IAssetEditor)} instead to avoid conflicts.)", LogLevel.Warn);
                return null;
            }

            // fetch asset from loader
            IModMetadata mod = loaders[0].Key;
            IAssetLoader loader = loaders[0].Value;
            T data;
            try
            {
                data = loader.Load<T>(info);
                this.Monitor.Log($"{mod.DisplayName} loaded asset '{info.AssetName}'.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                mod.LogAsMod($"Mod crashed when loading asset '{info.AssetName}'. SMAPI will use the default asset instead. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                return null;
            }

            // validate asset
            if (data == null)
            {
                mod.LogAsMod($"Mod incorrectly set asset '{info.AssetName}' to a null value; ignoring override.", LogLevel.Error);
                return null;
            }

            // return matched asset
            return new AssetDataForObject(info, data, this.NormaliseAssetName);
        }

        /// <summary>Apply any <see cref="Editors"/> to a loaded asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="info">The basic asset metadata.</param>
        /// <param name="asset">The loaded asset.</param>
        private IAssetData ApplyEditors<T>(IAssetInfo info, IAssetData asset)
        {
            IAssetData GetNewData(object data) => new AssetDataForObject(info, data, this.NormaliseAssetName);

            // edit asset
            foreach (var entry in this.GetInterceptors(this.Editors))
            {
                // check for match
                IModMetadata mod = entry.Key;
                IAssetEditor editor = entry.Value;
                try
                {
                    if (!editor.CanEdit<T>(info))
                        continue;
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod crashed when checking whether it could edit asset '{info.AssetName}', and will be ignored. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // try edit
                object prevAsset = asset.Data;
                try
                {
                    editor.Edit<T>(asset);
                    this.Monitor.Log($"{mod.DisplayName} intercepted {info.AssetName}.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod crashed when editing asset '{info.AssetName}', which may cause errors in-game. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                // validate edit
                if (asset.Data == null)
                {
                    mod.LogAsMod($"Mod incorrectly set asset '{info.AssetName}' to a null value; ignoring override.", LogLevel.Warn);
                    asset = GetNewData(prevAsset);
                }
                else if (!(asset.Data is T))
                {
                    mod.LogAsMod($"Mod incorrectly set asset '{asset.AssetName}' to incompatible type '{asset.Data.GetType()}', expected '{typeof(T)}'; ignoring override.", LogLevel.Warn);
                    asset = GetNewData(prevAsset);
                }
            }

            // return result
            return asset;
        }

        /// <summary>Get all registered interceptors from a list.</summary>
        private IEnumerable<KeyValuePair<IModMetadata, T>> GetInterceptors<T>(IDictionary<IModMetadata, IList<T>> entries)
        {
            foreach (var entry in entries)
            {
                IModMetadata mod = entry.Key;
                IList<T> interceptors = entry.Value;

                // registered editors
                foreach (T interceptor in interceptors)
                    yield return new KeyValuePair<IModMetadata, T>(mod, interceptor);
            }
        }

        /// <summary>Premultiply a texture's alpha values to avoid transparency issues in the game. This is only possible if the game isn't currently drawing.</summary>
        /// <param name="texture">The texture to premultiply.</param>
        /// <returns>Returns a premultiplied texture.</returns>
        /// <remarks>Based on <a href="https://gist.github.com/Layoric/6255384">code by Layoric</a>.</remarks>
        private Texture2D PremultiplyTransparency(Texture2D texture)
        {
            // validate
            if (Context.IsInDrawLoop)
                throw new NotSupportedException("Can't load a PNG file while the game is drawing to the screen. Make sure you load content outside the draw loop.");

            // process texture
            SpriteBatch spriteBatch = Game1.spriteBatch;
            GraphicsDevice gpu = Game1.graphics.GraphicsDevice;
            using (RenderTarget2D renderTarget = new RenderTarget2D(Game1.graphics.GraphicsDevice, texture.Width, texture.Height))
            {
                // create blank render target to premultiply
                gpu.SetRenderTarget(renderTarget);
                gpu.Clear(Color.Black);

                // multiply each color by the source alpha, and write just the color values into the final texture
                spriteBatch.Begin(SpriteSortMode.Immediate, new BlendState
                {
                    ColorDestinationBlend = Blend.Zero,
                    ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
                    AlphaDestinationBlend = Blend.Zero,
                    AlphaSourceBlend = Blend.SourceAlpha,
                    ColorSourceBlend = Blend.SourceAlpha
                });
                spriteBatch.Draw(texture, texture.Bounds, Color.White);
                spriteBatch.End();

                // copy the alpha values from the source texture into the final one without multiplying them
                spriteBatch.Begin(SpriteSortMode.Immediate, new BlendState
                {
                    ColorWriteChannels = ColorWriteChannels.Alpha,
                    AlphaDestinationBlend = Blend.Zero,
                    ColorDestinationBlend = Blend.Zero,
                    AlphaSourceBlend = Blend.One,
                    ColorSourceBlend = Blend.One
                });
                spriteBatch.Draw(texture, texture.Bounds, Color.White);
                spriteBatch.End();

                // release GPU
                gpu.SetRenderTarget(null);

                // extract premultiplied data
                Color[] data = new Color[texture.Width * texture.Height];
                renderTarget.GetData(data);

                // unset texture from GPU to regain control
                gpu.Textures[0] = null;

                // update texture with premultiplied data
                texture.SetData(data);
            }

            return texture;
        }

        /****
        ** Concurrency logic
        ****/
        /// <summary>Acquire a read lock which prevents concurrent writes to the cache while it's open.</summary>
        /// <typeparam name="T">The action's return value.</typeparam>
        /// <param name="action">The action to perform.</param>
        private T WithReadLock<T>(Func<T> action)
        {
            try
            {
                this.Lock.EnterReadLock();
                return action();
            }
            finally
            {
                this.Lock.ExitReadLock();
            }
        }

        /// <summary>Acquire a write lock which prevents concurrent reads or writes to the cache while it's open.</summary>
        /// <param name="action">The action to perform.</param>
        private void WithWriteLock(Action action)
        {
            try
            {
                this.Lock.EnterWriteLock();
                action();
            }
            finally
            {
                this.Lock.ExitWriteLock();
            }
        }

        /// <summary>Acquire a write lock which prevents concurrent reads or writes to the cache while it's open.</summary>
        /// <typeparam name="T">The action's return value.</typeparam>
        /// <param name="action">The action to perform.</param>
        private T WithWriteLock<T>(Func<T> action)
        {
            try
            {
                this.Lock.EnterWriteLock();
                return action();
            }
            finally
            {
                this.Lock.ExitWriteLock();
            }
        }
    }
}
