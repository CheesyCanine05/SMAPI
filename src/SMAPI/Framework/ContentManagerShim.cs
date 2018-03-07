using System;
using System.Globalization;
using StardewValley;

namespace StardewModdingAPI.Framework
{
    /// <summary>A minimal content manager which defers to SMAPI's core content logic.</summary>
    internal class ContentManagerShim : LocalizedContentManager
    {
        /*********
        ** Properties
        *********/
        /// <summary>SMAPI's core content logic.</summary>
        private readonly ContentCore ContentCore;


        /*********
        ** Accessors
        *********/
        /// <summary>The content manager's name for logs (if any).</summary>
        public string Name { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="contentCore">SMAPI's core content logic.</param>
        /// <param name="name">The content manager's name for logs (if any).</param>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        /// <param name="currentCulture">The current culture for which to localise content.</param>
        /// <param name="languageCodeOverride">The current language code for which to localise content.</param>
        public ContentManagerShim(ContentCore contentCore, string name, IServiceProvider serviceProvider, string rootDirectory, CultureInfo currentCulture, string languageCodeOverride)
                : base(serviceProvider, rootDirectory, currentCulture, languageCodeOverride)
        {
            this.ContentCore = contentCore;
            this.Name = name;
        }

        /// <summary>Load an asset that has been processed by the content pipeline.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        public override T Load<T>(string assetName)
        {
            return this.Load<T>(assetName, LocalizedContentManager.CurrentLanguageCode);
        }

        /// <summary>Load an asset that has been processed by the content pipeline.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="language">The language code for which to load content.</param>
        public override T Load<T>(string assetName, LanguageCode language)
        {
            return this.ContentCore.Load<T>(assetName, this, language);
        }

        /// <summary>Load the base asset without localisation.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        public override T LoadBase<T>(string assetName)
        {
            return this.Load<T>(assetName, LanguageCode.en);
        }

        /// <summary>Inject an asset into the cache.</summary>
        /// <typeparam name="T">The type of asset to inject.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="value">The asset value.</param>
        public void Inject<T>(string assetName, T value)
        {
            this.ContentCore.Inject<T>(assetName, value, this);
        }

        /// <summary>Create a new content manager for temporary use.</summary>
        public override LocalizedContentManager CreateTemporary()
        {
            return this.ContentCore.CreateContentManager("(temporary)");
        }


        /*********
        ** Protected methods
        *********/
        /// <summary>Dispose held resources.</summary>
        /// <param name="disposing">Whether the content manager is disposing (rather than finalising).</param>
        protected override void Dispose(bool disposing)
        {
            this.ContentCore.DisposeFor(this);
        }
    }
}
