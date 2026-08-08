using System.Collections.Generic;

// Shapes taken from Space Engineers 2 build 2.3.0.2798.
// Hand-written, and nothing checks them against the real assemblies — a GAMEDIR build
// before a release is the only guard against drift.

namespace Keen.VRage.Library.Diagnostics
{
    /// <summary>
    /// The game log. <c>Default</c> is a public static property with a public setter
    /// on the real type too, which is what lets a test install a capturing instance.
    ///
    /// <para>It starts null on purpose: that is the state the plugin's constructor
    /// actually runs in, and Log.Flush's buffer-and-retry only gets exercised if the
    /// stub reproduces it.</para>
    /// </summary>
    public class Log
    {
        public static Log Default { get; set; }

        public readonly List<string> Lines = new List<string>();

        public void WriteLine(string msg)
        {
            Lines.Add(msg);
        }
    }
}

namespace Keen.VRage.Library.Utils
{
    /// <summary>
    /// The real one adds <c>public static T Instance => SingletonManager.Get&lt;T&gt;()</c>
    /// and an <c>[AutoInstantiate]</c> attribute. Neither is reachable from the plugin,
    /// which names this type for one reason only: it is the constraint on
    /// <c>GameWhitelistProvider&lt;T&gt;</c>, so <c>TryDedupeAnchors</c> has to repeat it
    /// to pass a type argument through.
    /// </summary>
    public abstract class Singleton<T> where T : Singleton<T>
    {
    }
}
