using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.Scripting;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Project;
using Keen.VRage.DCS.Builders;

// Shapes taken from Space Engineers 2 build 2.3.0.2798.
// Hand-written, and nothing checks them against the real assemblies — a GAMEDIR build
// before a release is the only guard against drift.

namespace Keen.Game2
{
    /// <summary>
    /// The game's application class. Two differences from the real one, both forced:
    ///
    /// <para>It is <c>public</c>, and so is <c>AddScripting</c>. In the game they are
    /// <c>internal class GameApp : VRageCore</c> and <c>private static void AddScripting</c>,
    /// and the plugin reaches them because Pulsar rewrites SpaceEngineers2 with everything
    /// public before Roslyn sees it — triggered by the <c>IgnoresAccessChecksTo</c> in
    /// AssemblyInfo.cs. A stub cannot reproduce that pass, so it declares the end state
    /// instead. The cost is that deleting that attribute would still build here; the test
    /// that notices is <c>The_publicizer_attribute_pulsar_scans_for_is_present</c>.</para>
    ///
    /// <para><c>AddScripting</c>'s body is not the real one, but it does the two things the
    /// plugin depends on it doing, so the end-to-end tests are about behaviour rather than
    /// a mock agreeing with itself.</para>
    /// </summary>
    public class GameApp
    {
        /// <summary>
        /// A second anchor type in this assembly. Stands in for the pair the game really
        /// sets — <c>InputGameComponent</c> and <c>BlockRenderComponent</c>, both in
        /// Game2.Client — which is the duplicate the plugin exists to remove.
        /// </summary>
        public class ScriptingAnchor
        {
        }

        /// <summary>Stands in for GameCodeProvider and GameScriptCodeProvider.</summary>
        private class StubCodeProvider : IProjectCodeProvider
        {
        }

        /// <summary>
        /// GameApp.cs:427. Called from the <c>-loadScripts</c> branch at :320, and the
        /// plugin calls it for the same effect when that branch did not run.
        ///
        /// <para>Faithful in the two ways that matter. It adds four code providers under
        /// four fixed <c>ProjectType</c> keys with <c>Dictionary.Add</c>, so a second call
        /// throws <c>ArgumentException</c> — that is what the plugin's empty-dictionary
        /// precondition is guarding against. And it sets both whitelists to an anchor list
        /// containing two types from one assembly, which is the duplicate that aborts world
        /// load.</para>
        ///
        /// <para>Omitted, because nothing here observes them: the engine component add for
        /// LoadedScriptsProviderComponent, and GameCompilationDescriptor.MetaDatas.</para>
        /// </summary>
        public static void AddScripting(EngineBuilder engine)
        {
            ProjectManagerEngineComponent.ObjectBuilder objectBuilder =
                OB<ProjectManagerEngineComponent.ObjectBuilder>(engine.EntityBuilder);

            StubCodeProvider gameCode = new StubCodeProvider();
            objectBuilder.CodeProviders.Add(ProjectType.Unknown, gameCode);
            objectBuilder.CodeProviders.Add(ProjectType.Vanilla, gameCode);

            StubCodeProvider scriptCode = new StubCodeProvider();
            objectBuilder.CodeProviders.Add(ProjectType.Mod, scriptCode);
            objectBuilder.CodeProviders.Add(ProjectType.DLC, scriptCode);

            GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies = Anchors();
            GameWhitelistProvider<InGameWhitelistProvider>.AllowedAssemblies = Anchors();
        }

        /// <summary>
        /// The real list is ten types across nine assemblies, written inline twice inside
        /// AddScripting. This is six across five, with the same defect in the same place:
        /// the last two share an assembly, and the one kept is the earlier of them.
        ///
        /// <para>Public, unlike anything it stands in for, so EngineSeamTests can state
        /// what the plugin was given rather than restate what it should have produced.</para>
        /// </summary>
        public static Type[] Anchors()
        {
            return new Type[6]
            {
                typeof(int),                                    // System.Private.CoreLib
                typeof(Keen.VRage.Library.Diagnostics.Log),     // VRage.Library
                typeof(EntityBuilder),                          // VRage.DCS
                typeof(EngineBuilder),                          // VRage.Core
                typeof(GameApp),                                // SpaceEngineers2
                typeof(ScriptingAnchor)                         // SpaceEngineers2, again
            };
        }

        /// <summary>
        /// GameApp.cs:482, including the throw. The plugin has its own copy of this search
        /// that returns null instead, because the game is entitled to assume its own
        /// component list and a plugin reading it mid-construction is not.
        /// </summary>
        private static T OB<T>(EntityBuilder builder)
        {
            foreach (EntityBuilder.ComponentBuildInfo component in builder.Components)
            {
                object objectBuilder = component.ObjectBuilder;
                if (objectBuilder is T)
                {
                    return (T)objectBuilder;
                }
            }

            throw new Exception("Not found " + typeof(T).Name);
        }
    }
}
