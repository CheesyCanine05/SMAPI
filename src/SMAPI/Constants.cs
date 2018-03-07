using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.ModLoading;
using StardewValley;

namespace StardewModdingAPI
{
    /// <summary>Contains SMAPI's constants and assumptions.</summary>
    public static class Constants
    {
        /*********
        ** Properties
        *********/
        /// <summary>The directory path containing the current save's data (if a save is loaded).</summary>
        private static string RawSavePath => Context.IsSaveLoaded ? Path.Combine(Constants.SavesPath, Constants.GetSaveFolderName()) : null;

        /// <summary>Whether the directory containing the current save's data exists on disk.</summary>
        private static bool SavePathReady => Context.IsSaveLoaded && Directory.Exists(Constants.RawSavePath);

        /// <summary>Maps vendor keys (like <c>Nexus</c>) to their mod URL template (where <c>{0}</c> is the mod ID). This doesn't affect update checks, which defer to the remote web API.</summary>
        private static readonly IDictionary<string, string> VendorModUrls = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
        {
            ["Chucklefish"] = "https://community.playstarbound.com/resources/{0}",
            ["GitHub"] = "https://github.com/{0}/releases",
            ["Nexus"] = "https://www.nexusmods.com/stardewvalley/mods/{0}"
        };


        /*********
        ** Accessors
        *********/
        /****
        ** Public
        ****/
        /// <summary>SMAPI's current semantic version.</summary>
        public static ISemanticVersion ApiVersion { get; } = new SemanticVersion($"2.6-alpha.{DateTime.UtcNow:yyyyMMddHHmm}");

        /// <summary>The minimum supported version of Stardew Valley.</summary>
        public static ISemanticVersion MinimumGameVersion { get; } = new GameVersion("1.3.0.4");

        /// <summary>The maximum supported version of Stardew Valley.</summary>
        public static ISemanticVersion MaximumGameVersion { get; } = null;

        /// <summary>The path to the game folder.</summary>
        public static string ExecutionPath { get; } = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        /// <summary>The directory path containing Stardew Valley's app data.</summary>
        public static string DataPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley");

        /// <summary>The directory path in which error logs should be stored.</summary>
        public static string LogDir { get; } = Path.Combine(Constants.DataPath, "ErrorLogs");

        /// <summary>The directory path where all saves are stored.</summary>
        public static string SavesPath { get; } = Path.Combine(Constants.DataPath, "Saves");

        /// <summary>The directory name containing the current save's data (if a save is loaded and the directory exists).</summary>
        public static string SaveFolderName => Context.IsSaveLoaded ? Constants.GetSaveFolderName() : "";

        /// <summary>The directory path containing the current save's data (if a save is loaded and the directory exists).</summary>
        public static string CurrentSavePath => Constants.SavePathReady ? Path.Combine(Constants.SavesPath, Constants.GetSaveFolderName()) : "";

        /****
        ** Internal
        ****/
        /// <summary>The GitHub repository to check for updates.</summary>
        internal const string GitHubRepository = "Pathoschild/SMAPI";

        /// <summary>The file path for the SMAPI configuration file.</summary>
        internal static string ApiConfigPath => Path.Combine(Constants.ExecutionPath, $"{typeof(Program).Assembly.GetName().Name}.config.json");

        /// <summary>The file path to the log where the latest output should be saved.</summary>
        internal static string DefaultLogPath => Path.Combine(Constants.LogDir, "SMAPI-latest.txt");

        /// <summary>A copy of the log leading up to the previous fatal crash, if any.</summary>
        internal static string FatalCrashLog => Path.Combine(Constants.LogDir, "SMAPI-crash.txt");

        /// <summary>The file path which stores a fatal crash message for the next run.</summary>
        internal static string FatalCrashMarker => Path.Combine(Constants.ExecutionPath, "StardewModdingAPI.crash.marker");

        /// <summary>The full path to the folder containing mods.</summary>
        internal static string ModPath { get; } = Path.Combine(Constants.ExecutionPath, "Mods");

        /// <summary>The game's current semantic version.</summary>
        internal static ISemanticVersion GameVersion { get; } = new GameVersion(Constants.GetGameVersion());

        /// <summary>The target game platform.</summary>
        internal static Platform TargetPlatform { get; } =
#if SMAPI_FOR_WINDOWS
        Platform.Windows;
#else
        Platform.Mono;
#endif


        /*********
        ** Internal methods
        *********/
        /// <summary>Get metadata for mapping assemblies to the current platform.</summary>
        /// <param name="targetPlatform">The target game platform.</param>
        internal static PlatformAssemblyMap GetAssemblyMap(Platform targetPlatform)
        {
            // get assembly changes needed for platform
            string[] removeAssemblyReferences;
            Assembly[] targetAssemblies;
            switch (targetPlatform)
            {
                case Platform.Mono:
                    removeAssemblyReferences = new[]
                    {
                        "Stardew Valley",
                        "Microsoft.Xna.Framework",
                        "Microsoft.Xna.Framework.Game",
                        "Microsoft.Xna.Framework.Graphics"
                    };
                    targetAssemblies = new[]
                    {
                        typeof(StardewValley.Game1).Assembly,
                        typeof(Microsoft.Xna.Framework.Vector2).Assembly
                    };
                    break;

                case Platform.Windows:
                    removeAssemblyReferences = new[]
                    {
                        "StardewValley",
                        "MonoGame.Framework"
                    };
                    targetAssemblies = new[]
                    {
                        typeof(StardewValley.Game1).Assembly,
                        typeof(Microsoft.Xna.Framework.Vector2).Assembly,
                        typeof(Microsoft.Xna.Framework.Game).Assembly,
                        typeof(Microsoft.Xna.Framework.Graphics.SpriteBatch).Assembly
                    };
                    break;

                default:
                    throw new InvalidOperationException($"Unknown target platform '{targetPlatform}'.");
            }

            return new PlatformAssemblyMap(targetPlatform, removeAssemblyReferences, targetAssemblies);
        }

        /// <summary>Get an update URL for an update key (if valid).</summary>
        /// <param name="updateKey">The update key.</param>
        internal static string GetUpdateUrl(string updateKey)
        {
            string[] parts = updateKey.Split(new[] { ':' }, 2);
            if (parts.Length != 2)
                return null;

            string vendorKey = parts[0].Trim();
            string modID = parts[1].Trim();

            if (Constants.VendorModUrls.TryGetValue(vendorKey, out string urlTemplate))
                return string.Format(urlTemplate, modID);

            return null;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get the name of a save directory for the current player.</summary>
        private static string GetSaveFolderName()
        {
            string prefix = new string(Game1.player.name.Value.Where(char.IsLetterOrDigit).ToArray());
            return $"{prefix}_{Game1.uniqueIDForThisGame}";
        }

        /// <summary>Get the game's current version string.</summary>
        private static string GetGameVersion()
        {
            // we need reflection because it's a constant, so SMAPI's references to it are inlined at compile-time
            FieldInfo field = typeof(Game1).GetField(nameof(Game1.version), BindingFlags.Public | BindingFlags.Static);
            if (field == null)
                throw new InvalidOperationException($"The {nameof(Game1)}.{nameof(Game1.version)} field could not be found.");
            return (string)field.GetValue(null);
        }
    }
}
