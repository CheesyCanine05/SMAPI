using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Utilities;
using StardewValley;
using xTile;
using xTile.Format;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ModHelpers
{
    /// <summary>Provides an API for loading content assets.</summary>
    internal class ContentHelper : BaseHelper, IContentHelper
    {
        /*********
        ** Properties
        *********/
        /// <summary>SMAPI's core content logic.</summary>
        private readonly ContentCore ContentCore;

        /// <summary>The content manager for this mod.</summary>
        private readonly ContentManagerShim ContentManager;

        /// <summary>The absolute path to the mod folder.</summary>
        private readonly string ModFolderPath;

        /// <summary>The friendly mod name for use in errors.</summary>
        private readonly string ModName;

        /// <summary>Encapsulates monitoring and logging for a given module.</summary>
        private readonly IMonitor Monitor;


        /*********
        ** Accessors
        *********/
        /// <summary>The game's current locale code (like <c>pt-BR</c>).</summary>
        public string CurrentLocale => this.ContentCore.GetLocale();

        /// <summary>The game's current locale as an enum value.</summary>
        public LocalizedContentManager.LanguageCode CurrentLocaleConstant => this.ContentCore.Language;

        /// <summary>The observable implementation of <see cref="AssetEditors"/>.</summary>
        internal ObservableCollection<IAssetEditor> ObservableAssetEditors { get; } = new ObservableCollection<IAssetEditor>();

        /// <summary>The observable implementation of <see cref="AssetLoaders"/>.</summary>
        internal ObservableCollection<IAssetLoader> ObservableAssetLoaders { get; } = new ObservableCollection<IAssetLoader>();

        /// <summary>Interceptors which provide the initial versions of matching content assets.</summary>
        public IList<IAssetLoader> AssetLoaders => this.ObservableAssetLoaders;

        /// <summary>Interceptors which edit matching content assets after they're loaded.</summary>
        public IList<IAssetEditor> AssetEditors => this.ObservableAssetEditors;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="contentCore">SMAPI's core content logic.</param>
        /// <param name="contentManager">The content manager for this mod.</param>
        /// <param name="modFolderPath">The absolute path to the mod folder.</param>
        /// <param name="modID">The unique ID of the relevant mod.</param>
        /// <param name="modName">The friendly mod name for use in errors.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public ContentHelper(ContentCore contentCore, ContentManagerShim contentManager, string modFolderPath, string modID, string modName, IMonitor monitor)
            : base(modID)
        {
            this.ContentCore = contentCore;
            this.ContentManager = contentManager;
            this.ModFolderPath = modFolderPath;
            this.ModName = modName;
            this.Monitor = monitor;
        }

        /// <summary>Load content from the game folder or mod folder (if not already cached), and return it. When loading a <c>.png</c> file, this must be called outside the game's draw loop.</summary>
        /// <typeparam name="T">The expected data type. The main supported types are <see cref="Map"/>, <see cref="Texture2D"/>, and dictionaries; other types may be supported by the game's content pipeline.</typeparam>
        /// <param name="key">The asset key to fetch (if the <paramref name="source"/> is <see cref="ContentSource.GameContent"/>), or the local path to a content file relative to the mod folder.</param>
        /// <param name="source">Where to search for a matching content asset.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        /// <exception cref="ContentLoadException">The content asset couldn't be loaded (e.g. because it doesn't exist).</exception>
        public T Load<T>(string key, ContentSource source = ContentSource.ModFolder)
        {
            SContentLoadException GetContentError(string reasonPhrase) => new SContentLoadException($"{this.ModName} failed loading content asset '{key}' from {source}: {reasonPhrase}.");

            try
            {
                this.AssertValidAssetKeyFormat(key);
                switch (source)
                {
                    case ContentSource.GameContent:
                        return this.ContentManager.Load<T>(key);

                    case ContentSource.ModFolder:
                        // get file
                        FileInfo file = this.GetModFile(key);
                        if (!file.Exists)
                            throw GetContentError($"there's no matching file at path '{file.FullName}'.");

                        // get asset path
                        string assetName = this.ContentCore.GetAssetNameFromFilePath(file.FullName);

                        // try cache
                        if (this.ContentCore.IsLoaded(assetName))
                            return this.ContentManager.Load<T>(assetName);

                        // fix map tilesheets
                        if (file.Extension.ToLower() == ".tbin")
                        {
                            // validate
                            if (typeof(T) != typeof(Map))
                                throw GetContentError($"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Map)}'.");

                            // fetch & cache
                            FormatManager formatManager = FormatManager.Instance;
                            Map map = formatManager.LoadMap(file.FullName);
                            this.FixCustomTilesheetPaths(map, key);

                            // inject map
                            this.ContentManager.Inject(assetName, map);
                            return (T)(object)map;
                        }

                        // load through content manager
                        return this.ContentManager.Load<T>(assetName);

                    default:
                        throw GetContentError($"unknown content source '{source}'.");
                }
            }
            catch (Exception ex) when (!(ex is SContentLoadException))
            {
                throw new SContentLoadException($"{this.ModName} failed loading content asset '{key}' from {source}.", ex);
            }
        }

        /// <summary>Normalise an asset name so it's consistent with those generated by the game. This is mainly useful for string comparisons like <see cref="string.StartsWith(string)"/> on generated asset names, and isn't necessary when passing asset names into other content helper methods.</summary>
        /// <param name="assetName">The asset key.</param>
        [Pure]
        public string NormaliseAssetName(string assetName)
        {
            return this.ContentCore.NormaliseAssetName(assetName);
        }

        /// <summary>Get the underlying key in the game's content cache for an asset. This can be used to load custom map tilesheets, but should be avoided when you can use the content API instead. This does not validate whether the asset exists.</summary>
        /// <param name="key">The asset key to fetch (if the <paramref name="source"/> is <see cref="ContentSource.GameContent"/>), or the local path to a content file relative to the mod folder.</param>
        /// <param name="source">Where to search for a matching content asset.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        public string GetActualAssetKey(string key, ContentSource source = ContentSource.ModFolder)
        {
            switch (source)
            {
                case ContentSource.GameContent:
                    return this.ContentCore.NormaliseAssetName(key);

                case ContentSource.ModFolder:
                    FileInfo file = this.GetModFile(key);
                    return this.ContentCore.NormaliseAssetName(this.ContentCore.GetAssetNameFromFilePath(file.FullName));

                default:
                    throw new NotSupportedException($"Unknown content source '{source}'.");
            }
        }

        /// <summary>Remove an asset from the content cache so it's reloaded on the next request. This will reload core game assets if needed, but references to the former asset will still show the previous content.</summary>
        /// <param name="key">The asset key to invalidate in the content folder.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        /// <returns>Returns whether the given asset key was cached.</returns>
        public bool InvalidateCache(string key)
        {
            string actualKey = this.GetActualAssetKey(key, ContentSource.GameContent);
            this.Monitor.Log($"Requested cache invalidation for '{actualKey}'.", LogLevel.Trace);
            return this.ContentCore.InvalidateCache(asset => asset.AssetNameEquals(actualKey));
        }

        /// <summary>Remove all assets of the given type from the cache so they're reloaded on the next request. <b>This can be a very expensive operation and should only be used in very specific cases.</b> This will reload core game assets if needed, but references to the former assets will still show the previous content.</summary>
        /// <typeparam name="T">The asset type to remove from the cache.</typeparam>
        /// <returns>Returns whether any assets were invalidated.</returns>
        public bool InvalidateCache<T>()
        {
            this.Monitor.Log($"Requested cache invalidation for all assets of type {typeof(T)}. This is an expensive operation and should be avoided if possible.", LogLevel.Trace);
            return this.ContentCore.InvalidateCache((key, type) => typeof(T).IsAssignableFrom(type));
        }

        /// <summary>Remove matching assets from the content cache so they're reloaded on the next request. This will reload core game assets if needed, but references to the former asset will still show the previous content.</summary>
        /// <param name="predicate">A predicate matching the assets to invalidate.</param>
        /// <returns>Returns whether any cache entries were invalidated.</returns>
        public bool InvalidateCache(Func<IAssetInfo, bool> predicate)
        {
            this.Monitor.Log("Requested cache invalidation for all assets matching a predicate.", LogLevel.Trace);
            return this.ContentCore.InvalidateCache(predicate);
        }

        /*********
        ** Private methods
        *********/
        /// <summary>Assert that the given key has a valid format.</summary>
        /// <param name="key">The asset key to check.</param>
        /// <exception cref="ArgumentException">The asset key is empty or contains invalid characters.</exception>
        [SuppressMessage("ReSharper", "ParameterOnlyUsedForPreconditionCheck.Local", Justification = "Parameter is only used for assertion checks by design.")]
        private void AssertValidAssetKeyFormat(string key)
        {
            this.ContentCore.AssertValidAssetKeyFormat(key);
            if (Path.IsPathRooted(key))
                throw new ArgumentException("The asset key must not be an absolute path.");
        }

        /// <summary>Fix custom map tilesheet paths so they can be found by the content manager.</summary>
        /// <param name="map">The map whose tilesheets to fix.</param>
        /// <param name="mapKey">The map asset key within the mod folder.</param>
        /// <exception cref="ContentLoadException">A map tilesheet couldn't be resolved.</exception>
        /// <remarks>
        /// The game's logic for tilesheets in <see cref="Game1.setGraphicsForSeason"/> is a bit specialised. It boils
        /// down to this:
        ///  * If the location is indoors or the desert, or the image source contains 'path' or 'object', it's loaded
        ///    as-is relative to the <c>Content</c> folder.
        ///  * Else it's loaded from <c>Content\Maps</c> with a seasonal prefix.
        /// 
        /// That logic doesn't work well in our case, mainly because we have no location metadata at this point.
        /// Instead we use a more heuristic approach: check relative to the map file first, then relative to
        /// <c>Content\Maps</c>, then <c>Content</c>. If the image source filename contains a seasonal prefix, try for a
        /// seasonal variation and then an exact match.
        /// 
        /// While that doesn't exactly match the game logic, it's close enough that it's unlikely to make a difference.
        /// </remarks>
        private void FixCustomTilesheetPaths(Map map, string mapKey)
        {
            // get map info
            if (!map.TileSheets.Any())
                return;
            mapKey = this.ContentCore.NormaliseAssetName(mapKey); // Mono's Path.GetDirectoryName doesn't handle Windows dir separators
            string relativeMapFolder = Path.GetDirectoryName(mapKey) ?? ""; // folder path containing the map, relative to the mod folder

            // fix tilesheets
            foreach (TileSheet tilesheet in map.TileSheets)
            {
                string imageSource = tilesheet.ImageSource;

                // validate tilesheet path
                if (Path.IsPathRooted(imageSource) || PathUtilities.GetSegments(imageSource).Contains(".."))
                    throw new ContentLoadException($"The '{imageSource}' tilesheet couldn't be loaded. Tilesheet paths must be a relative path without directory climbing (../).");

                // get seasonal name (if applicable)
                string seasonalImageSource = null;
                if (Game1.currentSeason != null)
                {
                    string filename = Path.GetFileName(imageSource);
                    bool hasSeasonalPrefix =
                        filename.StartsWith("spring_", StringComparison.CurrentCultureIgnoreCase)
                        || filename.StartsWith("summer_", StringComparison.CurrentCultureIgnoreCase)
                        || filename.StartsWith("fall_", StringComparison.CurrentCultureIgnoreCase)
                        || filename.StartsWith("winter_", StringComparison.CurrentCultureIgnoreCase);
                    if (hasSeasonalPrefix && !filename.StartsWith(Game1.currentSeason + "_"))
                    {
                        string dirPath = imageSource.Substring(0, imageSource.LastIndexOf(filename, StringComparison.CurrentCultureIgnoreCase));
                        seasonalImageSource = $"{dirPath}{Game1.currentSeason}_{filename.Substring(filename.IndexOf("_", StringComparison.CurrentCultureIgnoreCase) + 1)}";
                    }
                }

                // load best match
                try
                {
                    string key =
                        this.GetTilesheetAssetName(relativeMapFolder, seasonalImageSource)
                        ?? this.GetTilesheetAssetName(relativeMapFolder, imageSource);
                    if (key != null)
                    {
                        tilesheet.ImageSource = key;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    throw new ContentLoadException($"The '{imageSource}' tilesheet couldn't be loaded relative to either map file or the game's content folder.", ex);
                }

                // none found
                throw new ContentLoadException($"The '{imageSource}' tilesheet couldn't be loaded relative to either map file or the game's content folder.");
            }
        }

        /// <summary>Get the actual asset name for a tilesheet.</summary>
        /// <param name="modRelativeMapFolder">The folder path containing the map, relative to the mod folder.</param>
        /// <param name="imageSource">The tilesheet image source to load.</param>
        /// <returns>Returns the asset name.</returns>
        /// <remarks>See remarks on <see cref="FixCustomTilesheetPaths"/>.</remarks>
        private string GetTilesheetAssetName(string modRelativeMapFolder, string imageSource)
        {
            if (imageSource == null)
                return null;

            // check relative to map file
            {
                string localKey = Path.Combine(modRelativeMapFolder, imageSource);
                FileInfo localFile = this.GetModFile(localKey);
                if (localFile.Exists)
                    return this.GetActualAssetKey(localKey);
            }

            // check relative to content folder
            {
                foreach (string candidateKey in new[] { imageSource, $@"Maps\{imageSource}" })
                {
                    string contentKey = candidateKey.EndsWith(".png")
                        ? candidateKey.Substring(0, imageSource.Length - 4)
                        : candidateKey;

                    try
                    {
                        this.Load<Texture2D>(contentKey, ContentSource.GameContent);
                        return contentKey;
                    }
                    catch
                    {
                        // ignore file-not-found errors
                        // TODO: while it's useful to suppress an asset-not-found error here to avoid
                        // confusion, this is a pretty naive approach. Even if the file doesn't exist,
                        // the file may have been loaded through an IAssetLoader which failed. So even
                        // if the content file doesn't exist, that doesn't mean the error here is a
                        // content-not-found error. Unfortunately XNA doesn't provide a good way to
                        // detect the error type.
                        if (this.GetContentFolderFile(contentKey).Exists)
                            throw;
                    }
                }
            }

            // not found
            return null;
        }

        /// <summary>Get a file from the mod folder.</summary>
        /// <param name="path">The asset path relative to the mod folder.</param>
        private FileInfo GetModFile(string path)
        {
            // try exact match
            path = Path.Combine(this.ModFolderPath, this.ContentCore.NormalisePathSeparators(path));
            FileInfo file = new FileInfo(path);

            // try with default extension
            if (!file.Exists && file.Extension.ToLower() != ".xnb")
            {
                FileInfo result = new FileInfo(path + ".xnb");
                if (result.Exists)
                    file = result;
            }

            return file;
        }

        /// <summary>Get a file from the game's content folder.</summary>
        /// <param name="key">The asset key.</param>
        private FileInfo GetContentFolderFile(string key)
        {
            // get file path
            string path = Path.Combine(this.ContentCore.FullRootDirectory, key);
            if (!path.EndsWith(".xnb"))
                path += ".xnb";

            // get file
            return new FileInfo(path);
        }
    }
}
