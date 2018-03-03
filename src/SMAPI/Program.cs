using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
#if SMAPI_FOR_WINDOWS
using System.Management;
using System.Windows.Forms;
#endif
using Newtonsoft.Json;
using StardewModdingAPI.Common.Models;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Framework.ModData;
using StardewModdingAPI.Framework.Models;
using StardewModdingAPI.Framework.ModHelpers;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Serialisation;
using StardewModdingAPI.Framework.Utilities;
using StardewValley;
using Monitor = StardewModdingAPI.Framework.Monitor;
using SObject = StardewValley.Object;

namespace StardewModdingAPI
{
    /// <summary>The main entry point for SMAPI, responsible for hooking into and launching the game.</summary>
    internal class Program : IDisposable
    {
        /*********
        ** Properties
        *********/
        /// <summary>The log file to which to write messages.</summary>
        private readonly LogFileManager LogFile;

        /// <summary>Manages console output interception.</summary>
        private readonly ConsoleInterceptionManager ConsoleManager = new ConsoleInterceptionManager();

        /// <summary>The core logger and monitor for SMAPI.</summary>
        private readonly Monitor Monitor;

        /// <summary>Tracks whether the game should exit immediately and any pending initialisation should be cancelled.</summary>
        private readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection = new Reflector();

        /// <summary>The underlying game instance.</summary>
        private SGame GameInstance;

        /// <summary>The underlying content manager.</summary>
        private SContentManager ContentManager => this.GameInstance.SContentManager;

        /// <summary>The SMAPI configuration settings.</summary>
        /// <remarks>This is initialised after the game starts.</remarks>
        private SConfig Settings;

        /// <summary>Tracks the installed mods.</summary>
        /// <remarks>This is initialised after the game starts.</remarks>
        private readonly ModRegistry ModRegistry = new ModRegistry();

        /// <summary>Manages deprecation warnings.</summary>
        /// <remarks>This is initialised after the game starts.</remarks>
        private DeprecationManager DeprecationManager;

        /// <summary>Manages console commands.</summary>
        /// <remarks>This is initialised after the game starts.</remarks>
        private CommandManager CommandManager;

        /// <summary>Manages SMAPI events for mods.</summary>
        private readonly EventManager EventManager;

        /// <summary>Whether the game is currently running.</summary>
        private bool IsGameRunning;

        /// <summary>Whether the program has been disposed.</summary>
        private bool IsDisposed;

        /// <summary>Regex patterns which match console messages to suppress from the console and log.</summary>
        private readonly Regex[] SuppressConsolePatterns =
        {
            new Regex(@"^TextBox\.Selected is now '(?:True|False)'\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^(?:FRUIT )?TREE: IsClient:(?:True|False) randomOutput: \d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)
        };

        /// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
        private readonly JsonHelper JsonHelper = new JsonHelper();


        /*********
        ** Public methods
        *********/
        /// <summary>The main entry point which hooks into and launches the game.</summary>
        /// <param name="args">The command-line arguments.</param>
        public static void Main(string[] args)
        {
            Program.AssertMinimumCompatibility();

            // get flags from arguments
            bool writeToConsole = !args.Contains("--no-terminal");

            // get log path from arguments
            string logPath = null;
            {
                int pathIndex = Array.LastIndexOf(args, "--log-path") + 1;
                if (pathIndex >= 1 && args.Length >= pathIndex)
                {
                    logPath = args[pathIndex];
                    if (!Path.IsPathRooted(logPath))
                        logPath = Path.Combine(Constants.LogDir, logPath);
                }
            }
            if (string.IsNullOrWhiteSpace(logPath))
                logPath = Constants.DefaultLogPath;

            // load SMAPI
            using (Program program = new Program(writeToConsole, logPath))
                program.RunInteractively();
        }

        /// <summary>Construct an instance.</summary>
        /// <param name="writeToConsole">Whether to output log messages to the console.</param>
        /// <param name="logPath">The full file path to which to write log messages.</param>
        public Program(bool writeToConsole, string logPath)
        {
            // init basics
            this.LogFile = new LogFileManager(logPath);
            this.Monitor = new Monitor("SMAPI", this.ConsoleManager, this.LogFile, this.CancellationTokenSource) { WriteToConsole = writeToConsole };
            this.EventManager = new EventManager(this.Monitor, this.ModRegistry);

            // hook up events
            ContentEvents.Init(this.EventManager);
            ControlEvents.Init(this.EventManager);
            GameEvents.Init(this.EventManager);
            GraphicsEvents.Init(this.EventManager);
            InputEvents.Init(this.EventManager);
            LocationEvents.Init(this.EventManager);
            MenuEvents.Init(this.EventManager);
            MineEvents.Init(this.EventManager);
            PlayerEvents.Init(this.EventManager);
            SaveEvents.Init(this.EventManager);
            SpecialisedEvents.Init(this.EventManager);
            TimeEvents.Init(this.EventManager);
        }

        /// <summary>Launch SMAPI.</summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical] // let try..catch handle corrupted state exceptions
        public void RunInteractively()
        {
            // initialise SMAPI
            try
            {
                // init logging
                this.Monitor.Log($"SMAPI {Constants.ApiVersion} with Stardew Valley {Constants.GameVersion} on {this.GetFriendlyPlatformName()}", LogLevel.Info);
                this.Monitor.Log($"Mods go here: {Constants.ModPath}");
                this.Monitor.Log($"Log started at {DateTime.UtcNow:s} UTC", LogLevel.Trace);

                // validate paths
                this.VerifyPath(Constants.ModPath);
                this.VerifyPath(Constants.LogDir);

                // validate game version
                if (Constants.GameVersion.IsOlderThan(Constants.MinimumGameVersion))
                {
                    this.Monitor.Log($"Oops! You're running Stardew Valley {Constants.GameVersion}, but the oldest supported version is {Constants.MinimumGameVersion}. Please update your game before using SMAPI.", LogLevel.Error);
                    this.PressAnyKeyToExit();
                    return;
                }
                if (Constants.MaximumGameVersion != null && Constants.GameVersion.IsNewerThan(Constants.MaximumGameVersion))
                {
                    this.Monitor.Log($"Oops! You're running Stardew Valley {Constants.GameVersion}, but this version of SMAPI is only compatible up to Stardew Valley {Constants.MaximumGameVersion}. Please check for a newer version of SMAPI: https://smapi.io.", LogLevel.Error);
                    this.PressAnyKeyToExit();
                    return;
                }

                // add error handlers
#if SMAPI_FOR_WINDOWS
                Application.ThreadException += (sender, e) => this.Monitor.Log($"Critical thread exception: {e.Exception.GetLogSummary()}", LogLevel.Error);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
#endif
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => this.Monitor.Log($"Critical app domain exception: {e.ExceptionObject}", LogLevel.Error);

                // override game
                this.GameInstance = new SGame(this.Monitor, this.Reflection, this.EventManager, this.InitialiseAfterGameStart);
                StardewValley.Program.gamePtr = this.GameInstance;

                // add exit handler
                new Thread(() =>
                {
                    this.CancellationTokenSource.Token.WaitHandle.WaitOne();
                    if (this.IsGameRunning)
                    {
                        try
                        {
                            File.WriteAllText(Constants.FatalCrashMarker, string.Empty);
                            File.Copy(this.LogFile.Path, Constants.FatalCrashLog, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"SMAPI failed trying to track the crash details: {ex.GetLogSummary()}");
                        }

                        this.GameInstance.Exit();
                    }
                }).Start();

                // hook into game events
#if SMAPI_FOR_WINDOWS
                ((Form)Control.FromHandle(this.GameInstance.Window.Handle)).FormClosing += (sender, args) => this.Dispose();
#endif
                this.GameInstance.Exiting += (sender, e) => this.Dispose();
                ContentEvents.AfterLocaleChanged += (sender, e) => this.OnLocaleChanged();

                // set window titles
                this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion}";
                Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion}";
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"SMAPI failed to initialise: {ex.GetLogSummary()}", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }

            // show details if game crashed during last session
            if (File.Exists(Constants.FatalCrashMarker))
            {
                this.Monitor.Log("The game crashed last time you played. That can be due to bugs in the game, but if it happens repeatedly you can ask for help here: http://community.playstarbound.com/threads/108375/.", LogLevel.Error);
                this.Monitor.Log($"If you ask for help, make sure to attach this file: {Constants.FatalCrashLog}", LogLevel.Error);
                this.Monitor.Log("Press any key to delete the crash data and continue playing.", LogLevel.Info);
                Console.ReadKey();
                File.Delete(Constants.FatalCrashLog);
                File.Delete(Constants.FatalCrashMarker);
            }

            // start game
            this.Monitor.Log("Starting game...", LogLevel.Trace);
            try
            {
                this.IsGameRunning = true;
                StardewValley.Program.releaseBuild = true; // game's debug logic interferes with SMAPI opening the game window
                this.GameInstance.Run();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"The game failed unexpectedly: {ex.GetLogSummary()}", LogLevel.Error);
                this.PressAnyKeyToExit();
            }
            finally
            {
                this.Dispose();
            }
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            this.Monitor.Log("Disposing...", LogLevel.Trace);

            // skip if already disposed
            if (this.IsDisposed)
                return;
            this.IsDisposed = true;

            // dispose mod data
            foreach (IModMetadata mod in this.ModRegistry.GetAll())
            {
                try
                {
                    (mod.Mod as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod failed during disposal: {ex.GetLogSummary()}.", LogLevel.Warn);
                }
            }

            // dispose core components
            this.IsGameRunning = false;
            this.LogFile?.Dispose();
            this.ConsoleManager?.Dispose();
            this.CancellationTokenSource?.Dispose();
            this.GameInstance?.Dispose();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Assert that the minimum conditions are present to initialise SMAPI without type load exceptions.</summary>
        private static void AssertMinimumCompatibility()
        {
            void PrintErrorAndExit(string message)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(message);
                Console.ResetColor();
                Program.PressAnyKeyToExit(showMessage: true);
            }

            // get game assembly name
            const string gameAssemblyName =
#if SMAPI_FOR_WINDOWS
                "Stardew Valley";
#else
                "StardewValley";
#endif

            // game not present
            if (Type.GetType($"StardewValley.Game1, {gameAssemblyName}", throwOnError: false) == null)
            {
                PrintErrorAndExit(
                    "Oops! SMAPI can't find the game. "
                    + (Assembly.GetCallingAssembly().Location.Contains(Path.Combine("internal", "Windows")) || Assembly.GetCallingAssembly().Location.Contains(Path.Combine("internal", "Mono"))
                        ? "It looks like you're running SMAPI from the download package, but you need to run the installed version instead. "
                        : "Make sure you're running StardewModdingAPI.exe in your game folder. "
                    )
                    + "See the readme.txt file for details."
                );
            }

            // Stardew Valley 1.2 types not present
            if (Type.GetType($"StardewValley.LocalizedContentManager+LanguageCode, {gameAssemblyName}", throwOnError: false) == null)
            {
                PrintErrorAndExit(Constants.GameVersion.IsOlderThan(Constants.MinimumGameVersion)
                    ? $"Oops! You're running Stardew Valley {Constants.GameVersion}, but the oldest supported version is {Constants.MinimumGameVersion}. Please update your game before using SMAPI."
                    : "Oops! SMAPI doesn't seem to be compatible with your game. Make sure you're running the latest version of Stardew Valley and SMAPI."
                );
            }
        }

        /// <summary>Initialise SMAPI and mods after the game starts.</summary>
        private void InitialiseAfterGameStart()
        {
            // load settings
            this.Settings = JsonConvert.DeserializeObject<SConfig>(File.ReadAllText(Constants.ApiConfigPath));
            this.GameInstance.VerboseLogging = this.Settings.VerboseLogging;

            // load core components
            this.DeprecationManager = new DeprecationManager(this.Monitor, this.ModRegistry);
            this.CommandManager = new CommandManager();

            // redirect direct console output
            {
                Monitor monitor = this.GetSecondaryMonitor("Console.Out");
                if (monitor.WriteToConsole)
                    this.ConsoleManager.OnMessageIntercepted += message => this.HandleConsoleMessage(monitor, message);
            }

            // add headers
            if (this.Settings.DeveloperMode)
            {
                this.Monitor.ShowTraceInConsole = true;
                this.Monitor.ShowFullStampInConsole = true;
                this.Monitor.Log($"You configured SMAPI to run in developer mode. The console may be much more verbose. You can disable developer mode by installing the non-developer version of SMAPI, or by editing {Constants.ApiConfigPath}.", LogLevel.Info);
            }
            if (!this.Settings.CheckForUpdates)
                this.Monitor.Log($"You configured SMAPI to not check for updates. Running an old version of SMAPI is not recommended. You can enable update checks by reinstalling SMAPI or editing {Constants.ApiConfigPath}.", LogLevel.Warn);
            if (!this.Monitor.WriteToConsole)
                this.Monitor.Log("Writing to the terminal is disabled because the --no-terminal argument was received. This usually means launching the terminal failed.", LogLevel.Warn);
            this.VerboseLog("Verbose logging enabled.");

            // validate XNB integrity
            if (!this.ValidateContentIntegrity())
                this.Monitor.Log("SMAPI found problems in your game's content files which are likely to cause errors or crashes. Consider uninstalling XNB mods or reinstalling the game.", LogLevel.Error);

            // load mod data
            ModDatabase modDatabase = new ModDatabase(this.Settings.ModData, Constants.GetUpdateUrl);

            // load mods
            {
                this.Monitor.Log("Loading mod metadata...", LogLevel.Trace);
                ModResolver resolver = new ModResolver();

                // load manifests
                IModMetadata[] mods = resolver.ReadManifests(Constants.ModPath, this.JsonHelper, modDatabase).ToArray();
                resolver.ValidateManifests(mods, Constants.ApiVersion, Constants.GetUpdateUrl);

                // process dependencies
                mods = resolver.ProcessDependencies(mods, modDatabase).ToArray();

                // load mods
                this.LoadMods(mods, this.JsonHelper, this.ContentManager, modDatabase);

                // check for updates
                this.CheckForUpdatesAsync(mods);
            }
            if (this.Monitor.IsExiting)
            {
                this.Monitor.Log("SMAPI shutting down: aborting initialisation.", LogLevel.Warn);
                return;
            }

            // update window titles
            int modsLoaded = this.ModRegistry.GetAll().Count();
            this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion} with {modsLoaded} mods";
            Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion} with {modsLoaded} mods";

            // start SMAPI console
            new Thread(this.RunConsoleLoop).Start();
        }

        /// <summary>Handle the game changing locale.</summary>
        private void OnLocaleChanged()
        {
            // get locale
            string locale = this.ContentManager.GetLocale();
            LocalizedContentManager.LanguageCode languageCode = this.ContentManager.GetCurrentLanguage();

            // update mod translation helpers
            foreach (IModMetadata mod in this.ModRegistry.GetAll(contentPacks: false))
                (mod.Mod.Helper.Translation as TranslationHelper)?.SetLocale(locale, languageCode);
        }

        /// <summary>Run a loop handling console input.</summary>
        [SuppressMessage("ReSharper", "FunctionNeverReturns", Justification = "The thread is aborted when the game exits.")]
        private void RunConsoleLoop()
        {
            // prepare console
            this.Monitor.Log("Type 'help' for help, or 'help <cmd>' for a command's usage", LogLevel.Info);
            this.CommandManager.Add("SMAPI", "help", "Lists command documentation.\n\nUsage: help\nLists all available commands.\n\nUsage: help <cmd>\n- cmd: The name of a command whose documentation to display.", this.HandleCommand);
            this.CommandManager.Add("SMAPI", "reload_i18n", "Reloads translation files for all mods.\n\nUsage: reload_i18n", this.HandleCommand);

            // start handling command line input
            Thread inputThread = new Thread(() =>
            {
                while (true)
                {
                    // get input
                    string input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    // parse input
                    try
                    {
                        if (!this.CommandManager.Trigger(input))
                            this.Monitor.Log("Unknown command; type 'help' for a list of available commands.", LogLevel.Error);
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"The handler registered for that command failed:\n{ex.GetLogSummary()}", LogLevel.Error);
                    }
                }
            });
            inputThread.Start();

            // keep console thread alive while the game is running
            while (this.IsGameRunning && !this.Monitor.IsExiting)
                Thread.Sleep(1000 / 10);
            if (inputThread.ThreadState == ThreadState.Running)
                inputThread.Abort();
        }

        /// <summary>Look for common issues with the game's XNB content, and log warnings if anything looks broken or outdated.</summary>
        /// <returns>Returns whether all integrity checks passed.</returns>
        private bool ValidateContentIntegrity()
        {
            this.Monitor.Log("Detecting common issues...", LogLevel.Trace);
            bool issuesFound = false;

            // object format (commonly broken by outdated files)
            {
                // detect issues
                bool hasObjectIssues = false;
                void LogIssue(int id, string issue) => this.Monitor.Log($@"Detected issue: item #{id} in Content\Data\ObjectInformation.xnb is invalid ({issue}).", LogLevel.Trace);
                foreach (KeyValuePair<int, string> entry in Game1.objectInformation)
                {
                    // must not be empty
                    if (string.IsNullOrWhiteSpace(entry.Value))
                    {
                        LogIssue(entry.Key, "entry is empty");
                        hasObjectIssues = true;
                        continue;
                    }

                    // require core fields
                    string[] fields = entry.Value.Split('/');
                    if (fields.Length < SObject.objectInfoDescriptionIndex + 1)
                    {
                        LogIssue(entry.Key, "too few fields for an object");
                        hasObjectIssues = true;
                        continue;
                    }

                    // check min length for specific types
                    switch (fields[SObject.objectInfoTypeIndex].Split(new[] { ' ' }, 2)[0])
                    {
                        case "Cooking":
                            if (fields.Length < SObject.objectInfoBuffDurationIndex + 1)
                            {
                                LogIssue(entry.Key, "too few fields for a cooking item");
                                hasObjectIssues = true;
                            }
                            break;
                    }
                }

                // log error
                if (hasObjectIssues)
                {
                    issuesFound = true;
                    this.Monitor.Log(@"Your Content\Data\ObjectInformation.xnb file seems to be broken or outdated.", LogLevel.Warn);
                }
            }

            return !issuesFound;
        }

        /// <summary>Asynchronously check for a new version of SMAPI and any installed mods, and print alerts to the console if an update is available.</summary>
        /// <param name="mods">The mods to include in the update check (if eligible).</param>
        private void CheckForUpdatesAsync(IModMetadata[] mods)
        {
            if (!this.Settings.CheckForUpdates)
                return;

            new Thread(() =>
            {
                // create client
                WebApiClient client = new WebApiClient(this.Settings.WebApiBaseUrl, Constants.ApiVersion);
                this.Monitor.Log("Checking for updates...", LogLevel.Trace);

                // check SMAPI version
                try
                {
                    ModInfoModel response = client.GetModInfo($"GitHub:{this.Settings.GitHubProjectName}").Single().Value;
                    if (response.Error != null)
                    {
                        this.Monitor.Log("Couldn't check for a new version of SMAPI. This won't affect your game, but you may not be notified of new versions if this keeps happening.", LogLevel.Warn);
                        this.Monitor.Log($"Error: {response.Error}");
                    }
                    else if (new SemanticVersion(response.Version).IsNewerThan(Constants.ApiVersion))
                        this.Monitor.Log($"You can update SMAPI to {response.Version}: {response.Url}", LogLevel.Alert);
                    else
                        this.Monitor.Log("   SMAPI okay.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log("Couldn't check for a new version of SMAPI. This won't affect your game, but you won't be notified of new versions if this keeps happening.", LogLevel.Warn);
                    this.Monitor.Log(ex is WebException && ex.InnerException == null
                        ? $"Error: {ex.Message}"
                        : $"Error: {ex.GetLogSummary()}"
                    );
                }

                // check mod versions
                if (mods.Any())
                {
                    try
                    {
                        // prepare update keys
                        Dictionary<string, IModMetadata[]> modsByKey =
                            (
                                from mod in mods
                                where mod.Manifest?.UpdateKeys != null
                                from key in mod.Manifest.UpdateKeys
                                select new { key, mod }
                            )
                            .GroupBy(p => p.key, StringComparer.InvariantCultureIgnoreCase)
                            .ToDictionary(
                                group => group.Key,
                                group => group.Select(p => p.mod).ToArray(),
                                StringComparer.InvariantCultureIgnoreCase
                            );

                        // report update keys
                        {
                            IModMetadata[] modsWithoutKeys = (
                                from mod in mods
                                where
                                    mod.Manifest != null
                                    && (mod.Manifest.UpdateKeys == null || !mod.Manifest.UpdateKeys.Any())
                                    && (mod.Manifest?.UniqueID != "SMAPI.ConsoleCommands" && mod.Manifest?.UniqueID != "SMAPI.TrainerMod")
                                orderby mod.DisplayName
                                select mod
                            ).ToArray();

                            string message = $"Checking {modsByKey.Count} mod update keys.";
                            if (modsWithoutKeys.Any())
                                message += $" {modsWithoutKeys.Length} mods have no update keys: {string.Join(", ", modsWithoutKeys.Select(p => p.DisplayName))}.";
                            this.Monitor.Log($"   {message}", LogLevel.Trace);
                        }

                        // fetch results
                        var results =
                            (
                                from entry in client.GetModInfo(modsByKey.Keys.ToArray())
                                from mod in modsByKey[entry.Key]
                                orderby mod.DisplayName
                                select new { entry.Key, Mod = mod, Info = entry.Value }
                            )
                            .ToArray();

                        // extract latest versions
                        IDictionary<IModMetadata, ModInfoModel> updatesByMod = new Dictionary<IModMetadata, ModInfoModel>();
                        foreach (var result in results)
                        {
                            IModMetadata mod = result.Mod;
                            ModInfoModel remoteInfo = result.Info;

                            // handle error
                            if (remoteInfo.Error != null)
                            {
                                this.Monitor.Log($"   {mod.DisplayName} ({result.Key}): update error: {remoteInfo.Error}", LogLevel.Trace);
                                continue;
                            }

                            // normalise versions
                            ISemanticVersion localVersion = mod.DataRecord?.GetLocalVersionForUpdateChecks(mod.Manifest.Version) ?? mod.Manifest.Version;
                            if (!SemanticVersion.TryParse(mod.DataRecord?.GetRemoteVersionForUpdateChecks(remoteInfo.Version) ?? remoteInfo.Version, out ISemanticVersion remoteVersion))
                            {
                                this.Monitor.Log($"   {mod.DisplayName} ({result.Key}): update error: Mod has invalid version {remoteInfo.Version}", LogLevel.Trace);
                                continue;
                            }

                            // compare versions
                            bool isUpdate = remoteVersion.IsNewerThan(localVersion);
                            this.VerboseLog($"   {mod.DisplayName} ({result.Key}): {(isUpdate ? $"{mod.Manifest.Version}{(!localVersion.Equals(mod.Manifest.Version) ? $" [{localVersion}]" : "")} => {remoteInfo.Version}{(!remoteVersion.Equals(new SemanticVersion(remoteInfo.Version)) ? $" [{remoteVersion}]" : "")}" : "okay")}.");
                            if (isUpdate)
                            {
                                if (!updatesByMod.TryGetValue(mod, out ModInfoModel other) || remoteVersion.IsNewerThan(other.Version))
                                    updatesByMod[mod] = remoteInfo;
                            }
                        }

                        // output
                        if (updatesByMod.Any())
                        {
                            this.Monitor.Newline();
                            this.Monitor.Log($"You can update {updatesByMod.Count} mod{(updatesByMod.Count != 1 ? "s" : "")}:", LogLevel.Alert);
                            foreach (var entry in updatesByMod.OrderBy(p => p.Key.DisplayName))
                                this.Monitor.Log($"   {entry.Key.DisplayName} {entry.Value.Version}: {entry.Value.Url}", LogLevel.Alert);
                        }
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log("Couldn't check for new mod versions. This won't affect your game, but you won't be notified of mod updates if this keeps happening.", LogLevel.Warn);
                        this.Monitor.Log(ex is WebException && ex.InnerException == null
                            ? ex.Message
                            : ex.ToString()
                        );
                    }
                }
            }).Start();
        }

        /// <summary>Create a directory path if it doesn't exist.</summary>
        /// <param name="path">The directory path.</param>
        private void VerifyPath(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't create a path: {path}\n\n{ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Load and hook up the given mods.</summary>
        /// <param name="mods">The mods to load.</param>
        /// <param name="jsonHelper">The JSON helper with which to read mods' JSON files.</param>
        /// <param name="contentManager">The content manager to use for mod content.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        private void LoadMods(IModMetadata[] mods, JsonHelper jsonHelper, SContentManager contentManager, ModDatabase modDatabase)
        {
            this.Monitor.Log("Loading mods...", LogLevel.Trace);

            IDictionary<IModMetadata, string[]> skippedMods = new Dictionary<IModMetadata, string[]>();
            void TrackSkip(IModMetadata mod, string userReasonPhrase, string devReasonPhrase = null) => skippedMods[mod] = new[] { userReasonPhrase, devReasonPhrase };

            // load content packs
            foreach (IModMetadata metadata in mods.Where(p => p.IsContentPack))
            {
                // get basic info
                IManifest manifest = metadata.Manifest;
                this.Monitor.Log($"Loading {metadata.DisplayName} from {PathUtilities.GetRelativePath(Constants.ModPath, metadata.DirectoryPath)} (content pack)...", LogLevel.Trace);

                // validate status
                if (metadata.Status == ModMetadataStatus.Failed)
                {
                    this.Monitor.Log($"   Failed: {metadata.Error}", LogLevel.Trace);
                    TrackSkip(metadata, metadata.Error);
                    continue;
                }

                // load mod as content pack
                IMonitor monitor = this.GetSecondaryMonitor(metadata.DisplayName);
                IContentHelper contentHelper = new ContentHelper(contentManager, metadata.DirectoryPath, manifest.UniqueID, metadata.DisplayName, monitor);
                IContentPack contentPack = new ContentPack(metadata.DirectoryPath, manifest, contentHelper, jsonHelper);
                metadata.SetMod(contentPack, monitor);
                this.ModRegistry.Add(metadata);
            }
            IModMetadata[] loadedContentPacks = this.ModRegistry.GetAll(assemblyMods: false).ToArray();

            // load mods
            {
                // get content packs by mod ID
                IDictionary<string, IContentPack[]> contentPacksByModID =
                    loadedContentPacks
                    .GroupBy(p => p.Manifest.ContentPackFor.UniqueID)
                    .ToDictionary(
                            group => group.Key,
                            group => group.Select(metadata => metadata.ContentPack).ToArray(),
                            StringComparer.InvariantCultureIgnoreCase
                    );

                // get assembly loaders
                AssemblyLoader modAssemblyLoader = new AssemblyLoader(Constants.TargetPlatform, this.Monitor, this.Settings.DeveloperMode);
                AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => modAssemblyLoader.ResolveAssembly(e.Name);
                InterfaceProxyFactory proxyFactory = new InterfaceProxyFactory();

                // load from metadata
                foreach (IModMetadata metadata in mods.Where(p => !p.IsContentPack))
                {
                    // get basic info
                    IManifest manifest = metadata.Manifest;
                    this.Monitor.Log(metadata.Manifest?.EntryDll != null
                        ? $"Loading {metadata.DisplayName} from {PathUtilities.GetRelativePath(Constants.ModPath, metadata.DirectoryPath)}{Path.DirectorySeparatorChar}{metadata.Manifest.EntryDll}..." // don't use Path.Combine here, since EntryDLL might not be valid
                        : $"Loading {metadata.DisplayName}...", LogLevel.Trace);

                    // validate status
                    if (metadata.Status == ModMetadataStatus.Failed)
                    {
                        this.Monitor.Log($"   Failed: {metadata.Error}", LogLevel.Trace);
                        TrackSkip(metadata, metadata.Error);
                        continue;
                    }

                    // load mod
                    string assemblyPath = metadata.Manifest?.EntryDll != null
                        ? Path.Combine(metadata.DirectoryPath, metadata.Manifest.EntryDll)
                        : null;
                    Assembly modAssembly;
                    try
                    {
                        modAssembly = modAssemblyLoader.Load(metadata, assemblyPath, assumeCompatible: metadata.DataRecord?.Status == ModStatus.AssumeCompatible);
                    }
                    catch (IncompatibleInstructionException) // details already in trace logs
                    {
                        string[] updateUrls = new[] { modDatabase.GetModPageUrlFor(metadata.Manifest.UniqueID), "https://smapi.io/compat" }.Where(p => p != null).ToArray();

                        TrackSkip(metadata, $"it's outdated. Please check for a new version at {string.Join(" or ", updateUrls)}.");
                        continue;
                    }
                    catch (SAssemblyLoadFailedException ex)
                    {
                        TrackSkip(metadata, $"it DLL couldn't be loaded: {ex.Message}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        TrackSkip(metadata, "its DLL couldn't be loaded.", $"Error: {ex.GetLogSummary()}");
                        continue;
                    }

                    // initialise mod
                    try
                    {
                        // get content packs
                        if (!contentPacksByModID.TryGetValue(manifest.UniqueID, out IContentPack[] contentPacks))
                            contentPacks = new IContentPack[0];

                        // init mod helpers
                        IMonitor monitor = this.GetSecondaryMonitor(metadata.DisplayName);
                        IModHelper modHelper;
                        {
                            ICommandHelper commandHelper = new CommandHelper(manifest.UniqueID, metadata.DisplayName, this.CommandManager);
                            IContentHelper contentHelper = new ContentHelper(contentManager, metadata.DirectoryPath, manifest.UniqueID, metadata.DisplayName, monitor);
                            IReflectionHelper reflectionHelper = new ReflectionHelper(manifest.UniqueID, metadata.DisplayName, this.Reflection, this.DeprecationManager);
                            IModRegistry modRegistryHelper = new ModRegistryHelper(manifest.UniqueID, this.ModRegistry, proxyFactory, monitor);
                            ITranslationHelper translationHelper = new TranslationHelper(manifest.UniqueID, manifest.Name, contentManager.GetLocale(), contentManager.GetCurrentLanguage());

                            IContentPack CreateTransitionalContentPack(string packDirPath, IManifest packManifest)
                            {
                                IMonitor packMonitor = this.GetSecondaryMonitor(packManifest.Name);
                                IContentHelper packContentHelper = new ContentHelper(contentManager, packDirPath, packManifest.UniqueID, packManifest.Name, packMonitor);
                                return new ContentPack(packDirPath, packManifest, packContentHelper, this.JsonHelper);
                            }

                            modHelper = new ModHelper(manifest.UniqueID, metadata.DirectoryPath, jsonHelper, contentHelper, commandHelper, modRegistryHelper, reflectionHelper, translationHelper, contentPacks, CreateTransitionalContentPack, this.DeprecationManager);
                        }

                        // get mod instance
                        if (!this.TryLoadModEntry(modAssembly, error => TrackSkip(metadata, error), out Mod mod))
                            continue;

                        // init mod
                        mod.ModManifest = manifest;
                        mod.Helper = modHelper;
                        mod.Monitor = monitor;

                        // track mod
                        metadata.SetMod(mod);
                        this.ModRegistry.Add(metadata);
                    }
                    catch (Exception ex)
                    {
                        TrackSkip(metadata, $"initialisation failed:\n{ex.GetLogSummary()}");
                    }
                }
            }
            IModMetadata[] loadedMods = this.ModRegistry.GetAll(contentPacks: false).ToArray();

            // log skipped mods
            this.Monitor.Newline();
            if (skippedMods.Any())
            {
                this.Monitor.Log($"Skipped {skippedMods.Count} mods:", LogLevel.Error);
                foreach (var pair in skippedMods.OrderBy(p => p.Key.DisplayName))
                {
                    IModMetadata mod = pair.Key;
                    string[] reason = pair.Value;

                    this.Monitor.Log($"   {mod.DisplayName}{(mod.Manifest?.Version != null ? " " + mod.Manifest.Version.ToString() : "")} because {reason[0]}", LogLevel.Error);
                    if (reason[1] != null)
                        this.Monitor.Log($"     {reason[1]}", LogLevel.Trace);
                }
                this.Monitor.Newline();
            }

            // log loaded mods
            this.Monitor.Log($"Loaded {loadedMods.Length} mods" + (loadedMods.Length > 0 ? ":" : "."), LogLevel.Info);

            foreach (IModMetadata metadata in loadedMods.OrderBy(p => p.DisplayName))
            {
                IManifest manifest = metadata.Manifest;
                this.Monitor.Log(
                    $"   {metadata.DisplayName} {manifest.Version}"
                        + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                        + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                    LogLevel.Info
                );
            }
            this.Monitor.Newline();

            // log loaded content packs
            if (loadedContentPacks.Any())
            {
                string GetModDisplayName(string id) => loadedMods.First(p => id != null && id.Equals(p.Manifest?.UniqueID, StringComparison.InvariantCultureIgnoreCase))?.DisplayName;

                this.Monitor.Log($"Loaded {loadedContentPacks.Length} content packs:", LogLevel.Info);
                foreach (IModMetadata metadata in loadedContentPacks.OrderBy(p => p.DisplayName))
                {
                    IManifest manifest = metadata.Manifest;
                    this.Monitor.Log(
                        $"   {metadata.DisplayName} {manifest.Version}"
                        + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                        + (metadata.IsContentPack ? $" | for {GetModDisplayName(metadata.Manifest.ContentPackFor.UniqueID)}" : "")
                        + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                        LogLevel.Info
                    );
                }
                this.Monitor.Newline();
            }

            // initialise translations
            this.ReloadTranslations(loadedMods);

            // initialise loaded non-content-pack mods
            foreach (IModMetadata metadata in loadedMods)
            {
                // add interceptors
                if (metadata.Mod.Helper.Content is ContentHelper helper)
                {
                    // ReSharper disable SuspiciousTypeConversion.Global
                    if (metadata.Mod is IAssetEditor editor)
                        helper.ObservableAssetEditors.Add(editor);
                    if (metadata.Mod is IAssetLoader loader)
                        helper.ObservableAssetLoaders.Add(loader);
                    // ReSharper restore SuspiciousTypeConversion.Global

                    this.ContentManager.Editors[metadata] = helper.ObservableAssetEditors;
                    this.ContentManager.Loaders[metadata] = helper.ObservableAssetLoaders;
                }

                // call entry method
                try
                {
                    IMod mod = metadata.Mod;
                    mod.Entry(mod.Helper);
                }
                catch (Exception ex)
                {
                    metadata.LogAsMod($"Mod crashed on entry and might not work correctly. Technical details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                // get mod API
                try
                {
                    object api = metadata.Mod.GetApi();
                    if (api != null && !api.GetType().IsPublic)
                    {
                        api = null;
                        this.Monitor.Log($"{metadata.DisplayName} provides an API instance with a non-public type. This isn't currently supported, so the API won't be available to other mods.", LogLevel.Warn);
                    }

                    if (api != null)
                        this.Monitor.Log($"   Found mod-provided API ({api.GetType().FullName}).", LogLevel.Trace);
                    metadata.SetApi(api);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Failed loading mod-provided API for {metadata.DisplayName}. Integrations with other mods may not work. Error: {ex.GetLogSummary()}", LogLevel.Error);
                }
            }

            // invalidate cache entries when needed
            // (These listeners are registered after Entry to avoid repeatedly reloading assets as mods initialise.)
            foreach (IModMetadata metadata in loadedMods)
            {
                if (metadata.Mod.Helper.Content is ContentHelper helper)
                {
                    helper.ObservableAssetEditors.CollectionChanged += (sender, e) =>
                    {
                        if (e.NewItems.Count > 0)
                        {
                            this.Monitor.Log("Invalidating cache entries for new asset editors...", LogLevel.Trace);
                            this.ContentManager.InvalidateCacheFor(e.NewItems.Cast<IAssetEditor>().ToArray(), new IAssetLoader[0]);
                        }
                    };
                    helper.ObservableAssetLoaders.CollectionChanged += (sender, e) =>
                    {
                        if (e.NewItems.Count > 0)
                        {
                            this.Monitor.Log("Invalidating cache entries for new asset loaders...", LogLevel.Trace);
                            this.ContentManager.InvalidateCacheFor(new IAssetEditor[0], e.NewItems.Cast<IAssetLoader>().ToArray());
                        }
                    };
                }
            }

            // reset cache now if any editors or loaders were added during entry
            IAssetEditor[] editors = loadedMods.SelectMany(p => p.Mod.Helper.Content.AssetEditors).ToArray();
            IAssetLoader[] loaders = loadedMods.SelectMany(p => p.Mod.Helper.Content.AssetLoaders).ToArray();
            if (editors.Any() || loaders.Any())
            {
                this.Monitor.Log("Invalidating cached assets for new editors & loaders...", LogLevel.Trace);
                this.ContentManager.InvalidateCacheFor(editors, loaders);
            }

            // unlock mod integrations
            this.ModRegistry.AreAllModsInitialised = true;
        }

        /// <summary>Load a mod's entry class.</summary>
        /// <param name="modAssembly">The mod assembly.</param>
        /// <param name="onError">A callback invoked when loading fails.</param>
        /// <param name="mod">The loaded instance.</param>
        private bool TryLoadModEntry(Assembly modAssembly, Action<string> onError, out Mod mod)
        {
            mod = null;

            // find type
            TypeInfo[] modEntries = modAssembly.DefinedTypes.Where(type => typeof(Mod).IsAssignableFrom(type) && !type.IsAbstract).Take(2).ToArray();
            if (modEntries.Length == 0)
            {
                onError($"its DLL has no '{nameof(Mod)}' subclass.");
                return false;
            }
            if (modEntries.Length > 1)
            {
                onError($"its DLL contains multiple '{nameof(Mod)}' subclasses.");
                return false;
            }

            // get implementation
            mod = (Mod)modAssembly.CreateInstance(modEntries[0].ToString());
            if (mod == null)
            {
                onError("its entry class couldn't be instantiated.");
                return false;
            }

            return true;
        }

        /// <summary>Reload translations for all mods.</summary>
        /// <param name="mods">The mods for which to reload translations.</param>
        private void ReloadTranslations(IEnumerable<IModMetadata> mods)
        {
            JsonHelper jsonHelper = new JsonHelper();
            foreach (IModMetadata metadata in mods)
            {
                if (metadata.IsContentPack)
                    throw new InvalidOperationException("Can't reload translations for a content pack.");

                // read translation files
                IDictionary<string, IDictionary<string, string>> translations = new Dictionary<string, IDictionary<string, string>>();
                DirectoryInfo translationsDir = new DirectoryInfo(Path.Combine(metadata.DirectoryPath, "i18n"));
                if (translationsDir.Exists)
                {
                    foreach (FileInfo file in translationsDir.EnumerateFiles("*.json"))
                    {
                        string locale = Path.GetFileNameWithoutExtension(file.Name.ToLower().Trim());
                        try
                        {
                            translations[locale] = jsonHelper.ReadJsonFile<IDictionary<string, string>>(file.FullName);
                        }
                        catch (Exception ex)
                        {
                            metadata.LogAsMod($"Mod's i18n/{locale}.json file couldn't be parsed: {ex.GetLogSummary()}");
                        }
                    }
                }

                // validate translations
                foreach (string locale in translations.Keys)
                {
                    HashSet<string> keys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    HashSet<string> duplicateKeys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    foreach (string key in translations[locale].Keys.ToArray())
                    {
                        if (!keys.Add(key))
                        {
                            duplicateKeys.Add(key);
                            translations[locale].Remove(key);
                        }
                    }

                    if (duplicateKeys.Any())
                        metadata.LogAsMod($"Mod's i18n/{locale}.json has duplicate translation keys: [{string.Join(", ", duplicateKeys)}]. Keys are case-insensitive.", LogLevel.Warn);
                }

                // update translation
                TranslationHelper translationHelper = (TranslationHelper)metadata.Mod.Helper.Translation;
                translationHelper.SetTranslations(translations);
            }
        }

        /// <summary>The method called when the user submits a core SMAPI command in the console.</summary>
        /// <param name="name">The command name.</param>
        /// <param name="arguments">The command arguments.</param>
        private void HandleCommand(string name, string[] arguments)
        {
            switch (name)
            {
                case "help":
                    if (arguments.Any())
                    {
                        Command result = this.CommandManager.Get(arguments[0]);
                        if (result == null)
                            this.Monitor.Log("There's no command with that name.", LogLevel.Error);
                        else
                            this.Monitor.Log($"{result.Name}: {result.Documentation}\n(Added by {result.ModName}.)", LogLevel.Info);
                    }
                    else
                    {
                        string message = "The following commands are registered:\n";
                        IGrouping<string, string>[] groups = (from command in this.CommandManager.GetAll() orderby command.ModName, command.Name group command.Name by command.ModName).ToArray();
                        foreach (var group in groups)
                        {
                            string modName = group.Key;
                            string[] commandNames = group.ToArray();
                            message += $"{modName}:\n  {string.Join("\n  ", commandNames)}\n\n";
                        }
                        message += "For more information about a command, type 'help command_name'.";

                        this.Monitor.Log(message, LogLevel.Info);
                    }
                    break;

                case "reload_i18n":
                    this.ReloadTranslations(this.ModRegistry.GetAll(contentPacks: false));
                    this.Monitor.Log("Reloaded translation files for all mods. This only affects new translations the mods fetch; if they cached some text, it may not be updated.", LogLevel.Info);
                    break;

                default:
                    throw new NotSupportedException($"Unrecognise core SMAPI command '{name}'.");
            }
        }

        /// <summary>Redirect messages logged directly to the console to the given monitor.</summary>
        /// <param name="monitor">The monitor with which to log messages.</param>
        /// <param name="message">The message to log.</param>
        private void HandleConsoleMessage(IMonitor monitor, string message)
        {
            // detect exception
            LogLevel level = message.Contains("Exception") ? LogLevel.Error : LogLevel.Trace;

            // ignore suppressed message
            if (level != LogLevel.Error && this.SuppressConsolePatterns.Any(p => p.IsMatch(message)))
                return;

            // forward to monitor
            monitor.Log(message, level);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        private void PressAnyKeyToExit()
        {
            this.Monitor.Log("Game has ended. Press any key to exit.", LogLevel.Info);
            Program.PressAnyKeyToExit(showMessage: false);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        /// <param name="showMessage">Whether to print a 'press any key to exit' message to the console.</param>
        private static void PressAnyKeyToExit(bool showMessage)
        {
            if (showMessage)
                Console.WriteLine("Game has ended. Press any key to exit.");
            Thread.Sleep(100);
            Console.ReadKey();
            Environment.Exit(0);
        }

        /// <summary>Get a monitor instance derived from SMAPI's current settings.</summary>
        /// <param name="name">The name of the module which will log messages with this instance.</param>
        private Monitor GetSecondaryMonitor(string name)
        {
            return new Monitor(name, this.ConsoleManager, this.LogFile, this.CancellationTokenSource)
            {
                WriteToConsole = this.Monitor.WriteToConsole,
                ShowTraceInConsole = this.Settings.DeveloperMode,
                ShowFullStampInConsole = this.Settings.DeveloperMode
            };
        }

        /// <summary>Get a human-readable name for the current platform.</summary>
        [SuppressMessage("ReSharper", "EmptyGeneralCatchClause", Justification = "Error suppressed deliberately to fallback to default behaviour.")]
        private string GetFriendlyPlatformName()
        {
#if SMAPI_FOR_WINDOWS
            try
            {
                return new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem")
                    .Get()
                    .Cast<ManagementObject>()
                    .Select(entry => entry.GetPropertyValue("Caption").ToString())
                    .FirstOrDefault();
            }
            catch { }
#endif
            return Environment.OSVersion.ToString();
        }

        /// <summary>Log a message if verbose mode is enabled.</summary>
        /// <param name="message">The message to log.</param>
        private void VerboseLog(string message)
        {
            if (this.Settings.VerboseLogging)
                this.Monitor.Log(message, LogLevel.Trace);
        }
    }
}
