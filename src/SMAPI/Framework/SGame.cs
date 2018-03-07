using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Netcode;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Input;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;
using xTile.Dimensions;

namespace StardewModdingAPI.Framework
{
    /// <summary>SMAPI's extension of the game's core <see cref="Game1"/>, used to inject events.</summary>
    internal class SGame : Game1
    {
        /*********
        ** Properties
        *********/
        /****
        ** SMAPI state
        ****/
        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>Manages SMAPI events for mods.</summary>
        private readonly EventManager Events;

        /// <summary>The maximum number of consecutive attempts SMAPI should make to recover from a draw error.</summary>
        private readonly Countdown DrawCrashTimer = new Countdown(60); // 60 ticks = roughly one second

        /// <summary>The maximum number of consecutive attempts SMAPI should make to recover from an update error.</summary>
        private readonly Countdown UpdateCrashTimer = new Countdown(60); // 60 ticks = roughly one second

        /// <summary>The number of ticks until SMAPI should notify mods that the game has loaded.</summary>
        /// <remarks>Skipping a few frames ensures the game finishes initialising the world before mods try to change it.</remarks>
        private int AfterLoadTimer = 5;

        /// <summary>Whether the game is returning to the menu.</summary>
        private bool IsExitingToTitle;

        /// <summary>Whether the game is saving and SMAPI has already raised <see cref="SaveEvents.BeforeSave"/>.</summary>
        private bool IsBetweenSaveEvents;

        /// <summary>Whether the game is creating the save file and SMAPI has already raised <see cref="SaveEvents.BeforeCreate"/>.</summary>
        private bool IsBetweenCreateEvents;

        /****
        ** Game state
        ****/
        /// <summary>The player input as of the previous tick.</summary>
        private InputState PreviousInput = new InputState();

        /// <summary>The window size value at last check.</summary>
        private Point PreviousWindowSize;

        /// <summary>The save ID at last check.</summary>
        private ulong PreviousSaveID;

        /// <summary>A hash of <see cref="Game1.locations"/> at last check.</summary>
        private int PreviousGameLocations;

        /// <summary>A hash of the current location's <see cref="GameLocation.objects"/> at last check.</summary>
        private int PreviousLocationObjects;

        /// <summary>The player's inventory at last check.</summary>
        private IDictionary<Item, int> PreviousItems;

        /// <summary>The player's combat skill level at last check.</summary>
        private int PreviousCombatLevel;

        /// <summary>The player's farming skill level at last check.</summary>
        private int PreviousFarmingLevel;

        /// <summary>The player's fishing skill level at last check.</summary>
        private int PreviousFishingLevel;

        /// <summary>The player's foraging skill level at last check.</summary>
        private int PreviousForagingLevel;

        /// <summary>The player's mining skill level at last check.</summary>
        private int PreviousMiningLevel;

        /// <summary>The player's luck skill level at last check.</summary>
        private int PreviousLuckLevel;

        /// <summary>The player's location at last check.</summary>
        private GameLocation PreviousGameLocation;

        /// <summary>The active game menu at last check.</summary>
        private IClickableMenu PreviousActiveMenu;

        /// <summary>The mine level at last check.</summary>
        private int PreviousMineLevel;

        /// <summary>The time of day (in 24-hour military format) at last check.</summary>
        private int PreviousTime;

        /// <summary>The previous content locale.</summary>
        private LocalizedContentManager.LanguageCode? PreviousLocale;

        /// <summary>An index incremented on every tick and reset every 60th tick (0–59).</summary>
        private int CurrentUpdateTick;

        /// <summary>Whether this is the very first update tick since the game started.</summary>
        private bool FirstUpdate;

        /// <summary>The current game instance.</summary>
        private static SGame Instance;

        /// <summary>A callback to invoke after the game finishes initialising.</summary>
        private readonly Action OnGameInitialised;

        /****
        ** Private wrappers
        ****/
        /// <summary>Simplifies access to private game code.</summary>
        private static Reflector Reflection;

        // ReSharper disable ArrangeStaticMemberQualifier, ArrangeThisQualifier, InconsistentNaming
        /// <summary>Used to access private fields and methods.</summary>
        private static List<float> _fpsList => SGame.Reflection.GetField<List<float>>(typeof(Game1), nameof(_fpsList)).GetValue();
        private static Stopwatch _fpsStopwatch => SGame.Reflection.GetField<Stopwatch>(typeof(Game1), nameof(SGame._fpsStopwatch)).GetValue();
        private static float _fps
        {
            set => SGame.Reflection.GetField<float>(typeof(Game1), nameof(_fps)).SetValue(value);
        }
        private static Task _newDayTask => SGame.Reflection.GetField<Task>(typeof(Game1), nameof(_newDayTask)).GetValue();
        private Color bgColor => SGame.Reflection.GetField<Color>(this, nameof(this.bgColor)).GetValue();
        public RenderTarget2D screenWrapper => SGame.Reflection.GetProperty<RenderTarget2D>(this, "screen").GetValue(); // deliberately renamed to avoid an infinite loop
        public BlendState lightingBlend => SGame.Reflection.GetField<BlendState>(this, nameof(this.lightingBlend)).GetValue();
        private readonly Action drawFarmBuildings = () => SGame.Reflection.GetMethod(SGame.Instance, nameof(drawFarmBuildings)).Invoke();
        private readonly Action drawHUD = () => SGame.Reflection.GetMethod(SGame.Instance, nameof(drawHUD)).Invoke();
        private readonly Action drawDialogueBox = () => SGame.Reflection.GetMethod(SGame.Instance, nameof(drawDialogueBox)).Invoke();
        private readonly Action<SpriteBatch> drawOverlays = spriteBatch => SGame.Reflection.GetMethod(SGame.Instance, nameof(SGame.drawOverlays)).Invoke(spriteBatch);
        private readonly Action renderScreenBuffer = () => SGame.Reflection.GetMethod(SGame.Instance, nameof(renderScreenBuffer)).Invoke();
        // ReSharper restore ArrangeStaticMemberQualifier, ArrangeThisQualifier, InconsistentNaming


        /*********
        ** Accessors
        *********/
        /// <summary>SMAPI's content manager.</summary>
        public ContentCore ContentCore { get; }

        /// <summary>Whether SMAPI should log more information about the game context.</summary>
        public bool VerboseLogging { get; set; }


        /*********
        ** Protected methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="reflection">Simplifies access to private game code.</param>
        /// <param name="eventManager">Manages SMAPI events for mods.</param>
        /// <param name="onGameInitialised">A callback to invoke after the game finishes initialising.</param>
        internal SGame(IMonitor monitor, Reflector reflection, EventManager eventManager, Action onGameInitialised)
        {
            // initialise
            this.Monitor = monitor;
            this.Events = eventManager;
            this.FirstUpdate = true;
            SGame.Instance = this;
            SGame.Reflection = reflection;
            this.OnGameInitialised = onGameInitialised;

            // set XNA option required by Stardew Valley
            Game1.graphics.GraphicsProfile = GraphicsProfile.HiDef;

            // override content manager
            this.Monitor?.Log("Overriding content manager...", LogLevel.Trace);
            this.ContentCore = new ContentCore(this.Content.ServiceProvider, this.Content.RootDirectory, Thread.CurrentThread.CurrentUICulture, null, this.Monitor, reflection);
            this.Content = this.ContentCore.CreateContentManager("SGame.Content");
            Game1.content = this.ContentCore.CreateContentManager("Game1.content");

            // replace already-created temporary content managers
            reflection.GetField<LocalizedContentManager>(typeof(Game1), "_temporaryContent").SetValue(this.ContentCore.CreateContentManager("Game1._temporaryContent")); // regenerate value with new content manager
        }

        /****
        ** Intercepted methods & events
        ****/
        /// <summary>Constructor a content manager to read XNB files.</summary>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        protected override LocalizedContentManager CreateContentManager(IServiceProvider serviceProvider, string rootDirectory)
        {
            // return default if SMAPI's content manager isn't initialised yet
            if (this.ContentCore == null)
            {
                this.Monitor?.Log("SMAPI's content manager isn't initialised; skipping content manager interception.", LogLevel.Trace);
                return base.CreateContentManager(serviceProvider, rootDirectory);
            }

            // return single instance if valid
            if (serviceProvider != this.Content.ServiceProvider)
                throw new InvalidOperationException("SMAPI uses a single content manager internally. You can't get a new content manager with a different service provider.");
            if (rootDirectory != this.Content.RootDirectory)
                throw new InvalidOperationException($"SMAPI uses a single content manager internally. You can't get a new content manager with a different root directory (current is {this.Content.RootDirectory}, requested {rootDirectory}).");
            return this.ContentCore.CreateContentManager("(generated instance)");
        }

        /// <summary>The method called when the game is updating its state. This happens roughly 60 times per second.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        protected override void Update(GameTime gameTime)
        {
            try
            {
                /*********
                ** Skip conditions
                *********/
                // SMAPI exiting, stop processing game updates
                if (this.Monitor.IsExiting)
                {
                    this.Monitor.Log("SMAPI shutting down: aborting update.", LogLevel.Trace);
                    return;
                }

                // While a background new-day task is in progress, the game skips its own update logic
                // and defers to the XNA Update method. Running mod code in parallel to the background
                // update is risky, because data changes can conflict (e.g. collection changed during
                // enumeration errors) and data may change unexpectedly from one mod instruction to the
                // next.
                // 
                // Therefore we can just run Game1.Update here without raising any SMAPI events. There's
                // a small chance that the task will finish after we defer but before the game checks,
                // which means technically events should be raised, but the effects of missing one
                // update tick are neglible and not worth the complications of bypassing Game1.Update.
                if (SGame._newDayTask != null)
                {
                    base.Update(gameTime);
                    this.Events.Specialised_UnvalidatedUpdateTick.Raise();
                    return;
                }

                // game is asynchronously loading a save, block mod events to avoid conflicts
                if (Game1.gameMode == Game1.loadingMode)
                {
                    base.Update(gameTime);
                    this.Events.Specialised_UnvalidatedUpdateTick.Raise();
                    return;
                }

                /*********
                ** Save events + suppress events during save
                *********/
                // While the game is writing to the save file in the background, mods can unexpectedly
                // fail since they don't have exclusive access to resources (e.g. collection changed
                // during enumeration errors). To avoid problems, events are not invoked while a save
                // is in progress. It's safe to raise SaveEvents.BeforeSave as soon as the menu is
                // opened (since the save hasn't started yet), but all other events should be suppressed.
                if (Context.IsSaving)
                {
                    // raise before-create
                    if (!Context.IsWorldReady && !this.IsBetweenCreateEvents)
                    {
                        this.IsBetweenCreateEvents = true;
                        this.Monitor.Log("Context: before save creation.", LogLevel.Trace);
                        this.Events.Save_BeforeCreate.Raise();
                    }

                    // raise before-save
                    if (Context.IsWorldReady && !this.IsBetweenSaveEvents)
                    {
                        this.IsBetweenSaveEvents = true;
                        this.Monitor.Log("Context: before save.", LogLevel.Trace);
                        this.Events.Save_BeforeSave.Raise();
                    }

                    // suppress non-save events
                    base.Update(gameTime);
                    this.Events.Specialised_UnvalidatedUpdateTick.Raise();
                    return;
                }
                if (this.IsBetweenCreateEvents)
                {
                    // raise after-create
                    this.IsBetweenCreateEvents = false;
                    this.Monitor.Log($"Context: after save creation, starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                    this.Events.Save_AfterCreate.Raise();
                }
                if (this.IsBetweenSaveEvents)
                {
                    // raise after-save
                    this.IsBetweenSaveEvents = false;
                    this.Monitor.Log($"Context: after save, starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                    this.Events.Save_AfterSave.Raise();
                    this.Events.Time_AfterDayStarted.Raise();
                }

                /*********
                ** Notify SMAPI that game is initialised
                *********/
                if (this.FirstUpdate)
                    this.OnGameInitialised();

                /*********
                ** Locale changed events
                *********/
                if (this.PreviousLocale != LocalizedContentManager.CurrentLanguageCode)
                {
                    var oldValue = this.PreviousLocale;
                    var newValue = LocalizedContentManager.CurrentLanguageCode;

                    this.Monitor.Log($"Context: locale set to {newValue}.", LogLevel.Trace);

                    if (oldValue != null)
                        this.Events.Content_LocaleChanged.Raise(new EventArgsValueChanged<string>(oldValue.ToString(), newValue.ToString()));

                    this.PreviousLocale = newValue;
                }

                /*********
                ** After load events
                *********/
                if (Context.IsSaveLoaded && !SaveGame.IsProcessing /*still loading save*/ && this.AfterLoadTimer >= 0)
                {
                    if (Game1.dayOfMonth != 0) // wait until new-game intro finishes (world not fully initialised yet)
                        this.AfterLoadTimer--;

                    if (this.AfterLoadTimer == 0)
                    {
                        this.Monitor.Log($"Context: loaded saved game '{Constants.SaveFolderName}', starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                        Context.IsWorldReady = true;

                        this.Events.Save_AfterLoad.Raise();
                        this.Events.Time_AfterDayStarted.Raise();
                    }
                }

                /*********
                ** Exit to title events
                *********/
                // before exit to title
                if (Game1.exitToTitle)
                    this.IsExitingToTitle = true;

                // after exit to title
                if (Context.IsWorldReady && this.IsExitingToTitle && Game1.activeClickableMenu is TitleMenu)
                {
                    this.Monitor.Log("Context: returned to title", LogLevel.Trace);

                    this.IsExitingToTitle = false;
                    this.CleanupAfterReturnToTitle();
                    this.Events.Save_AfterReturnToTitle.Raise();
                }

                /*********
                ** Window events
                *********/
                // Here we depend on the game's viewport instead of listening to the Window.Resize
                // event because we need to notify mods after the game handles the resize, so the
                // game's metadata (like Game1.viewport) are updated. That's a bit complicated
                // since the game adds & removes its own handler on the fly.
                if (Game1.viewport.Width != this.PreviousWindowSize.X || Game1.viewport.Height != this.PreviousWindowSize.Y)
                {
                    Point size = new Point(Game1.viewport.Width, Game1.viewport.Height);
                    this.Events.Graphics_Resize.Raise();
                    this.PreviousWindowSize = size;
                }

                /*********
                ** Input events (if window has focus)
                *********/
                if (Game1.game1.IsActive)
                {
                    // get input state
                    InputState inputState;
                    try
                    {
                        inputState = InputState.GetState(this.PreviousInput);
                    }
                    catch (InvalidOperationException) // GetState() may crash for some players if window doesn't have focus but game1.IsActive == true
                    {
                        inputState = this.PreviousInput;
                    }

                    // get cursor position
                    ICursorPosition cursor;
                    {
                        // cursor position
                        Vector2 screenPixels = new Vector2(Game1.getMouseX(), Game1.getMouseY());
                        Vector2 tile = new Vector2((int)((Game1.viewport.X + screenPixels.X) / Game1.tileSize), (int)((Game1.viewport.Y + screenPixels.Y) / Game1.tileSize));
                        Vector2 grabTile = (Game1.mouseCursorTransparency > 0 && Utility.tileWithinRadiusOfPlayer((int)tile.X, (int)tile.Y, 1, Game1.player)) // derived from Game1.pressActionButton
                            ? tile
                            : Game1.player.GetGrabTile();
                        cursor = new CursorPosition(screenPixels, tile, grabTile);
                    }

                    // raise input events
                    foreach (var pair in inputState.ActiveButtons)
                    {
                        SButton button = pair.Key;
                        InputStatus status = pair.Value;

                        if (status == InputStatus.Pressed)
                        {
                            this.Events.Input_ButtonPressed.Raise(new EventArgsInput(button, cursor, button.IsActionButton(), button.IsUseToolButton()));

                            // legacy events
                            if (button.TryGetKeyboard(out Keys key))
                            {
                                if (key != Keys.None)
                                    this.Events.Control_KeyPressed.Raise(new EventArgsKeyPressed(key));
                            }
                            else if (button.TryGetController(out Buttons controllerButton))
                            {
                                if (controllerButton == Buttons.LeftTrigger || controllerButton == Buttons.RightTrigger)
                                    this.Events.Control_ControllerTriggerPressed.Raise(new EventArgsControllerTriggerPressed(PlayerIndex.One, controllerButton, controllerButton == Buttons.LeftTrigger ? inputState.ControllerState.Triggers.Left : inputState.ControllerState.Triggers.Right));
                                else
                                    this.Events.Control_ControllerButtonPressed.Raise(new EventArgsControllerButtonPressed(PlayerIndex.One, controllerButton));
                            }
                        }
                        else if (status == InputStatus.Released)
                        {
                            this.Events.Input_ButtonReleased.Raise(new EventArgsInput(button, cursor, button.IsActionButton(), button.IsUseToolButton()));

                            // legacy events
                            if (button.TryGetKeyboard(out Keys key))
                            {
                                if (key != Keys.None)
                                    this.Events.Control_KeyReleased.Raise(new EventArgsKeyPressed(key));
                            }
                            else if (button.TryGetController(out Buttons controllerButton))
                            {
                                if (controllerButton == Buttons.LeftTrigger || controllerButton == Buttons.RightTrigger)
                                    this.Events.Control_ControllerTriggerReleased.Raise(new EventArgsControllerTriggerReleased(PlayerIndex.One, controllerButton, controllerButton == Buttons.LeftTrigger ? inputState.ControllerState.Triggers.Left : inputState.ControllerState.Triggers.Right));
                                else
                                    this.Events.Control_ControllerButtonReleased.Raise(new EventArgsControllerButtonReleased(PlayerIndex.One, controllerButton));
                            }
                        }
                    }

                    // raise legacy state-changed events
                    if (inputState.KeyboardState != this.PreviousInput.KeyboardState)
                        this.Events.Control_KeyboardChanged.Raise(new EventArgsKeyboardStateChanged(this.PreviousInput.KeyboardState, inputState.KeyboardState));
                    if (inputState.MouseState != this.PreviousInput.MouseState)
                        this.Events.Control_MouseChanged.Raise(new EventArgsMouseStateChanged(this.PreviousInput.MouseState, inputState.MouseState, this.PreviousInput.MousePosition, inputState.MousePosition));

                    // track state
                    this.PreviousInput = inputState;
                }

                /*********
                ** Menu events
                *********/
                if (Game1.activeClickableMenu != this.PreviousActiveMenu)
                {
                    IClickableMenu previousMenu = this.PreviousActiveMenu;
                    IClickableMenu newMenu = Game1.activeClickableMenu;

                    // log context
                    if (this.VerboseLogging)
                    {
                        if (previousMenu == null)
                            this.Monitor.Log($"Context: opened menu {newMenu?.GetType().FullName ?? "(none)"}.", LogLevel.Trace);
                        else if (newMenu == null)
                            this.Monitor.Log($"Context: closed menu {previousMenu.GetType().FullName}.", LogLevel.Trace);
                        else
                            this.Monitor.Log($"Context: changed menu from {previousMenu.GetType().FullName} to {newMenu.GetType().FullName}.", LogLevel.Trace);
                    }

                    // raise menu events
                    if (newMenu != null)
                        this.Events.Menu_Changed.Raise(new EventArgsClickableMenuChanged(previousMenu, newMenu));
                    else
                        this.Events.Menu_Closed.Raise(new EventArgsClickableMenuClosed(previousMenu));

                    // update previous menu
                    // (if the menu was changed in one of the handlers, deliberately defer detection until the next update so mods can be notified of the new menu change)
                    this.PreviousActiveMenu = newMenu;
                }

                /*********
                ** World & player events
                *********/
                if (Context.IsWorldReady)
                {
                    // raise current location changed
                    // ReSharper disable once PossibleUnintendedReferenceComparison
                    if (Game1.currentLocation != this.PreviousGameLocation)
                    {
                        if (this.VerboseLogging)
                            this.Monitor.Log($"Context: set location to {Game1.currentLocation?.Name ?? "(none)"}.", LogLevel.Trace);
                        this.Events.Location_CurrentLocationChanged.Raise(new EventArgsCurrentLocationChanged(this.PreviousGameLocation, Game1.currentLocation));
                    }

                    // raise location list changed
                    if (this.GetHash(Game1.locations) != this.PreviousGameLocations)
                        this.Events.Location_LocationsChanged.Raise(new EventArgsGameLocationsChanged(Game1.locations));

                    // raise events that shouldn't be triggered on initial load
                    if (Game1.uniqueIDForThisGame == this.PreviousSaveID)
                    {
                        // raise player leveled up a skill
                        if (Game1.player.combatLevel != this.PreviousCombatLevel)
                            this.Events.Player_LeveledUp.Raise(new EventArgsLevelUp(EventArgsLevelUp.LevelType.Combat, Game1.player.combatLevel));
                        if (Game1.player.farmingLevel != this.PreviousFarmingLevel)
                            this.Events.Player_LeveledUp.Raise(new EventArgsLevelUp(EventArgsLevelUp.LevelType.Farming, Game1.player.farmingLevel));
                        if (Game1.player.fishingLevel != this.PreviousFishingLevel)
                            this.Events.Player_LeveledUp.Raise(new EventArgsLevelUp(EventArgsLevelUp.LevelType.Fishing, Game1.player.fishingLevel));
                        if (Game1.player.foragingLevel != this.PreviousForagingLevel)
                            this.Events.Player_LeveledUp.Raise(new EventArgsLevelUp(EventArgsLevelUp.LevelType.Foraging, Game1.player.foragingLevel));
                        if (Game1.player.miningLevel != this.PreviousMiningLevel)
                            this.Events.Player_LeveledUp.Raise(new EventArgsLevelUp(EventArgsLevelUp.LevelType.Mining, Game1.player.miningLevel));
                        if (Game1.player.luckLevel != this.PreviousLuckLevel)
                            this.Events.Player_LeveledUp.Raise(new EventArgsLevelUp(EventArgsLevelUp.LevelType.Luck, Game1.player.luckLevel));

                        // raise player inventory changed
                        ItemStackChange[] changedItems = this.GetInventoryChanges(Game1.player.items, this.PreviousItems).ToArray();
                        if (changedItems.Any())
                            this.Events.Player_InventoryChanged.Raise(new EventArgsInventoryChanged(Game1.player.items, changedItems.ToList()));

                        // raise current location's object list changed
                        if (this.GetHash(Game1.currentLocation.objects) != this.PreviousLocationObjects)
                            this.Events.Location_LocationObjectsChanged.Raise(new EventArgsLocationObjectsChanged(Game1.currentLocation.objects.FieldDict));

                        // raise time changed
                        if (Game1.timeOfDay != this.PreviousTime)
                            this.Events.Time_TimeOfDayChanged.Raise(new EventArgsIntChanged(this.PreviousTime, Game1.timeOfDay));

                        // raise mine level changed
                        if (Game1.mine != null && Game1.mine.mineLevel != this.PreviousMineLevel)
                            this.Events.Mine_LevelChanged.Raise(new EventArgsMineLevelChanged(this.PreviousMineLevel, Game1.mine.mineLevel));
                    }

                    // update state
                    this.PreviousGameLocations = this.GetHash(Game1.locations);
                    this.PreviousGameLocation = Game1.currentLocation;
                    this.PreviousCombatLevel = Game1.player.combatLevel;
                    this.PreviousFarmingLevel = Game1.player.farmingLevel;
                    this.PreviousFishingLevel = Game1.player.fishingLevel;
                    this.PreviousForagingLevel = Game1.player.foragingLevel;
                    this.PreviousMiningLevel = Game1.player.miningLevel;
                    this.PreviousLuckLevel = Game1.player.luckLevel;
                    this.PreviousItems = Game1.player.items.Where(n => n != null).ToDictionary(n => n, n => n.Stack);
                    this.PreviousLocationObjects = this.GetHash(Game1.currentLocation.objects);
                    this.PreviousTime = Game1.timeOfDay;
                    this.PreviousMineLevel = Game1.mine?.mineLevel ?? 0;
                    this.PreviousSaveID = Game1.uniqueIDForThisGame;
                }

                /*********
                ** Game update
                *********/
                try
                {
                    base.Update(gameTime);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"An error occured in the base update loop: {ex.GetLogSummary()}", LogLevel.Error);
                }

                /*********
                ** Update events
                *********/
                this.Events.Specialised_UnvalidatedUpdateTick.Raise();
                if (this.FirstUpdate)
                {
                    this.FirstUpdate = false;
                    this.Events.Game_FirstUpdateTick.Raise();
                }
                this.Events.Game_UpdateTick.Raise();
                if (this.CurrentUpdateTick % 2 == 0)
                    this.Events.Game_SecondUpdateTick.Raise();
                if (this.CurrentUpdateTick % 4 == 0)
                    this.Events.Game_FourthUpdateTick.Raise();
                if (this.CurrentUpdateTick % 8 == 0)
                    this.Events.Game_EighthUpdateTick.Raise();
                if (this.CurrentUpdateTick % 15 == 0)
                    this.Events.Game_QuarterSecondTick.Raise();
                if (this.CurrentUpdateTick % 30 == 0)
                    this.Events.Game_HalfSecondTick.Raise();
                if (this.CurrentUpdateTick % 60 == 0)
                    this.Events.Game_OneSecondTick.Raise();
                this.CurrentUpdateTick += 1;
                if (this.CurrentUpdateTick >= 60)
                    this.CurrentUpdateTick = 0;

                this.UpdateCrashTimer.Reset();
            }
            catch (Exception ex)
            {
                // log error
                this.Monitor.Log($"An error occured in the overridden update loop: {ex.GetLogSummary()}", LogLevel.Error);

                // exit if irrecoverable
                if (!this.UpdateCrashTimer.Decrement())
                    this.Monitor.ExitGameImmediately("the game crashed when updating, and SMAPI was unable to recover the game.");
            }
        }

        /// <summary>The method called to draw everything to the screen.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        protected override void Draw(GameTime gameTime)
        {
            Context.IsInDrawLoop = true;
            try
            {
                this.DrawImpl(gameTime);
                this.DrawCrashTimer.Reset();
            }
            catch (Exception ex)
            {
                // log error
                this.Monitor.Log($"An error occured in the overridden draw loop: {ex.GetLogSummary()}", LogLevel.Error);

                // exit if irrecoverable
                if (!this.DrawCrashTimer.Decrement())
                {
                    this.Monitor.ExitGameImmediately("the game crashed when drawing, and SMAPI was unable to recover the game.");
                    return;
                }

                // recover sprite batch
                try
                {
                    if (Game1.spriteBatch.IsOpen(SGame.Reflection))
                    {
                        this.Monitor.Log("Recovering sprite batch from error...", LogLevel.Trace);
                        Game1.spriteBatch.End();
                    }
                }
                catch (Exception innerEx)
                {
                    this.Monitor.Log($"Could not recover sprite batch state: {innerEx.GetLogSummary()}", LogLevel.Error);
                }
            }
            Context.IsInDrawLoop = false;
        }

        /// <summary>Replicate the game's draw logic with some changes for SMAPI.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <remarks>This implementation is identical to <see cref="Game1.Draw"/>, except for try..catch around menu draw code, private field references replaced by wrappers, and added events.</remarks>
        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "LocalVariableHidesMember", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "PossibleLossOfFraction", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantArgumentDefaultValue", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantCast", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantExplicitNullableCreation", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantTypeArgumentsOfMethod", Justification = "copied from game code as-is")]
        private void DrawImpl(GameTime gameTime)
        {
            if (Game1.debugMode)
            {
                if (SGame._fpsStopwatch.IsRunning)
                {
                    float totalSeconds = (float)SGame._fpsStopwatch.Elapsed.TotalSeconds;
                    SGame._fpsList.Add(totalSeconds);
                    while (SGame._fpsList.Count >= 120)
                        SGame._fpsList.RemoveAt(0);
                    float num = 0.0f;
                    foreach (float fps in SGame._fpsList)
                        num += fps;
                    SGame._fps = (float)(1.0 / ((double)num / (double)SGame._fpsList.Count));
                }
                SGame._fpsStopwatch.Restart();
            }
            else
            {
                if (SGame._fpsStopwatch.IsRunning)
                    SGame._fpsStopwatch.Reset();
                SGame._fps = 0.0f;
                SGame._fpsList.Clear();
            }
            if (SGame._newDayTask != null)
            {
                this.GraphicsDevice.Clear(this.bgColor);
                //base.Draw(gameTime);
            }
            else
            {
                if ((double)Game1.options.zoomLevel != 1.0)
                    this.GraphicsDevice.SetRenderTarget(this.screenWrapper);
                if (this.IsSaving)
                {
                    this.GraphicsDevice.Clear(this.bgColor);
                    IClickableMenu activeClickableMenu = Game1.activeClickableMenu;
                    if (activeClickableMenu != null)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        try
                        {
                            this.Events.Graphics_OnPreRenderGuiEvent.Raise();
                            activeClickableMenu.draw(Game1.spriteBatch);
                            this.Events.Graphics_OnPostRenderGuiEvent.Raise();
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"The {activeClickableMenu.GetType().FullName} menu crashed while drawing itself during save. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                            activeClickableMenu.exitThisMenu();
                        }
                        this.RaisePostRender();
                        Game1.spriteBatch.End();
                    }
                    //base.Draw(gameTime);
                    this.renderScreenBuffer();
                }
                else
                {
                    this.GraphicsDevice.Clear(this.bgColor);
                    if (Game1.activeClickableMenu != null && Game1.options.showMenuBackground && Game1.activeClickableMenu.showWithoutTransparencyIfOptionIsSet())
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        try
                        {
                            Game1.activeClickableMenu.drawBackground(Game1.spriteBatch);
                            this.Events.Graphics_OnPreRenderGuiEvent.Raise();
                            Game1.activeClickableMenu.draw(Game1.spriteBatch);
                            this.Events.Graphics_OnPostRenderGuiEvent.Raise();
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                            Game1.activeClickableMenu.exitThisMenu();
                        }
                        this.RaisePostRender();
                        Game1.spriteBatch.End();
                        if ((double)Game1.options.zoomLevel != 1.0)
                        {
                            this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                            this.GraphicsDevice.Clear(this.bgColor);
                            Game1.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                            Game1.spriteBatch.Draw((Texture2D)this.screenWrapper, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(this.screenWrapper.Bounds), Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                            Game1.spriteBatch.End();
                        }
                        this.drawOverlays(Game1.spriteBatch);
                    }
                    else if ((int)Game1.gameMode == 11)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3685"), new Vector2(16f, 16f), Color.HotPink);
                        Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3686"), new Vector2(16f, 32f), new Color(0, (int)byte.MaxValue, 0));
                        Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.parseText(Game1.errorMessage, Game1.dialogueFont, Game1.graphics.GraphicsDevice.Viewport.Width), new Vector2(16f, 48f), Color.White);
                        this.RaisePostRender();
                        Game1.spriteBatch.End();
                    }
                    else if (Game1.currentMinigame != null)
                    {
                        Game1.currentMinigame.draw(Game1.spriteBatch);
                        if (Game1.globalFade && !Game1.menuUp && (!Game1.nameSelectUp || Game1.messagePause))
                        {
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * ((int)Game1.gameMode == 0 ? 1f - Game1.fadeToBlackAlpha : Game1.fadeToBlackAlpha));
                            Game1.spriteBatch.End();
                        }
                        this.RaisePostRender(needsNewBatch: true);
                        if ((double)Game1.options.zoomLevel != 1.0)
                        {
                            this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                            this.GraphicsDevice.Clear(this.bgColor);
                            Game1.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                            Game1.spriteBatch.Draw((Texture2D)this.screenWrapper, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(this.screenWrapper.Bounds), Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                            Game1.spriteBatch.End();
                        }
                        this.drawOverlays(Game1.spriteBatch);
                    }
                    else if (Game1.showingEndOfNightStuff)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        if (Game1.activeClickableMenu != null)
                        {
                            try
                            {
                                this.Events.Graphics_OnPreRenderGuiEvent.Raise();
                                Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                this.Events.Graphics_OnPostRenderGuiEvent.Raise();
                            }
                            catch (Exception ex)
                            {
                                this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during end-of-night-stuff. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                Game1.activeClickableMenu.exitThisMenu();
                            }
                        }
                        this.RaisePostRender();
                        Game1.spriteBatch.End();
                        if ((double)Game1.options.zoomLevel != 1.0)
                        {
                            this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                            this.GraphicsDevice.Clear(this.bgColor);
                            Game1.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                            Game1.spriteBatch.Draw((Texture2D)this.screenWrapper, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(this.screenWrapper.Bounds), Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                            Game1.spriteBatch.End();
                        }
                        this.drawOverlays(Game1.spriteBatch);
                    }
                    else
                    {
                        int num1;
                        switch (Game1.gameMode)
                        {
                            case 3:
                                num1 = Game1.currentLocation == null ? 1 : 0;
                                break;
                            case 6:
                                num1 = 1;
                                break;
                            default:
                                num1 = 0;
                                break;
                        }
                        if (num1 != 0)
                        {
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            string str1 = "";
                            for (int index = 0; (double)index < gameTime.TotalGameTime.TotalMilliseconds % 999.0 / 333.0; ++index)
                                str1 += ".";
                            string str2 = Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3688");
                            string s = str2 + str1;
                            string str3 = str2 + "... ";
                            int widthOfString = SpriteText.getWidthOfString(str3);
                            int height = 64;
                            int x = 64;
                            int y = Game1.graphics.GraphicsDevice.Viewport.TitleSafeArea.Bottom - height;
                            SpriteText.drawString(Game1.spriteBatch, s, x, y, 999999, widthOfString, height, 1f, 0.88f, false, 0, str3, -1);
                            Game1.spriteBatch.End();
                            if ((double)Game1.options.zoomLevel != 1.0)
                            {
                                this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                                this.GraphicsDevice.Clear(this.bgColor);
                                Game1.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                                Game1.spriteBatch.Draw((Texture2D)this.screenWrapper, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(this.screenWrapper.Bounds), Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                                Game1.spriteBatch.End();
                            }
                            this.drawOverlays(Game1.spriteBatch);
                        }
                        else
                        {
                            Viewport viewport1;
                            if ((int)Game1.gameMode == 0)
                            {
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            }
                            else
                            {
                                Microsoft.Xna.Framework.Rectangle bounds;
                                if (Game1.drawLighting)
                                {
                                    this.GraphicsDevice.SetRenderTarget(Game1.lightmap);
                                    this.GraphicsDevice.Clear(Color.White * 0.0f);
                                    Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                    Game1.spriteBatch.Draw(Game1.staminaRect, Game1.lightmap.Bounds, Game1.currentLocation.Name.StartsWith("UndergroundMine") ? Game1.mine.getLightingColor(gameTime) : (Game1.ambientLight.Equals(Color.White) || Game1.isRaining && (bool)((NetFieldBase<bool, NetBool>)Game1.currentLocation.isOutdoors) ? Game1.outdoorLight : Game1.ambientLight));
                                    for (int index = 0; index < Game1.currentLightSources.Count; ++index)
                                    {
                                        if (Utility.isOnScreen((Vector2)((NetFieldBase<Vector2, NetVector2>)Game1.currentLightSources.ElementAt<LightSource>(index).position), (int)((double)(float)((NetFieldBase<float, NetFloat>)Game1.currentLightSources.ElementAt<LightSource>(index).radius) * 64.0 * 4.0)))
                                        {
                                            SpriteBatch spriteBatch = Game1.spriteBatch;
                                            Texture2D lightTexture = Game1.currentLightSources.ElementAt<LightSource>(index).lightTexture;
                                            Vector2 position = Game1.GlobalToLocal(Game1.viewport, (Vector2)((NetFieldBase<Vector2, NetVector2>)Game1.currentLightSources.ElementAt<LightSource>(index).position)) / (float)(Game1.options.lightingQuality / 2);
                                            Microsoft.Xna.Framework.Rectangle? sourceRectangle = new Microsoft.Xna.Framework.Rectangle?(Game1.currentLightSources.ElementAt<LightSource>(index).lightTexture.Bounds);
                                            Color color = (Color)((NetFieldBase<Color, NetColor>)Game1.currentLightSources.ElementAt<LightSource>(index).color);
                                            double num2 = 0.0;
                                            bounds = Game1.currentLightSources.ElementAt<LightSource>(index).lightTexture.Bounds;
                                            double x = (double)bounds.Center.X;
                                            bounds = Game1.currentLightSources.ElementAt<LightSource>(index).lightTexture.Bounds;
                                            double y = (double)bounds.Center.Y;
                                            Vector2 origin = new Vector2((float)x, (float)y);
                                            double num3 = (double)(float)((NetFieldBase<float, NetFloat>)Game1.currentLightSources.ElementAt<LightSource>(index).radius) / (double)(Game1.options.lightingQuality / 2);
                                            int num4 = 0;
                                            double num5 = 0.899999976158142;
                                            spriteBatch.Draw(lightTexture, position, sourceRectangle, color, (float)num2, origin, (float)num3, (SpriteEffects)num4, (float)num5);
                                        }
                                    }
                                    Game1.spriteBatch.End();
                                    this.GraphicsDevice.SetRenderTarget((double)Game1.options.zoomLevel == 1.0 ? (RenderTarget2D)null : this.screenWrapper);
                                }
                                if (Game1.bloomDay && Game1.bloom != null)
                                    Game1.bloom.BeginDraw();
                                this.GraphicsDevice.Clear(this.bgColor);
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                this.Events.Graphics_OnPreRenderEvent.Raise();
                                if (Game1.background != null)
                                    Game1.background.draw(Game1.spriteBatch);
                                Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                Game1.currentLocation.Map.GetLayer("Back").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                                Game1.currentLocation.drawWater(Game1.spriteBatch);
                                if (Game1.CurrentEvent == null)
                                {
                                    foreach (NPC character in Game1.currentLocation.characters)
                                    {
                                        if (!(bool)((NetFieldBase<bool, NetBool>)character.swimming) && !character.HideShadow && !character.IsInvisible && !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(character.getTileLocation()))
                                        {
                                            SpriteBatch spriteBatch = Game1.spriteBatch;
                                            Texture2D shadowTexture = Game1.shadowTexture;
                                            Vector2 local = Game1.GlobalToLocal(Game1.viewport, character.Position + new Vector2((float)(character.Sprite.SpriteWidth * 4) / 2f, (float)(character.GetBoundingBox().Height + (character.IsMonster ? 0 : 12))));
                                            Microsoft.Xna.Framework.Rectangle? sourceRectangle = new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds);
                                            Color white = Color.White;
                                            double num2 = 0.0;
                                            bounds = Game1.shadowTexture.Bounds;
                                            double x = (double)bounds.Center.X;
                                            bounds = Game1.shadowTexture.Bounds;
                                            double y = (double)bounds.Center.Y;
                                            Vector2 origin = new Vector2((float)x, (float)y);
                                            double num3 = (4.0 + (double)character.yJumpOffset / 40.0) * (double)(float)((NetFieldBase<float, NetFloat>)character.scale);
                                            int num4 = 0;
                                            double num5 = (double)Math.Max(0.0f, (float)character.getStandingY() / 10000f) - 9.99999997475243E-07;
                                            spriteBatch.Draw(shadowTexture, local, sourceRectangle, white, (float)num2, origin, (float)num3, (SpriteEffects)num4, (float)num5);
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (NPC actor in Game1.CurrentEvent.actors)
                                    {
                                        if (!(bool)((NetFieldBase<bool, NetBool>)actor.swimming) && !actor.HideShadow && !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(actor.getTileLocation()))
                                        {
                                            SpriteBatch spriteBatch = Game1.spriteBatch;
                                            Texture2D shadowTexture = Game1.shadowTexture;
                                            Vector2 local = Game1.GlobalToLocal(Game1.viewport, actor.Position + new Vector2((float)(actor.Sprite.SpriteWidth * 4) / 2f, (float)(actor.GetBoundingBox().Height + (actor.IsMonster ? 0 : (actor.Sprite.SpriteHeight <= 16 ? -4 : 12)))));
                                            Microsoft.Xna.Framework.Rectangle? sourceRectangle = new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds);
                                            Color white = Color.White;
                                            double num2 = 0.0;
                                            bounds = Game1.shadowTexture.Bounds;
                                            double x = (double)bounds.Center.X;
                                            bounds = Game1.shadowTexture.Bounds;
                                            double y = (double)bounds.Center.Y;
                                            Vector2 origin = new Vector2((float)x, (float)y);
                                            double num3 = (4.0 + (double)actor.yJumpOffset / 40.0) * (double)(float)((NetFieldBase<float, NetFloat>)actor.scale);
                                            int num4 = 0;
                                            double num5 = (double)Math.Max(0.0f, (float)actor.getStandingY() / 10000f) - 9.99999997475243E-07;
                                            spriteBatch.Draw(shadowTexture, local, sourceRectangle, white, (float)num2, origin, (float)num3, (SpriteEffects)num4, (float)num5);
                                        }
                                    }
                                }
                                Game1.currentLocation.Map.GetLayer("Buildings").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                                Game1.mapDisplayDevice.EndScene();
                                Game1.spriteBatch.End();
                                Game1.spriteBatch.Begin(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                if (Game1.CurrentEvent == null)
                                {
                                    foreach (NPC character in Game1.currentLocation.characters)
                                    {
                                        if (!(bool)((NetFieldBase<bool, NetBool>)character.swimming) && !character.HideShadow && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(character.getTileLocation()))
                                            Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, character.Position + new Vector2((float)(character.Sprite.SpriteWidth * 4) / 2f, (float)(character.GetBoundingBox().Height + (character.IsMonster ? 0 : 12)))), new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds), Color.White, 0.0f, new Vector2((float)Game1.shadowTexture.Bounds.Center.X, (float)Game1.shadowTexture.Bounds.Center.Y), (float)(4.0 + (double)character.yJumpOffset / 40.0) * (float)((NetFieldBase<float, NetFloat>)character.scale), SpriteEffects.None, Math.Max(0.0f, (float)character.getStandingY() / 10000f) - 1E-06f);
                                    }
                                }
                                else
                                {
                                    foreach (NPC actor in Game1.CurrentEvent.actors)
                                    {
                                        if (!(bool)((NetFieldBase<bool, NetBool>)actor.swimming) && !actor.HideShadow && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(actor.getTileLocation()))
                                            Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, actor.Position + new Vector2((float)(actor.Sprite.SpriteWidth * 4) / 2f, (float)(actor.GetBoundingBox().Height + (actor.IsMonster ? 0 : 12)))), new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds), Color.White, 0.0f, new Vector2((float)Game1.shadowTexture.Bounds.Center.X, (float)Game1.shadowTexture.Bounds.Center.Y), (float)(4.0 + (double)actor.yJumpOffset / 40.0) * (float)((NetFieldBase<float, NetFloat>)actor.scale), SpriteEffects.None, Math.Max(0.0f, (float)actor.getStandingY() / 10000f) - 1E-06f);
                                    }
                                }
                                if ((Game1.eventUp || Game1.killScreen) && (!Game1.killScreen && Game1.currentLocation.currentEvent != null))
                                    Game1.currentLocation.currentEvent.draw(Game1.spriteBatch);
                                if (Game1.player.currentUpgrade != null && Game1.player.currentUpgrade.daysLeftTillUpgradeDone <= 3 && Game1.currentLocation.Name.Equals("Farm"))
                                    Game1.spriteBatch.Draw(Game1.player.currentUpgrade.workerTexture, Game1.GlobalToLocal(Game1.viewport, Game1.player.currentUpgrade.positionOfCarpenter), new Microsoft.Xna.Framework.Rectangle?(Game1.player.currentUpgrade.getSourceRectangle()), Color.White, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, (float)(((double)Game1.player.currentUpgrade.positionOfCarpenter.Y + 48.0) / 10000.0));
                                Game1.currentLocation.draw(Game1.spriteBatch);
                                if (!Game1.eventUp || Game1.currentLocation.currentEvent == null || Game1.currentLocation.currentEvent.messageToScreen == null)
                                    ;
                                if (Game1.player.ActiveObject == null && ((Game1.player.UsingTool || Game1.pickingTool) && Game1.player.CurrentTool != null && (!Game1.player.CurrentTool.Name.Equals("Seeds") || Game1.pickingTool)))
                                    Game1.drawTool(Game1.player);
                                if (Game1.currentLocation.Name.Equals("Farm"))
                                    this.drawFarmBuildings();
                                if (Game1.tvStation >= 0)
                                    Game1.spriteBatch.Draw(Game1.tvStationTexture, Game1.GlobalToLocal(Game1.viewport, new Vector2(400f, 160f)), new Microsoft.Xna.Framework.Rectangle?(new Microsoft.Xna.Framework.Rectangle(Game1.tvStation * 24, 0, 24, 15)), Color.White, 0.0f, Vector2.Zero, 4f, SpriteEffects.None, 1E-08f);
                                if (Game1.panMode)
                                {
                                    Game1.spriteBatch.Draw(Game1.fadeToBlackRect, new Microsoft.Xna.Framework.Rectangle((int)Math.Floor((double)(Game1.getOldMouseX() + Game1.viewport.X) / 64.0) * 64 - Game1.viewport.X, (int)Math.Floor((double)(Game1.getOldMouseY() + Game1.viewport.Y) / 64.0) * 64 - Game1.viewport.Y, 64, 64), Color.Lime * 0.75f);
                                    foreach (Warp warp in (NetList<Warp, NetRef<Warp>>)Game1.currentLocation.warps)
                                        Game1.spriteBatch.Draw(Game1.fadeToBlackRect, new Microsoft.Xna.Framework.Rectangle(warp.X * 64 - Game1.viewport.X, warp.Y * 64 - Game1.viewport.Y, 64, 64), Color.Red * 0.75f);
                                }
                                Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                Game1.currentLocation.Map.GetLayer("Front").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                                Game1.mapDisplayDevice.EndScene();
                                Game1.currentLocation.drawAboveFrontLayer(Game1.spriteBatch);
                                Game1.spriteBatch.End();
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                if (Game1.displayFarmer && Game1.player.ActiveObject != null && ((bool)((NetFieldBase<bool, NetBool>)Game1.player.ActiveObject.bigCraftable) && this.checkBigCraftableBoundariesForFrontLayer()) && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), Game1.player.getStandingY()), Game1.viewport.Size) == null)
                                    Game1.drawPlayerHeldObject(Game1.player);
                                else if (Game1.displayFarmer && Game1.player.ActiveObject != null && (Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location((int)Game1.player.Position.X, (int)Game1.player.Position.Y - 38), Game1.viewport.Size) != null && !Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location((int)Game1.player.Position.X, (int)Game1.player.Position.Y - 38), Game1.viewport.Size).TileIndexProperties.ContainsKey("FrontAlways") || Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.GetBoundingBox().Right, (int)Game1.player.Position.Y - 38), Game1.viewport.Size) != null && !Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.GetBoundingBox().Right, (int)Game1.player.Position.Y - 38), Game1.viewport.Size).TileIndexProperties.ContainsKey("FrontAlways")))
                                    Game1.drawPlayerHeldObject(Game1.player);
                                if ((Game1.player.UsingTool || Game1.pickingTool) && Game1.player.CurrentTool != null && ((!Game1.player.CurrentTool.Name.Equals("Seeds") || Game1.pickingTool) && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), (int)Game1.player.Position.Y - 38), Game1.viewport.Size) != null) && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), Game1.player.getStandingY()), Game1.viewport.Size) == null)
                                    Game1.drawTool(Game1.player);
                                if (Game1.currentLocation.Map.GetLayer("AlwaysFront") != null)
                                {
                                    Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                    Game1.currentLocation.Map.GetLayer("AlwaysFront").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                                    Game1.mapDisplayDevice.EndScene();
                                }
                                if ((double)Game1.toolHold > 400.0 && Game1.player.CurrentTool.UpgradeLevel >= 1 && Game1.player.canReleaseTool)
                                {
                                    Color color = Color.White;
                                    switch ((int)((double)Game1.toolHold / 600.0) + 2)
                                    {
                                        case 1:
                                            color = Tool.copperColor;
                                            break;
                                        case 2:
                                            color = Tool.steelColor;
                                            break;
                                        case 3:
                                            color = Tool.goldColor;
                                            break;
                                        case 4:
                                            color = Tool.iridiumColor;
                                            break;
                                    }
                                    Game1.spriteBatch.Draw(Game1.littleEffect, new Microsoft.Xna.Framework.Rectangle((int)Game1.player.getLocalPosition(Game1.viewport).X - 2, (int)Game1.player.getLocalPosition(Game1.viewport).Y - (Game1.player.CurrentTool.Name.Equals("Watering Can") ? 0 : 64) - 2, (int)((double)Game1.toolHold % 600.0 * 0.0799999982118607) + 4, 12), Color.Black);
                                    Game1.spriteBatch.Draw(Game1.littleEffect, new Microsoft.Xna.Framework.Rectangle((int)Game1.player.getLocalPosition(Game1.viewport).X, (int)Game1.player.getLocalPosition(Game1.viewport).Y - (Game1.player.CurrentTool.Name.Equals("Watering Can") ? 0 : 64), (int)((double)Game1.toolHold % 600.0 * 0.0799999982118607), 8), color);
                                }
                                if (Game1.isDebrisWeather && Game1.currentLocation.IsOutdoors && (!(bool)((NetFieldBase<bool, NetBool>)Game1.currentLocation.ignoreDebrisWeather) && !Game1.currentLocation.Name.Equals("Desert")) && Game1.viewport.X > -10)
                                {
                                    foreach (WeatherDebris weatherDebris in Game1.debrisWeather)
                                        weatherDebris.draw(Game1.spriteBatch);
                                }
                                if (Game1.farmEvent != null)
                                    Game1.farmEvent.draw(Game1.spriteBatch);
                                if ((double)Game1.currentLocation.LightLevel > 0.0 && Game1.timeOfDay < 2000)
                                    Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * Game1.currentLocation.LightLevel);
                                if (Game1.screenGlow)
                                    Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Game1.screenGlowColor * Game1.screenGlowAlpha);
                                Game1.currentLocation.drawAboveAlwaysFrontLayer(Game1.spriteBatch);
                                if (Game1.player.CurrentTool != null && Game1.player.CurrentTool is FishingRod && ((Game1.player.CurrentTool as FishingRod).isTimingCast || (double)(Game1.player.CurrentTool as FishingRod).castingChosenCountdown > 0.0 || (Game1.player.CurrentTool as FishingRod).fishCaught || (Game1.player.CurrentTool as FishingRod).showingTreasure))
                                    Game1.player.CurrentTool.draw(Game1.spriteBatch);
                                if (Game1.isRaining && Game1.currentLocation.IsOutdoors && (!Game1.currentLocation.Name.Equals("Desert") && !(Game1.currentLocation is Summit)) && (!Game1.eventUp || Game1.currentLocation.isTileOnMap(new Vector2((float)(Game1.viewport.X / 64), (float)(Game1.viewport.Y / 64)))))
                                {
                                    for (int index = 0; index < Game1.rainDrops.Length; ++index)
                                        Game1.spriteBatch.Draw(Game1.rainTexture, Game1.rainDrops[index].position, new Microsoft.Xna.Framework.Rectangle?(Game1.getSourceRectForStandardTileSheet(Game1.rainTexture, Game1.rainDrops[index].frame, -1, -1)), Color.White);
                                }
                                Game1.spriteBatch.End();
                                //base.Draw(gameTime);
                                Game1.spriteBatch.Begin(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                if (Game1.eventUp && Game1.currentLocation.currentEvent != null)
                                {
                                    foreach (NPC actor in Game1.currentLocation.currentEvent.actors)
                                    {
                                        if (actor.isEmoting)
                                        {
                                            Vector2 localPosition = actor.getLocalPosition(Game1.viewport);
                                            localPosition.Y -= 140f;
                                            if (actor.Age == 2)
                                                localPosition.Y += 32f;
                                            else if (actor.Gender == 1)
                                                localPosition.Y += 10f;
                                            Game1.spriteBatch.Draw(Game1.emoteSpriteSheet, localPosition, new Microsoft.Xna.Framework.Rectangle?(new Microsoft.Xna.Framework.Rectangle(actor.CurrentEmoteIndex * 16 % Game1.emoteSpriteSheet.Width, actor.CurrentEmoteIndex * 16 / Game1.emoteSpriteSheet.Width * 16, 16, 16)), Color.White, 0.0f, Vector2.Zero, 4f, SpriteEffects.None, (float)actor.getStandingY() / 10000f);
                                        }
                                    }
                                }
                                Game1.spriteBatch.End();
                                if (Game1.drawLighting)
                                {
                                    Game1.spriteBatch.Begin(SpriteSortMode.Deferred, this.lightingBlend, SamplerState.LinearClamp, (DepthStencilState)null, (RasterizerState)null);
                                    Game1.spriteBatch.Draw((Texture2D)Game1.lightmap, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(Game1.lightmap.Bounds), Color.White, 0.0f, Vector2.Zero, (float)(Game1.options.lightingQuality / 2), SpriteEffects.None, 1f);
                                    if (Game1.isRaining && (bool)((NetFieldBase<bool, NetBool>)Game1.currentLocation.isOutdoors) && !(Game1.currentLocation is Desert))
                                        Game1.spriteBatch.Draw(Game1.staminaRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.OrangeRed * 0.45f);
                                    Game1.spriteBatch.End();
                                }
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                if (Game1.drawGrid)
                                {
                                    int num2 = -Game1.viewport.X % 64;
                                    float num3 = (float)(-Game1.viewport.Y % 64);
                                    int num4 = num2;
                                    while (true)
                                    {
                                        int num5 = num4;
                                        viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                        int width1 = viewport1.Width;
                                        if (num5 < width1)
                                        {
                                            SpriteBatch spriteBatch = Game1.spriteBatch;
                                            Texture2D staminaRect = Game1.staminaRect;
                                            int x = num4;
                                            int y = (int)num3;
                                            int width2 = 1;
                                            viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                            int height = viewport1.Height;
                                            Microsoft.Xna.Framework.Rectangle destinationRectangle = new Microsoft.Xna.Framework.Rectangle(x, y, width2, height);
                                            Color color = Color.Red * 0.5f;
                                            spriteBatch.Draw(staminaRect, destinationRectangle, color);
                                            num4 += 64;
                                        }
                                        else
                                            break;
                                    }
                                    float num6 = num3;
                                    while (true)
                                    {
                                        double num5 = (double)num6;
                                        viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                        double height1 = (double)viewport1.Height;
                                        if (num5 < height1)
                                        {
                                            SpriteBatch spriteBatch = Game1.spriteBatch;
                                            Texture2D staminaRect = Game1.staminaRect;
                                            int x = num2;
                                            int y = (int)num6;
                                            viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                            int width = viewport1.Width;
                                            int height2 = 1;
                                            Microsoft.Xna.Framework.Rectangle destinationRectangle = new Microsoft.Xna.Framework.Rectangle(x, y, width, height2);
                                            Color color = Color.Red * 0.5f;
                                            spriteBatch.Draw(staminaRect, destinationRectangle, color);
                                            num6 += 64f;
                                        }
                                        else
                                            break;
                                    }
                                }
                                if ((uint)Game1.currentBillboard > 0U)
                                    this.drawBillboard();
                                if ((Game1.displayHUD || Game1.eventUp) && (Game1.currentBillboard == 0 && (int)Game1.gameMode == 3) && (!Game1.freezeControls && !Game1.panMode) && !Game1.HostPaused)
                                {
                                    this.Events.Graphics_OnPreRenderHudEvent.Raise();
                                    this.drawHUD();
                                    this.Events.Graphics_OnPostRenderHudEvent.Raise();
                                }
                                else if (Game1.activeClickableMenu == null && Game1.farmEvent == null)
                                    Game1.spriteBatch.Draw(Game1.mouseCursors, new Vector2((float)Game1.getOldMouseX(), (float)Game1.getOldMouseY()), new Microsoft.Xna.Framework.Rectangle?(Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16)), Color.White, 0.0f, Vector2.Zero, (float)(4.0 + (double)Game1.dialogueButtonScale / 150.0), SpriteEffects.None, 1f);
                                if (Game1.hudMessages.Count > 0 && (!Game1.eventUp || Game1.isFestival()))
                                {
                                    for (int i = Game1.hudMessages.Count - 1; i >= 0; --i)
                                        Game1.hudMessages[i].draw(Game1.spriteBatch, i);
                                }
                            }
                            if (Game1.farmEvent != null)
                                Game1.farmEvent.draw(Game1.spriteBatch);
                            if (Game1.dialogueUp && !Game1.nameSelectUp && !Game1.messagePause && (Game1.activeClickableMenu == null || !(Game1.activeClickableMenu is DialogueBox)))
                                this.drawDialogueBox();
                            if (Game1.progressBar)
                            {
                                SpriteBatch spriteBatch1 = Game1.spriteBatch;
                                Texture2D fadeToBlackRect = Game1.fadeToBlackRect;
                                viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                int x1 = (viewport1.TitleSafeArea.Width - Game1.dialogueWidth) / 2;
                                viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                Microsoft.Xna.Framework.Rectangle titleSafeArea = viewport1.TitleSafeArea;
                                int y1 = titleSafeArea.Bottom - 128;
                                int dialogueWidth = Game1.dialogueWidth;
                                int height1 = 32;
                                Microsoft.Xna.Framework.Rectangle destinationRectangle1 = new Microsoft.Xna.Framework.Rectangle(x1, y1, dialogueWidth, height1);
                                Color lightGray = Color.LightGray;
                                spriteBatch1.Draw(fadeToBlackRect, destinationRectangle1, lightGray);
                                SpriteBatch spriteBatch2 = Game1.spriteBatch;
                                Texture2D staminaRect = Game1.staminaRect;
                                viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                int x2 = (viewport1.TitleSafeArea.Width - Game1.dialogueWidth) / 2;
                                viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                titleSafeArea = viewport1.TitleSafeArea;
                                int y2 = titleSafeArea.Bottom - 128;
                                int width = (int)((double)Game1.pauseAccumulator / (double)Game1.pauseTime * (double)Game1.dialogueWidth);
                                int height2 = 32;
                                Microsoft.Xna.Framework.Rectangle destinationRectangle2 = new Microsoft.Xna.Framework.Rectangle(x2, y2, width, height2);
                                Color dimGray = Color.DimGray;
                                spriteBatch2.Draw(staminaRect, destinationRectangle2, dimGray);
                            }
                            if (Game1.eventUp && (Game1.currentLocation != null && Game1.currentLocation.currentEvent != null))
                                Game1.currentLocation.currentEvent.drawAfterMap(Game1.spriteBatch);
                            if (Game1.isRaining && (Game1.currentLocation != null && (bool)((NetFieldBase<bool, NetBool>)Game1.currentLocation.isOutdoors) && !(Game1.currentLocation is Desert)))
                            {
                                SpriteBatch spriteBatch = Game1.spriteBatch;
                                Texture2D staminaRect = Game1.staminaRect;
                                viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                Microsoft.Xna.Framework.Rectangle bounds = viewport1.Bounds;
                                Color color = Color.Blue * 0.2f;
                                spriteBatch.Draw(staminaRect, bounds, color);
                            }
                            if ((Game1.fadeToBlack || Game1.globalFade) && !Game1.menuUp && (!Game1.nameSelectUp || Game1.messagePause))
                            {
                                SpriteBatch spriteBatch = Game1.spriteBatch;
                                Texture2D fadeToBlackRect = Game1.fadeToBlackRect;
                                viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                Microsoft.Xna.Framework.Rectangle bounds = viewport1.Bounds;
                                Color color = Color.Black * ((int)Game1.gameMode == 0 ? 1f - Game1.fadeToBlackAlpha : Game1.fadeToBlackAlpha);
                                spriteBatch.Draw(fadeToBlackRect, bounds, color);
                            }
                            else if ((double)Game1.flashAlpha > 0.0)
                            {
                                if (Game1.options.screenFlash)
                                {
                                    SpriteBatch spriteBatch = Game1.spriteBatch;
                                    Texture2D fadeToBlackRect = Game1.fadeToBlackRect;
                                    viewport1 = Game1.graphics.GraphicsDevice.Viewport;
                                    Microsoft.Xna.Framework.Rectangle bounds = viewport1.Bounds;
                                    Color color = Color.White * Math.Min(1f, Game1.flashAlpha);
                                    spriteBatch.Draw(fadeToBlackRect, bounds, color);
                                }
                                Game1.flashAlpha -= 0.1f;
                            }
                            if ((Game1.messagePause || Game1.globalFade) && Game1.dialogueUp)
                                this.drawDialogueBox();
                            foreach (TemporaryAnimatedSprite overlayTempSprite in Game1.screenOverlayTempSprites)
                                overlayTempSprite.draw(Game1.spriteBatch, true, 0, 0, 1f);
                            if (Game1.debugMode)
                            {
                                SpriteBatch spriteBatch = Game1.spriteBatch;
                                SpriteFont smallFont = Game1.smallFont;
                                object[] objArray = new object[10];
                                int index = 0;
                                string str;
                                if (!Game1.panMode)
                                    str = "player: " + (object)(Game1.player.getStandingX() / 64) + ", " + (object)(Game1.player.getStandingY() / 64);
                                else
                                    str = ((Game1.getOldMouseX() + Game1.viewport.X) / 64).ToString() + "," + (object)((Game1.getOldMouseY() + Game1.viewport.Y) / 64);
                                objArray[index] = (object)str;
                                objArray[1] = (object)" mouseTransparency: ";
                                objArray[2] = (object)Game1.mouseCursorTransparency;
                                objArray[3] = (object)" mousePosition: ";
                                objArray[4] = (object)Game1.getMouseX();
                                objArray[5] = (object)",";
                                objArray[6] = (object)Game1.getMouseY();
                                objArray[7] = (object)Environment.NewLine;
                                objArray[8] = (object)"debugOutput: ";
                                objArray[9] = (object)Game1.debugOutput;
                                string text = string.Concat(objArray);
                                Viewport viewport2 = this.GraphicsDevice.Viewport;
                                double x = (double)viewport2.TitleSafeArea.X;
                                viewport2 = this.GraphicsDevice.Viewport;
                                double y = (double)viewport2.TitleSafeArea.Y;
                                Vector2 position = new Vector2((float)x, (float)y);
                                Color red = Color.Red;
                                double num2 = 0.0;
                                Vector2 zero = Vector2.Zero;
                                double num3 = 1.0;
                                int num4 = 0;
                                double num5 = 0.99999988079071;
                                spriteBatch.DrawString(smallFont, text, position, red, (float)num2, zero, (float)num3, (SpriteEffects)num4, (float)num5);
                            }
                            if (Game1.showKeyHelp)
                                Game1.spriteBatch.DrawString(Game1.smallFont, Game1.keyHelpString, new Vector2(64f, (float)(Game1.viewport.Height - 64 - (Game1.dialogueUp ? 192 + (Game1.isQuestion ? Game1.questionChoices.Count * 64 : 0) : 0)) - Game1.smallFont.MeasureString(Game1.keyHelpString).Y), Color.LightGray, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9999999f);
                            if (Game1.activeClickableMenu != null)
                            {
                                try
                                {
                                    this.Events.Graphics_OnPreRenderGuiEvent.Raise();
                                    Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                    this.Events.Graphics_OnPostRenderGuiEvent.Raise();
                                }
                                catch (Exception ex)
                                {
                                    this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                    Game1.activeClickableMenu.exitThisMenu();
                                }
                            }
                            else if (Game1.farmEvent != null)
                                Game1.farmEvent.drawAboveEverything(Game1.spriteBatch);
                            if (Game1.HostPaused)
                            {
                                string s = Game1.content.LoadString("Strings\\StringsFromCSFiles:DayTimeMoneyBox.cs.10378");
                                SpriteText.drawStringWithScrollBackground(Game1.spriteBatch, s, 96, 32, "", 1f, -1);
                            }
                            this.RaisePostRender();
                            Game1.spriteBatch.End();
                            this.drawOverlays(Game1.spriteBatch);
                            this.renderScreenBuffer();
                        }
                    }
                }
            }
        }

        /****
        ** Methods
        ****/
        /// <summary>Perform any cleanup needed when the player unloads a save and returns to the title screen.</summary>
        private void CleanupAfterReturnToTitle()
        {
            Context.IsWorldReady = false;
            this.AfterLoadTimer = 5;
            this.PreviousSaveID = 0;
        }



        /// <summary>Get the player inventory changes between two states.</summary>
        /// <param name="current">The player's current inventory.</param>
        /// <param name="previous">The player's previous inventory.</param>
        private IEnumerable<ItemStackChange> GetInventoryChanges(IEnumerable<Item> current, IDictionary<Item, int> previous)
        {
            current = current.Where(n => n != null).ToArray();
            foreach (Item item in current)
            {
                // stack size changed
                if (previous != null && previous.ContainsKey(item))
                {
                    if (previous[item] != item.Stack)
                        yield return new ItemStackChange { Item = item, StackChange = item.Stack - previous[item], ChangeType = ChangeType.StackChange };
                }

                // new item
                else
                    yield return new ItemStackChange { Item = item, StackChange = item.Stack, ChangeType = ChangeType.Added };
            }

            // removed items
            if (previous != null)
            {
                foreach (var entry in previous)
                {
                    if (current.Any(i => i == entry.Key))
                        continue;

                    yield return new ItemStackChange { Item = entry.Key, StackChange = -entry.Key.Stack, ChangeType = ChangeType.Removed };
                }
            }
        }

        /// <summary>Get a hash value for an enumeration.</summary>
        /// <param name="enumerable">The enumeration of items to hash.</param>
        private int GetHash(IEnumerable enumerable)
        {
            int hash = 0;
            foreach (object v in enumerable)
                hash ^= v.GetHashCode();
            return hash;
        }

        /// <summary>Raise the <see cref="GraphicsEvents.OnPostRenderEvent"/> if there are any listeners.</summary>
        /// <param name="needsNewBatch">Whether to create a new sprite batch.</param>
        private void RaisePostRender(bool needsNewBatch = false)
        {
            if (this.Events.Graphics_OnPostRenderEvent.HasListeners())
            {
                if (needsNewBatch)
                    Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
                this.Events.Graphics_OnPostRenderEvent.Raise();
                if (needsNewBatch)
                    Game1.spriteBatch.End();
            }
        }
    }
}
