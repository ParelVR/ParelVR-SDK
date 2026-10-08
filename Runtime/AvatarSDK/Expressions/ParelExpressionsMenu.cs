using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// One page of the avatar's Expressions Menu, laid out exactly like VRChat's: up to 8 controls,
    /// each a Button, Toggle, Sub Menu, Two Axis Puppet, Four Axis Puppet or Radial Puppet, bound to
    /// parameters from the avatar's <see cref="ParelExpressionParameters"/>. In game it becomes the
    /// radial menu's Expressions page.
    /// </summary>
    [CreateAssetMenu(fileName = "ExpressionsMenu", menuName = "ParelVR/Avatars/Expressions Menu", order = 0)]
    public sealed class ParelExpressionsMenu : ScriptableObject
    {
        public const int MaxControls = 8;

        public enum ControlType
        {
            Button = 101,
            Toggle = 102,
            SubMenu = 103,
            TwoAxisPuppet = 201,
            FourAxisPuppet = 202,
            RadialPuppet = 203,
        }

        /// <summary>Control style slot (kept for VRChat parity; every style currently draws the same).</summary>
        public enum Style
        {
            Style1 = 0,
            Style2 = 1,
            Style3 = 2,
            Style4 = 3,
        }

        [Serializable]
        public sealed class ControlParameter
        {
            public string name = string.Empty;
        }

        [Serializable]
        public sealed class Label
        {
            public string name = string.Empty;
            public Texture2D icon;
        }

        [Serializable]
        public sealed class Control
        {
            public string name = "New Control";
            public Texture2D icon;
            public ControlType type = ControlType.Toggle;
            public Style style = Style.Style1;

            [Tooltip("Button: set to Value while held. Toggle: set to Value while on. Sub Menu / puppets: set to Value while open.")]
            public ControlParameter parameter = new ControlParameter();
            public float value = 1f;

            [Tooltip("Sub Menu only: the page this control opens.")]
            public ParelExpressionsMenu subMenu;

            [Tooltip("Puppets: Two Axis = [Horizontal, Vertical], Four Axis = [Up, Right, Down, Left], Radial = [Rotation].")]
            public ControlParameter[] subParameters = Array.Empty<ControlParameter>();

            [Tooltip("Puppet direction labels in VRChat order: Up, Right, Down, Left.")]
            public Label[] labels = Array.Empty<Label>();

            public string GetSubParameter(int index)
            {
                if (subParameters == null || index < 0 || index >= subParameters.Length) return null;
                ControlParameter p = subParameters[index];
                return p != null && !string.IsNullOrEmpty(p.name) ? p.name : null;
            }

            public Label GetLabel(int index)
            {
                if (labels == null || index < 0 || index >= labels.Length) return null;
                return labels[index];
            }

            /// <summary>How many sub-parameter slots this control type uses.</summary>
            public static int SubParameterCount(ControlType type)
            {
                switch (type)
                {
                    case ControlType.TwoAxisPuppet: return 2;
                    case ControlType.FourAxisPuppet: return 4;
                    case ControlType.RadialPuppet: return 1;
                    default: return 0;
                }
            }

            /// <summary>How many direction labels this control type shows.</summary>
            public static int LabelCount(ControlType type)
            {
                switch (type)
                {
                    case ControlType.TwoAxisPuppet:
                    case ControlType.FourAxisPuppet:
                        return 4;
                    default:
                        return 0;
                }
            }
        }

        public List<Control> controls = new List<Control>();

        /// <summary>Every parameter name this page (and its sub menus) references, without duplicates.</summary>
        public void CollectParameterNames(HashSet<string> into, HashSet<ParelExpressionsMenu> visited = null)
        {
            visited ??= new HashSet<ParelExpressionsMenu>();
            if (!visited.Add(this) || controls == null) return;

            for (int i = 0; i < controls.Count; i++)
            {
                Control c = controls[i];
                if (c == null) continue;
                if (c.parameter != null && !string.IsNullOrEmpty(c.parameter.name)) into.Add(c.parameter.name);
                if (c.subParameters != null)
                {
                    for (int s = 0; s < c.subParameters.Length; s++)
                    {
                        string sub = c.GetSubParameter(s);
                        if (sub != null) into.Add(sub);
                    }
                }
                if (c.type == ControlType.SubMenu && c.subMenu != null) c.subMenu.CollectParameterNames(into, visited);
            }
        }
    }
}
