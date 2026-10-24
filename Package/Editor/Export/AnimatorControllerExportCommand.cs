using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AssetOps
{
    // Animator Controller: parameters, layers, state machines with their states, sub-state machines,
    // transitions with conditions, blend trees. State paths ("Locomotion/Run") are what EditAnimatorController takes.
    // Usage: assetops.py AnimatorControllerExport Assets/Animation/Player.controller
    // Docs/Export.md
    public sealed class AnimatorControllerExportCommand : AssetOpsCommand
    {
        public override string Summary
        {
            get { return "Animator Controller parameters, layers, states, transitions and blend trees"; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            List<string> entries;
            if (!ExportHelper.Entries(ctx, p => AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(AnimatorController), out entries))
            {
                return ExportHelper.NoEntries(ctx);
            }
            bool failed = false;
            foreach (string path in entries)
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (controller == null)
                {
                    ctx.Log.Error(path + " is not an Animator Controller");
                    failed = true;
                    continue;
                }
                JObject json = ExportHelper.Header(ctx, path);
                json["Parameters"] = new JArray(controller.parameters.Select(Parameter).ToArray());
                var layers = new JArray();
                for (int i = 0; i < controller.layers.Length; i++)
                {
                    layers.Add(Layer(controller, controller.layers[i], i));
                }
                json["Layers"] = layers;
                ExportHelper.Save(ctx, path, json);
            }
            return failed ? ExitCode.Failed : ExitCode.Success;
        }

        static JObject Parameter(AnimatorControllerParameter p)
        {
            var obj = new JObject { ["Name"] = p.name, ["Type"] = p.type.ToString() };
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float:
                    obj["Default"] = Json.F(p.defaultFloat);
                    break;
                case AnimatorControllerParameterType.Int:
                    obj["Default"] = p.defaultInt;
                    break;
                case AnimatorControllerParameterType.Bool:
                    obj["Default"] = p.defaultBool;
                    break;
            }
            return obj;
        }

        // State and sub-machine paths of the layer being exported, so transitions name their target by path.
        static readonly Dictionary<AnimatorState, string> statePaths = new Dictionary<AnimatorState, string>();
        static readonly Dictionary<AnimatorStateMachine, string> machinePaths = new Dictionary<AnimatorStateMachine, string>();

        public static void IndexPaths(AnimatorStateMachine sm, string prefix, Dictionary<AnimatorState, string> states, Dictionary<AnimatorStateMachine, string> machines)
        {
            machines[sm] = prefix.TrimEnd('/');
            foreach (ChildAnimatorState child in sm.states)
            {
                if (child.state != null)
                {
                    states[child.state] = prefix + child.state.name;
                }
            }
            foreach (ChildAnimatorStateMachine child in sm.stateMachines)
            {
                if (child.stateMachine != null)
                {
                    IndexPaths(child.stateMachine, prefix + child.stateMachine.name + "/", states, machines);
                }
            }
        }

        static JObject Layer(AnimatorController controller, AnimatorControllerLayer layer, int index)
        {
            statePaths.Clear();
            machinePaths.Clear();
            if (layer.stateMachine != null)
            {
                IndexPaths(layer.stateMachine, string.Empty, statePaths, machinePaths);
            }
            var obj = new JObject
            {
                ["Name"] = layer.name,
                ["Index"] = index,
                ["Weight"] = Json.F(layer.defaultWeight),
                ["Blending"] = layer.blendingMode.ToString(),
                ["IKPass"] = layer.iKPass,
            };
            if (layer.avatarMask != null)
            {
                obj["AvatarMask"] = AssetDatabase.GetAssetPath(layer.avatarMask);
            }
            if (layer.syncedLayerIndex >= 0)
            {
                obj["SyncedLayer"] = layer.syncedLayerIndex;
                obj["SyncedLayerAffectsTiming"] = layer.syncedLayerAffectsTiming;
                return obj;
            }
            if (layer.stateMachine != null)
            {
                obj["StateMachine"] = StateMachine(layer.stateMachine, string.Empty);
            }
            return obj;
        }

        static JObject StateMachine(AnimatorStateMachine sm, string prefix)
        {
            var obj = new JObject { ["Name"] = sm.name };
            if (sm.defaultState != null)
            {
                obj["DefaultState"] = prefix + sm.defaultState.name;
            }
            var states = new JArray();
            foreach (ChildAnimatorState child in sm.states)
            {
                if (child.state == null)
                {
                    // A broken controller can list a state that no longer exists.
                    continue;
                }
                states.Add(State(child, prefix, sm));
            }
            obj["States"] = states;

            if (sm.anyStateTransitions.Length > 0)
            {
                obj["AnyStateTransitions"] = new JArray(sm.anyStateTransitions.Select(t => (JToken)Transition(t, sm)).ToArray());
            }
            if (sm.entryTransitions.Length > 0)
            {
                obj["EntryTransitions"] = new JArray(sm.entryTransitions.Select(t => (JToken)Transition(t, sm)).ToArray());
            }
            if (sm.stateMachines.Length > 0)
            {
                var subs = new JArray();
                foreach (ChildAnimatorStateMachine child in sm.stateMachines)
                {
                    if (child.stateMachine == null)
                    {
                        // A broken controller can list a sub-machine that no longer exists.
                        continue;
                    }
                    JObject sub = StateMachine(child.stateMachine, prefix + child.stateMachine.name + "/");
                    sub["Path"] = prefix + child.stateMachine.name;
                    sub["Position"] = Json.Array(child.position.x, child.position.y);
                    var exits = sm.GetStateMachineTransitions(child.stateMachine);
                    if (exits.Length > 0)
                    {
                        sub["ExitTransitions"] = new JArray(exits.Select(t => (JToken)Transition(t, sm)).ToArray());
                    }
                    subs.Add(sub);
                }
                obj["StateMachines"] = subs;
            }
            return obj;
        }

        static JObject State(ChildAnimatorState child, string prefix, AnimatorStateMachine owner)
        {
            AnimatorState s = child.state;
            var obj = new JObject
            {
                ["Path"] = prefix + s.name,
                ["Position"] = Json.Array(child.position.x, child.position.y),
                ["Motion"] = Motion(s.motion),
                ["Speed"] = Json.F(s.speed),
            };
            if (!string.IsNullOrEmpty(s.tag))
            {
                obj["Tag"] = s.tag;
            }
            if (s.speedParameterActive)
            {
                obj["SpeedParameter"] = s.speedParameter;
            }
            if (s.timeParameterActive)
            {
                obj["TimeParameter"] = s.timeParameter;
            }
            if (s.mirror || s.mirrorParameterActive)
            {
                obj["Mirror"] = s.mirror;
                if (s.mirrorParameterActive)
                {
                    obj["MirrorParameter"] = s.mirrorParameter;
                }
            }
            if (s.cycleOffset != 0 || s.cycleOffsetParameterActive)
            {
                obj["CycleOffset"] = Json.F(s.cycleOffset);
                if (s.cycleOffsetParameterActive)
                {
                    obj["CycleOffsetParameter"] = s.cycleOffsetParameter;
                }
            }
            obj["WriteDefaultValues"] = s.writeDefaultValues;
            if (s.iKOnFeet)
            {
                obj["IKOnFeet"] = true;
            }
            if (s.behaviours.Length > 0)
            {
                obj["Behaviours"] = new JArray(s.behaviours.Where(b => b != null).Select(b => (object)TypeResolver.DisplayName(b.GetType())).ToArray());
            }
            if (s.transitions.Length > 0)
            {
                obj["Transitions"] = new JArray(s.transitions.Select(t => (JToken)Transition(t, owner)).ToArray());
            }
            return obj;
        }

        public static JToken Motion(Motion motion)
        {
            if (motion == null)
            {
                return JValue.CreateNull();
            }
            var tree = motion as BlendTree;
            if (tree == null || AssetDatabase.IsMainAsset(tree))
            {
                return ObjectRefs.Write(motion, RefScope.None);
            }
            var obj = new JObject
            {
                ["BlendTree"] = tree.name,
                ["BlendType"] = tree.blendType.ToString(),
                ["Parameter"] = tree.blendParameter,
            };
            if (tree.blendType != BlendTreeType.Simple1D && tree.blendType != BlendTreeType.Direct)
            {
                obj["ParameterY"] = tree.blendParameterY;
            }
            var children = new JArray();
            foreach (ChildMotion c in tree.children)
            {
                var child = new JObject { ["Motion"] = Motion(c.motion), ["TimeScale"] = Json.F(c.timeScale) };
                if (tree.blendType == BlendTreeType.Simple1D)
                {
                    child["Threshold"] = Json.F(c.threshold);
                }
                else if (tree.blendType == BlendTreeType.Direct)
                {
                    child["DirectParameter"] = c.directBlendParameter;
                }
                else
                {
                    child["Position"] = Json.Array(c.position.x, c.position.y);
                }
                if (c.mirror)
                {
                    child["Mirror"] = true;
                }
                children.Add(child);
            }
            obj["Children"] = children;
            return obj;
        }

        static JObject Transition(AnimatorTransitionBase t, AnimatorStateMachine owner)
        {
            var obj = new JObject();
            if (t.isExit)
            {
                obj["To"] = "Exit";
            }
            else if (t.destinationState != null)
            {
                string path;
                obj["To"] = statePaths.TryGetValue(t.destinationState, out path) ? path : t.destinationState.name;
            }
            else if (t.destinationStateMachine != null)
            {
                string path;
                obj["ToStateMachine"] = machinePaths.TryGetValue(t.destinationStateMachine, out path) ? path : t.destinationStateMachine.name;
            }
            if (!string.IsNullOrEmpty(t.name))
            {
                obj["Name"] = t.name;
            }
            if (t.solo)
            {
                obj["Solo"] = true;
            }
            if (t.mute)
            {
                obj["Mute"] = true;
            }
            var st = t as AnimatorStateTransition;
            if (st != null)
            {
                obj["HasExitTime"] = st.hasExitTime;
                if (st.hasExitTime)
                {
                    obj["ExitTime"] = Json.F(st.exitTime);
                }
                obj["Duration"] = Json.F(st.duration);
                obj["FixedDuration"] = st.hasFixedDuration;
                if (st.offset != 0)
                {
                    obj["Offset"] = Json.F(st.offset);
                }
                if (st.interruptionSource != TransitionInterruptionSource.None)
                {
                    obj["Interruption"] = st.interruptionSource.ToString();
                    obj["OrderedInterruption"] = st.orderedInterruption;
                }
                if (st.canTransitionToSelf)
                {
                    obj["CanTransitionToSelf"] = true;
                }
            }
            obj["Conditions"] = new JArray(t.conditions.Select(c => (JToken)new JObject
            {
                ["Parameter"] = c.parameter,
                ["Mode"] = c.mode.ToString(),
                ["Threshold"] = Json.F(c.threshold),
            }).ToArray());
            return obj;
        }
    }
}
