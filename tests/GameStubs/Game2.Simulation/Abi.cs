using System;
using Keen.VRage.Library.Utils;

// Shapes taken from Space Engineers 2 build 2.3.0.2798.
// Hand-written, and nothing checks them against the real assemblies — a GAMEDIR build
// before a release is the only guard against drift.

namespace Keen.Game2.Simulation.Scripting
{
    /// <summary>
    /// The real one also implements <c>IScriptWhitelistProvider</c> (VRage.Scripting) and
    /// carries the <c>ConfigureWhitelist</c> that reads this field: for each anchor it
    /// takes <c>anchor.Assembly.GetExportedTypes()</c>, reduces them to one per namespace
    /// and calls <c>builder.AllowNamespace</c>. Two anchors in one assembly therefore walk
    /// the same namespaces twice, and the second pass throws.
    ///
    /// <para>The interface is dropped here because implementing it would mean stubbing
    /// <c>ScriptWhitelist.Builder</c> as well, and the plugin never names either.</para>
    /// </summary>
    public class GameWhitelistProvider<T> : Singleton<T> where T : Singleton<T>
    {
        /// <summary>
        /// Public static mutable field in the game too — commented there as
        /// "TODO SE2-9580, SE2-9581 Come up with better solution than statics". That it is
        /// writable from outside is the whole reason this plugin can fix the duplicate.
        /// </summary>
        public static Type[] AllowedAssemblies = Array.Empty<Type>();
    }

    /// <summary>Whitelist for mod scripts. Empty in the game too.</summary>
    public class ModWhitelistProvider : GameWhitelistProvider<ModWhitelistProvider>
    {
    }

    /// <summary>Whitelist for in-game (programmable block) scripts. Empty in the game too.</summary>
    public class InGameWhitelistProvider : GameWhitelistProvider<InGameWhitelistProvider>
    {
    }
}
