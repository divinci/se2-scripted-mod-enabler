using System;
using System.Collections.Generic;

// Shapes taken from Space Engineers 2 build 2.3.0.2798.
// Hand-written, and nothing checks them against the real assemblies — a GAMEDIR build
// before a release is the only guard against drift.

namespace Keen.VRage.DCS.Builders
{
    /// <summary>
    /// A struct in the game too, which matters: <c>EngineBuilder.EntityBuilder</c> is a
    /// field, so reading <c>.Components</c> off it is a read through a struct field and
    /// not a copy the plugin could accidentally mutate.
    ///
    /// <para>The real one carries a dozen more members — DebugName, DataEntity, Scene,
    /// BuildStrategy, the fluent Add overloads. <c>Components</c> is the only one the
    /// plugin reads.</para>
    /// </summary>
    public struct EntityBuilder
    {
        /// <summary>
        /// Real signature is
        /// <c>ComponentBuildInfo(Type componentType, object definition, object objectBuilder, StringId[]? tags)</c>
        /// with implicit conversions from Type and Definition. Only the fields survive
        /// here, because that is all a search over the list can look at.
        /// </summary>
        public struct ComponentBuildInfo
        {
            public Type Type;

            public object Definition;

            public object ObjectBuilder;
        }

        /// <summary>Null until the engine starts composing, exactly as in the game.</summary>
        public List<ComponentBuildInfo> Components;
    }
}
