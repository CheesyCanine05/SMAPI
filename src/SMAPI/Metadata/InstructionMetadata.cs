using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.AssemblyRewriters;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.ModLoading.Finders;
using StardewModdingAPI.Framework.ModLoading.Rewriters;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewModdingAPI.Metadata
{
    /// <summary>Provides CIL instruction handlers which rewrite mods for compatibility and throw exceptions for incompatible code.</summary>
    internal class InstructionMetadata
    {
        /*********
        ** Properties
        *********/
        /// <summary>The assembly names to which to heuristically detect broken references.</summary>
        /// <remarks>The current implementation only works correctly with assemblies that should always be present.</remarks>
        private readonly string[] ValidateReferencesToAssemblies = { "StardewModdingAPI", "Stardew Valley", "StardewValley" };


        /*********
        ** Public methods
        *********/
        /// <summary>Get rewriters which detect or fix incompatible CIL instructions in mod assemblies.</summary>
        public IEnumerable<IInstructionHandler> GetHandlers()
        {
            return new IInstructionHandler[]
            {
                // rewrite for crossplatform compatibility
                new MethodParentRewriter(typeof(SpriteBatch), typeof(SpriteBatchMethods), onlyIfPlatformChanged: true),

                // rewrite entry method for SMAPI 2.0
                new VirtualEntryCallRemover(),

                // fix common issues from SDV 1.3
                new StaticFieldToConstantRewriter<int>(typeof(Game1), "tileSize", Game1.tileSize),
                new FieldToPropertyRewriter(typeof(Character), nameof(Character.name), nameof(Character.Name)),
                new FieldToPropertyRewriter(typeof(GameLocation), nameof(GameLocation.isFarm), nameof(GameLocation.IsFarm)),
                new FieldToPropertyRewriter(typeof(GameLocation), nameof(GameLocation.isOutdoors), nameof(GameLocation.isOutdoors)),
                new FieldToPropertyRewriter(typeof(GameLocation), nameof(GameLocation.name), nameof(GameLocation.Name)),
                new FieldToPropertyRewriter(typeof(Item), nameof(Item.category), nameof(Item.Category)),
                new FieldToPropertyRewriter(typeof(SObject), nameof(SObject.quality), nameof(SObject.Quality)),
                new FieldToPropertyRewriter(typeof(SObject), nameof(SObject.stack), nameof(SObject.Stack)),

                // detect broken field/property/method references
                new ReferenceToMissingMemberFinder(this.ValidateReferencesToAssemblies),
                new ReferenceToMemberWithUnexpectedTypeFinder(this.ValidateReferencesToAssemblies),

                // detect code which may impact game stability
                new TypeFinder("Harmony.HarmonyInstance", InstructionHandleResult.DetectedGamePatch),
                new TypeFinder("System.Runtime.CompilerServices.CallSite", InstructionHandleResult.DetectedDynamic),
                new FieldFinder(typeof(SaveGame).FullName, nameof(SaveGame.serializer), InstructionHandleResult.DetectedSaveSerialiser),
                new FieldFinder(typeof(SaveGame).FullName, nameof(SaveGame.farmerSerializer), InstructionHandleResult.DetectedSaveSerialiser),
                new FieldFinder(typeof(SaveGame).FullName, nameof(SaveGame.locationSerializer), InstructionHandleResult.DetectedSaveSerialiser),
                new EventFinder(typeof(SpecialisedEvents).FullName, nameof(SpecialisedEvents.UnvalidatedUpdateTick), InstructionHandleResult.DetectedUnvalidatedUpdateTick)
            };
        }
    }
}
