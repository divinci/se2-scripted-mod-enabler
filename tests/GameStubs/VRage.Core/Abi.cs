using System;
using System.Collections.Generic;

// Shapes taken from Space Engineers 2 build 2.3.0.2798.
// Hand-written, and nothing checks them against the real assemblies — a GAMEDIR build
// before a release is the only guard against drift.

namespace Keen.VRage.Core.Project
{
    /// <summary>Four members, in this order, in the game too.</summary>
    public enum ProjectType
    {
        Unknown,
        Vanilla,
        DLC,
        Mod
    }

    /// <summary>
    /// The real one declares the compile entry points. Present here only as the value
    /// type of <c>CodeProviders</c>, which is the dictionary the plugin counts.
    /// </summary>
    public interface IProjectCodeProvider
    {
    }
}

namespace Keen.VRage.Core.EngineComponents
{
    using Keen.VRage.Core.Project;
    using Keen.VRage.DCS.Builders;

    /// <summary>
    /// Real one is <c>public class EngineBuilder : IDisposable</c> with a lot more on it —
    /// SceneBuilder, EntitySerializer, the service locator. <c>EntityBuilder</c> is a
    /// public field there as well, which is what lets the plugin read the component list
    /// mid-construction.
    /// </summary>
    public class EngineBuilder
    {
        public EntityBuilder EntityBuilder;
    }

    /// <summary>
    /// The real component loads and manages VRage projects; all that matters here is its
    /// nested object builder, because that is what <c>AddScripting</c> reaches for and
    /// what the plugin looks for to decide whether scripting is already on.
    /// </summary>
    public class ProjectManagerEngineComponent
    {
        /// <summary>
        /// Real one also has ProjectLocators, ProjectsPushed and BeforeProjectsUnloaded.
        /// <c>CodeProviders</c> is auto-initialised to an empty dictionary there too, so
        /// "no scripting yet" really does read as Count == 0 rather than null.
        /// </summary>
        public class ObjectBuilder
        {
            public Dictionary<ProjectType, IProjectCodeProvider> CodeProviders { get; set; }
                = new Dictionary<ProjectType, IProjectCodeProvider>();
        }
    }
}

namespace Keen.VRage.Core.Plugins
{
    using Keen.VRage.Core.EngineComponents;

    /// <summary>Empty marker interface, exactly as in the game.</summary>
    public interface IPlugin
    {
    }

    /// <summary>
    /// The real one discovers and instantiates plugins. All the plugin uses is the
    /// constructor parameter type and the one event.
    /// </summary>
    public class PluginHost
    {
        public string[] Args;

        public PluginHost(string[] args)
        {
            Args = args;
        }

        public event Action<EngineBuilder> OnBeforeEngineInstantiated;

        /// <summary>
        /// Not on the real PluginHost — the game raises this from
        /// <c>InvokeOnBeforeEngineInstantiated</c>, with no try/catch around the invoke.
        /// Present so PulsarSeamTests can drive the plugin's second entry point without a
        /// game, including the part where a throw would escape into startup.
        /// </summary>
        public void RaiseOnBeforeEngineInstantiated(EngineBuilder engine)
        {
            OnBeforeEngineInstantiated?.Invoke(engine);
        }
    }
}
