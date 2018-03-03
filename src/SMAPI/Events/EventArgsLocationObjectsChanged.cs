using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Netcode;
using Object = StardewValley.Object;

namespace StardewModdingAPI.Events
{
    /// <summary>Event arguments for a <see cref="LocationEvents.LocationObjectsChanged"/> event.</summary>
    public class EventArgsLocationObjectsChanged : EventArgs
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The current list of objects in the current location.</summary>
        public IDictionary<Vector2, NetRef<Object>> NewObjects { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="newObjects">The current list of objects in the current location.</param>
        public EventArgsLocationObjectsChanged(IDictionary<Vector2, NetRef<Object>> newObjects)
        {
            this.NewObjects = newObjects;
        }
    }
}
