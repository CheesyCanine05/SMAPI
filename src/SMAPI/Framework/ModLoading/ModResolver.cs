using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.ModData;
using StardewModdingAPI.Framework.Models;
using StardewModdingAPI.Framework.Serialisation;
using StardewModdingAPI.Framework.Utilities;

namespace StardewModdingAPI.Framework.ModLoading
{
    /// <summary>Finds and processes mod metadata.</summary>
    internal class ModResolver
    {
        /*********
        ** Public methods
        *********/
        /// <summary>Get manifest metadata for each folder in the given root path.</summary>
        /// <param name="rootPath">The root path to search for mods.</param>
        /// <param name="jsonHelper">The JSON helper with which to read manifests.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        /// <returns>Returns the manifests by relative folder.</returns>
        public IEnumerable<IModMetadata> ReadManifests(string rootPath, JsonHelper jsonHelper, ModDatabase modDatabase)
        {
            foreach (DirectoryInfo modDir in this.GetModFolders(rootPath))
            {
                // read file
                Manifest manifest = null;
                string path = Path.Combine(modDir.FullName, "manifest.json");
                string error = null;
                try
                {
                    manifest = jsonHelper.ReadJsonFile<Manifest>(path);
                    if (manifest == null)
                    {
                        error = File.Exists(path)
                            ? "its manifest is invalid."
                            : "it doesn't have a manifest.";
                    }
                }
                catch (SParseException ex)
                {
                    error = $"parsing its manifest failed: {ex.Message}";
                }
                catch (Exception ex)
                {
                    error = $"parsing its manifest failed:\n{ex.GetLogSummary()}";
                }

                // parse internal data record (if any)
                ParsedModDataRecord dataRecord = modDatabase.GetParsed(manifest);

                // get display name
                string displayName = manifest?.Name;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = dataRecord?.DisplayName;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = PathUtilities.GetRelativePath(rootPath, modDir.FullName);

                // apply defaults
                if (manifest != null && dataRecord != null)
                {
                    if (dataRecord.UpdateKey != null)
                        manifest.UpdateKeys = new[] { dataRecord.UpdateKey };
                }

                // build metadata
                ModMetadataStatus status = error == null
                    ? ModMetadataStatus.Found
                    : ModMetadataStatus.Failed;
                yield return new ModMetadata(displayName, modDir.FullName, manifest, dataRecord).SetStatus(status, error);
            }
        }

        /// <summary>Validate manifest metadata.</summary>
        /// <param name="mods">The mod manifests to validate.</param>
        /// <param name="apiVersion">The current SMAPI version.</param>
        /// <param name="getUpdateUrl">Get an update URL for an update key (if valid).</param>
        public void ValidateManifests(IEnumerable<IModMetadata> mods, ISemanticVersion apiVersion, Func<string, string> getUpdateUrl)
        {
            mods = mods.ToArray();

            // validate each manifest
            foreach (IModMetadata mod in mods)
            {
                // skip if already failed
                if (mod.Status == ModMetadataStatus.Failed)
                    continue;

                // validate compatibility from internal data
                switch (mod.DataRecord?.Status)
                {
                    case ModStatus.Obsolete:
                        mod.SetStatus(ModMetadataStatus.Failed, $"it's obsolete: {mod.DataRecord.StatusReasonPhrase}");
                        continue;

                    case ModStatus.AssumeBroken:
                        {
                            // get reason
                            string reasonPhrase = mod.DataRecord.StatusReasonPhrase ?? "it's outdated";

                            // get update URLs
                            List<string> updateUrls = new List<string>();
                            foreach (string key in mod.Manifest.UpdateKeys ?? new string[0])
                            {
                                string url = getUpdateUrl(key);
                                if (url != null)
                                    updateUrls.Add(url);
                            }
                            if (mod.DataRecord.AlternativeUrl != null)
                                updateUrls.Add(mod.DataRecord.AlternativeUrl);

                            // default update URL
                            updateUrls.Add("https://smapi.io/compat");

                            // build error
                            string error = $"{reasonPhrase}. Please check for a ";
                            if (mod.DataRecord.StatusUpperVersion == null || mod.Manifest.Version.Equals(mod.DataRecord.StatusUpperVersion))
                                error += "newer version";
                            else
                                error += $"version newer than {mod.DataRecord.StatusUpperVersion}";
                            error += " at " + string.Join(" or ", updateUrls);

                            mod.SetStatus(ModMetadataStatus.Failed, error);
                        }
                        continue;
                }

                // validate SMAPI version
                if (mod.Manifest.MinimumApiVersion?.IsNewerThan(apiVersion) == true)
                {
                    mod.SetStatus(ModMetadataStatus.Failed, $"it needs SMAPI {mod.Manifest.MinimumApiVersion} or later. Please update SMAPI to the latest version to use this mod.");
                    continue;
                }

                // validate DLL / content pack fields
                {
                    bool hasDll = !string.IsNullOrWhiteSpace(mod.Manifest.EntryDll);
                    bool isContentPack = mod.Manifest.ContentPackFor != null;

                    // validate field presence
                    if (!hasDll && !isContentPack)
                    {
                        mod.SetStatus(ModMetadataStatus.Failed, $"its manifest has no {nameof(IManifest.EntryDll)} or {nameof(IManifest.ContentPackFor)} field; must specify one.");
                        continue;
                    }
                    if (hasDll && isContentPack)
                    {
                        mod.SetStatus(ModMetadataStatus.Failed, $"its manifest sets both {nameof(IManifest.EntryDll)} and {nameof(IManifest.ContentPackFor)}, which are mutually exclusive.");
                        continue;
                    }

                    // validate DLL
                    if (hasDll)
                    {
                        // invalid filename format
                        if (mod.Manifest.EntryDll.Intersect(Path.GetInvalidFileNameChars()).Any())
                        {
                            mod.SetStatus(ModMetadataStatus.Failed, $"its manifest has invalid filename '{mod.Manifest.EntryDll}' for the EntryDLL field.");
                            continue;
                        }

                        // invalid path
                        string assemblyPath = Path.Combine(mod.DirectoryPath, mod.Manifest.EntryDll);
                        if (!File.Exists(assemblyPath))
                        {
                            mod.SetStatus(ModMetadataStatus.Failed, $"its DLL '{mod.Manifest.EntryDll}' doesn't exist.");
                            continue;
                        }
                    }

                    // validate content pack
                    else
                    {
                        // invalid content pack ID
                        if (string.IsNullOrWhiteSpace(mod.Manifest.ContentPackFor.UniqueID))
                        {
                            mod.SetStatus(ModMetadataStatus.Failed, $"its manifest declares {nameof(IManifest.ContentPackFor)} without its required {nameof(IManifestContentPackFor.UniqueID)} field.");
                            continue;
                        }
                    }
                }

                // validate required fields
                {
                    List<string> missingFields = new List<string>(3);

                    if (string.IsNullOrWhiteSpace(mod.Manifest.Name))
                        missingFields.Add(nameof(IManifest.Name));
                    if (mod.Manifest.Version == null || mod.Manifest.Version.ToString() == "0.0")
                        missingFields.Add(nameof(IManifest.Version));
                    if (string.IsNullOrWhiteSpace(mod.Manifest.UniqueID))
                        missingFields.Add(nameof(IManifest.UniqueID));

                    if (missingFields.Any())
                        mod.SetStatus(ModMetadataStatus.Failed, $"its manifest is missing required fields ({string.Join(", ", missingFields)}).");
                }
            }

            // validate IDs are unique
            {
                var duplicatesByID = mods
                    .GroupBy(mod => mod.Manifest?.UniqueID?.Trim(), mod => mod, StringComparer.InvariantCultureIgnoreCase)
                    .Where(p => p.Count() > 1);
                foreach (var group in duplicatesByID)
                {
                    foreach (IModMetadata mod in group)
                    {
                        if (mod.Status == ModMetadataStatus.Failed)
                            continue; // don't replace metadata error
                        mod.SetStatus(ModMetadataStatus.Failed, $"its unique ID '{mod.Manifest.UniqueID}' is used by multiple mods ({string.Join(", ", group.Select(p => p.DisplayName))}).");
                    }
                }
            }
        }

        /// <summary>Sort the given mods by the order they should be loaded.</summary>
        /// <param name="mods">The mods to process.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        public IEnumerable<IModMetadata> ProcessDependencies(IEnumerable<IModMetadata> mods, ModDatabase modDatabase)
        {
            // initialise metadata
            mods = mods.ToArray();
            var sortedMods = new Stack<IModMetadata>();
            var states = mods.ToDictionary(mod => mod, mod => ModDependencyStatus.Queued);

            // handle failed mods
            foreach (IModMetadata mod in mods.Where(m => m.Status == ModMetadataStatus.Failed))
            {
                states[mod] = ModDependencyStatus.Failed;
                sortedMods.Push(mod);
            }

            // sort mods
            foreach (IModMetadata mod in mods)
                this.ProcessDependencies(mods.ToArray(), modDatabase, mod, states, sortedMods, new List<IModMetadata>());

            return sortedMods.Reverse();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Sort a mod's dependencies by the order they should be loaded, and remove any mods that can't be loaded due to missing or conflicting dependencies.</summary>
        /// <param name="mods">The full list of mods being validated.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        /// <param name="mod">The mod whose dependencies to process.</param>
        /// <param name="states">The dependency state for each mod.</param>
        /// <param name="sortedMods">The list in which to save mods sorted by dependency order.</param>
        /// <param name="currentChain">The current change of mod dependencies.</param>
        /// <returns>Returns the mod dependency status.</returns>
        private ModDependencyStatus ProcessDependencies(IModMetadata[] mods, ModDatabase modDatabase, IModMetadata mod, IDictionary<IModMetadata, ModDependencyStatus> states, Stack<IModMetadata> sortedMods, ICollection<IModMetadata> currentChain)
        {
            // check if already visited
            switch (states[mod])
            {
                // already sorted or failed
                case ModDependencyStatus.Sorted:
                case ModDependencyStatus.Failed:
                    return states[mod];

                // dependency loop
                case ModDependencyStatus.Checking:
                    // This should never happen. The higher-level mod checks if the dependency is
                    // already being checked, so it can fail without visiting a mod twice. If this
                    // case is hit, that logic didn't catch the dependency loop for some reason.
                    throw new InvalidModStateException($"A dependency loop was not caught by the calling iteration ({string.Join(" => ", currentChain.Select(p => p.DisplayName))} => {mod.DisplayName})).");

                // not visited yet, start processing
                case ModDependencyStatus.Queued:
                    break;

                // sanity check
                default:
                    throw new InvalidModStateException($"Unknown dependency status '{states[mod]}'.");
            }

            // collect dependencies
            ModDependency[] dependencies = this.GetDependenciesFrom(mod.Manifest, mods).ToArray();

            // mark sorted if no dependencies
            if (!dependencies.Any())
            {
                sortedMods.Push(mod);
                return states[mod] = ModDependencyStatus.Sorted;
            }

            // mark failed if missing dependencies
            {
                string[] failedModNames = (
                    from entry in dependencies
                    where entry.IsRequired && entry.Mod == null
                    let displayName = modDatabase.GetDisplayNameFor(entry.ID) ?? entry.ID
                    let modUrl = modDatabase.GetModPageUrlFor(entry.ID)
                    orderby displayName
                    select modUrl != null
                        ? $"{displayName}: {modUrl}"
                        : displayName
                ).ToArray();
                if (failedModNames.Any())
                {
                    sortedMods.Push(mod);
                    mod.SetStatus(ModMetadataStatus.Failed, $"it requires mods which aren't installed ({string.Join(", ", failedModNames)}).");
                    return states[mod] = ModDependencyStatus.Failed;
                }
            }

            // dependency min version not met, mark failed
            {
                string[] failedLabels =
                    (
                        from entry in dependencies
                        where entry.Mod != null && entry.MinVersion != null && entry.MinVersion.IsNewerThan(entry.Mod.Manifest.Version)
                        select $"{entry.Mod.DisplayName} (needs {entry.MinVersion} or later)"
                    )
                    .ToArray();
                if (failedLabels.Any())
                {
                    sortedMods.Push(mod);
                    mod.SetStatus(ModMetadataStatus.Failed, $"it needs newer versions of some mods: {string.Join(", ", failedLabels)}.");
                    return states[mod] = ModDependencyStatus.Failed;
                }
            }

            // process dependencies
            {
                states[mod] = ModDependencyStatus.Checking;

                // recursively sort dependencies
                foreach (var dependency in dependencies)
                {
                    IModMetadata requiredMod = dependency.Mod;
                    var subchain = new List<IModMetadata>(currentChain) { mod };

                    // ignore missing optional dependency
                    if (!dependency.IsRequired && requiredMod == null)
                        continue;

                    // detect dependency loop
                    if (states[requiredMod] == ModDependencyStatus.Checking)
                    {
                        sortedMods.Push(mod);
                        mod.SetStatus(ModMetadataStatus.Failed, $"its dependencies have a circular reference: {string.Join(" => ", subchain.Select(p => p.DisplayName))} => {requiredMod.DisplayName}).");
                        return states[mod] = ModDependencyStatus.Failed;
                    }

                    // recursively process each dependency
                    var substatus = this.ProcessDependencies(mods, modDatabase, requiredMod, states, sortedMods, subchain);
                    switch (substatus)
                    {
                        // sorted successfully
                        case ModDependencyStatus.Sorted:
                            break;

                        // failed, which means this mod can't be loaded either
                        case ModDependencyStatus.Failed:
                            sortedMods.Push(mod);
                            mod.SetStatus(ModMetadataStatus.Failed, $"it needs the '{requiredMod.DisplayName}' mod, which couldn't be loaded.");
                            return states[mod] = ModDependencyStatus.Failed;

                        // unexpected status
                        case ModDependencyStatus.Queued:
                        case ModDependencyStatus.Checking:
                            throw new InvalidModStateException($"Something went wrong sorting dependencies: mod '{requiredMod.DisplayName}' unexpectedly stayed in the '{substatus}' status.");

                        // sanity check
                        default:
                            throw new InvalidModStateException($"Unknown dependency status '{states[mod]}'.");
                    }
                }

                // all requirements sorted successfully
                sortedMods.Push(mod);
                return states[mod] = ModDependencyStatus.Sorted;
            }
        }

        /// <summary>Get all mod folders in a root folder, passing through empty folders as needed.</summary>
        /// <param name="rootPath">The root folder path to search.</param>
        private IEnumerable<DirectoryInfo> GetModFolders(string rootPath)
        {
            foreach (string modRootPath in Directory.GetDirectories(rootPath))
            {
                DirectoryInfo directory = new DirectoryInfo(modRootPath);

                // if a folder only contains another folder, check the inner folder instead
                while (!directory.GetFiles().Any() && directory.GetDirectories().Length == 1)
                    directory = directory.GetDirectories().First();

                yield return directory;
            }
        }

        /// <summary>Get the dependencies declared in a manifest.</summary>
        /// <param name="manifest">The mod manifest.</param>
        /// <param name="loadedMods">The loaded mods.</param>
        private IEnumerable<ModDependency> GetDependenciesFrom(IManifest manifest, IModMetadata[] loadedMods)
        {
            IModMetadata FindMod(string id) => loadedMods.FirstOrDefault(m => string.Equals(m.Manifest?.UniqueID, id, StringComparison.InvariantCultureIgnoreCase));

            // yield dependencies
            if (manifest.Dependencies != null)
            {
                foreach (var entry in manifest.Dependencies)
                    yield return new ModDependency(entry.UniqueID, entry.MinimumVersion, FindMod(entry.UniqueID), entry.IsRequired);
            }

            // yield content pack parent
            if (manifest.ContentPackFor != null)
                yield return new ModDependency(manifest.ContentPackFor.UniqueID, manifest.ContentPackFor.MinimumVersion, FindMod(manifest.ContentPackFor.UniqueID), isRequired: true);
        }


        /*********
        ** Private models
        *********/
        /// <summary>Represents a dependency from one mod to another.</summary>
        private struct ModDependency
        {
            /*********
            ** Accessors
            *********/
            /// <summary>The unique ID of the required mod.</summary>
            public string ID { get; }

            /// <summary>The minimum required version (if any).</summary>
            public ISemanticVersion MinVersion { get; }

            /// <summary>Whether the mod shouldn't be loaded if the dependency isn't available.</summary>
            public bool IsRequired { get; }

            /// <summary>The loaded mod that fulfills the dependency (if available).</summary>
            public IModMetadata Mod { get; }


            /*********
            ** Public methods
            *********/
            /// <summary>Construct an instance.</summary>
            /// <param name="id">The unique ID of the required mod.</param>
            /// <param name="minVersion">The minimum required version (if any).</param>
            /// <param name="mod">The loaded mod that fulfills the dependency (if available).</param>
            /// <param name="isRequired">Whether the mod shouldn't be loaded if the dependency isn't available.</param>
            public ModDependency(string id, ISemanticVersion minVersion, IModMetadata mod, bool isRequired)
            {
                this.ID = id;
                this.MinVersion = minVersion;
                this.Mod = mod;
                this.IsRequired = isRequired;
            }
        }
    }
}
