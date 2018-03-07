using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Reflection;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Projectiles;
using StardewValley.TerrainFeatures;

namespace StardewModdingAPI.Metadata
{
    /// <summary>Provides metadata about core assets in the game.</summary>
    internal class CoreAssets
    {
        /*********
        ** Properties
        *********/
        /// <summary>Normalises an asset key to match the cache key.</summary>
        protected readonly Func<string, string> GetNormalisedPath;

        /// <summary>Setters which update static or singleton texture fields indexed by normalised asset key.</summary>
        private readonly IDictionary<string, Action<LocalizedContentManager, string>> SingletonSetters;


        /*********
        ** Public methods
        *********/
        /// <summary>Initialise the core asset data.</summary>
        /// <param name="getNormalisedPath">Normalises an asset key to match the cache key.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        public CoreAssets(Func<string, string> getNormalisedPath, Reflector reflection)
        {
            this.GetNormalisedPath = getNormalisedPath;
            this.SingletonSetters =
                new Dictionary<string, Action<LocalizedContentManager, string>>
                {
                    // from CraftingRecipe.InitShared
                    ["Data\\CraftingRecipes"] = (content, key) => CraftingRecipe.craftingRecipes = content.Load<Dictionary<string, string>>(key),
                    ["Data\\CookingRecipes"] = (content, key) => CraftingRecipe.cookingRecipes = content.Load<Dictionary<string, string>>(key),

                    // from Game1.loadContent
                    ["LooseSprites\\daybg"] = (content, key) => Game1.daybg = content.Load<Texture2D>(key),
                    ["LooseSprites\\nightbg"] = (content, key) => Game1.nightbg = content.Load<Texture2D>(key),
                    ["Maps\\MenuTiles"] = (content, key) => Game1.menuTexture = content.Load<Texture2D>(key),
                    ["LooseSprites\\Lighting\\lantern"] = (content, key) => Game1.lantern = content.Load<Texture2D>(key),
                    ["LooseSprites\\Lighting\\windowLight"] = (content, key) => Game1.windowLight = content.Load<Texture2D>(key),
                    ["LooseSprites\\Lighting\\sconceLight"] = (content, key) => Game1.sconceLight = content.Load<Texture2D>(key),
                    ["LooseSprites\\Lighting\\greenLight"] = (content, key) => Game1.cauldronLight = content.Load<Texture2D>(key),
                    ["LooseSprites\\Lighting\\indoorWindowLight"] = (content, key) => Game1.indoorWindowLight = content.Load<Texture2D>(key),
                    ["LooseSprites\\shadow"] = (content, key) => Game1.shadowTexture = content.Load<Texture2D>(key),
                    ["LooseSprites\\Cursors"] = (content, key) => Game1.mouseCursors = content.Load<Texture2D>(key),
                    ["LooseSprites\\ControllerMaps"] = (content, key) => Game1.controllerMaps = content.Load<Texture2D>(key),
                    ["TileSheets\\animations"] = (content, key) => Game1.animations = content.Load<Texture2D>(key),
                    ["Data\\Achievements"] = (content, key) => Game1.achievements = content.Load<Dictionary<int, string>>(key),
                    ["Data\\NPCGiftTastes"] = (content, key) => Game1.NPCGiftTastes = content.Load<Dictionary<string, string>>(key),
                    ["Fonts\\SpriteFont1"] = (content, key) => Game1.dialogueFont = content.Load<SpriteFont>(key),
                    ["Fonts\\SmallFont"] = (content, key) => Game1.smallFont = content.Load<SpriteFont>(key),
                    ["Fonts\\tinyFont"] = (content, key) => Game1.tinyFont = content.Load<SpriteFont>(key),
                    ["Fonts\\tinyFontBorder"] = (content, key) => Game1.tinyFontBorder = content.Load<SpriteFont>(key),
                    ["Maps\\springobjects"] = (content, key) => Game1.objectSpriteSheet = content.Load<Texture2D>(key),
                    ["TileSheets\\crops"] = (content, key) => Game1.cropSpriteSheet = content.Load<Texture2D>(key),
                    ["TileSheets\\emotes"] = (content, key) => Game1.emoteSpriteSheet = content.Load<Texture2D>(key),
                    ["TileSheets\\debris"] = (content, key) => Game1.debrisSpriteSheet = content.Load<Texture2D>(key),
                    ["TileSheets\\Craftables"] = (content, key) => Game1.bigCraftableSpriteSheet = content.Load<Texture2D>(key),
                    ["TileSheets\\rain"] = (content, key) => Game1.rainTexture = content.Load<Texture2D>(key),
                    ["TileSheets\\BuffsIcons"] = (content, key) => Game1.buffsIcons = content.Load<Texture2D>(key),
                    ["Data\\ObjectInformation"] = (content, key) => Game1.objectInformation = content.Load<Dictionary<int, string>>(key),
                    ["Data\\BigCraftablesInformation"] = (content, key) => Game1.bigCraftablesInformation = content.Load<Dictionary<int, string>>(key),
                    ["Characters\\Farmer\\hairstyles"] = (content, key) => FarmerRenderer.hairStylesTexture = content.Load<Texture2D>(key),
                    ["Characters\\Farmer\\shirts"] = (content, key) => FarmerRenderer.shirtsTexture = content.Load<Texture2D>(key),
                    ["Characters\\Farmer\\hats"] = (content, key) => FarmerRenderer.hatsTexture = content.Load<Texture2D>(key),
                    ["Characters\\Farmer\\accessories"] = (content, key) => FarmerRenderer.accessoriesTexture = content.Load<Texture2D>(key),
                    ["TileSheets\\furniture"] = (content, key) => Furniture.furnitureTexture = content.Load<Texture2D>(key),
                    ["LooseSprites\\font_bold"] = (content, key) => SpriteText.spriteTexture = content.Load<Texture2D>(key),
                    ["LooseSprites\\font_colored"] = (content, key) => SpriteText.coloredTexture = content.Load<Texture2D>(key),
                    ["TileSheets\\weapons"] = (content, key) => Tool.weaponsTexture = content.Load<Texture2D>(key),
                    ["TileSheets\\Projectiles"] = (content, key) => Projectile.projectileSheet = content.Load<Texture2D>(key),

                    // from Game1.ResetToolSpriteSheet
                    ["TileSheets\\tools"] = (content, key) => Game1.ResetToolSpriteSheet(),

                    // from Bush
                    ["TileSheets\\bushes"] = (content, key) => reflection.GetField<Lazy<Texture2D>>(typeof(Bush), "texture").SetValue(new Lazy<Texture2D>(() => content.Load<Texture2D>(key))),

                    // from Farm
                    ["Buildings\\houses"] = (content, key) => reflection.GetField<Texture2D>(typeof(Farm), nameof(Farm.houseTextures)).SetValue(content.Load<Texture2D>(key)),

                    // from Farmer
                    ["Characters\\Farmer\\farmer_base"] = (content, key) =>
                    {
                        if (Game1.player != null && Game1.player.isMale)
                            Game1.player.FarmerRenderer = new FarmerRenderer(key);
                    },
                    ["Characters\\Farmer\\farmer_girl_base"] = (content, key) =>
                    {
                        if (Game1.player != null && !Game1.player.isMale)
                            Game1.player.FarmerRenderer = new FarmerRenderer(key);
                    },

                    // from Flooring
                    ["TerrainFeatures\\Flooring"] = (content, key) => Flooring.floorsTexture = content.Load<Texture2D>(key),

                    // from FruitTree
                    ["TileSheets\\fruitTrees"] = (content, key) => FruitTree.texture = content.Load<Texture2D>(key),

                    // from HoeDirt
                    ["TerrainFeatures\\hoeDirt"] = (content, key) => HoeDirt.lightTexture = content.Load<Texture2D>(key),
                    ["TerrainFeatures\\hoeDirtDark"] = (content, key) => HoeDirt.darkTexture = content.Load<Texture2D>(key),
                    ["TerrainFeatures\\hoeDirtSnow"] = (content, key) => HoeDirt.snowTexture = content.Load<Texture2D>(key),

                    // from Wallpaper
                    ["Maps\\walls_and_floors"] = (content, key) => Wallpaper.wallpaperTexture = content.Load<Texture2D>(key)
                }
                .ToDictionary(p => getNormalisedPath(p.Key), p => p.Value);
        }

        /// <summary>Reload one of the game's core assets (if applicable).</summary>
        /// <param name="content">The content manager through which to reload the asset.</param>
        /// <param name="key">The asset key to reload.</param>
        /// <returns>Returns whether an asset was reloaded.</returns>
        public bool ReloadForKey(LocalizedContentManager content, string key)
        {
            // static assets
            if (this.SingletonSetters.TryGetValue(key, out Action<LocalizedContentManager, string> reload))
            {
                reload(content, key);
                return true;
            }

            // building textures
            if (key.StartsWith(this.GetNormalisedPath("Buildings\\")))
            {
                Building[] buildings = this.GetAllBuildings().Where(p => key == this.GetNormalisedPath($"Buildings\\{p.buildingType}")).ToArray();
                if (buildings.Any())
                {
                    foreach (Building building in buildings)
                        building.texture = new Lazy<Texture2D>(() => content.Load<Texture2D>(key));
                    return true;
                }
                return false;
            }

            return false;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get all player-constructed buildings in the world.</summary>
        private IEnumerable<Building> GetAllBuildings()
        {
            foreach (BuildableGameLocation location in Game1.locations.OfType<BuildableGameLocation>())
            {
                foreach (Building building in location.buildings)
                    yield return building;
            }
        }
    }
}
