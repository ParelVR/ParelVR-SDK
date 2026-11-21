using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.Rendering
{
    /// <summary>
    /// The game's URP and HDRP global settings, kept in Resources (<see cref="ParelPipelineSwitch.GlobalsResource"/>) so
    /// they are in the player. The game is built with no SRP active, so a build has no global settings for URP or HDRP,
    /// and Unity refuses to switch to either until <see cref="GraphicsSettings.TrySetCurrentRenderPipelineGlobalSettings"/>
    /// has been given the matching one (game only; the SDK doesn't ship this asset).
    /// </summary>
    public sealed class ParelPipelineGlobals : ScriptableObject
    {
        public RenderPipelineGlobalSettings universal;
        public RenderPipelineGlobalSettings highDefinition;
    }
}
