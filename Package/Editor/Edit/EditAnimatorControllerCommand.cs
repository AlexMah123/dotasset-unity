using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DotAsset
{
    // Edits Animator Controllers: parameters, layers, sub-state machines, states, blend trees, transitions.
    // States are addressed by layer plus path ("Locomotion/Run"), exactly as AnimatorControllerExport prints them.
    // Every change is recorded on one undo group, a failed or dry run reverts it before anything is saved.
    // Usage: dotasset.py EditAnimatorController --args '-spec="C:/temp/anim.json" -apply'
    // Docs/Edit.md
    public sealed class EditAnimatorControllerCommand : DotAssetCommand
    {
        static readonly string[] OpNames =
        {
            "AddParameter", "SetParameter", "RenameParameter", "RemoveParameter", "AddLayer", "SetLayer", "RemoveLayer",
            "AddStateMachine", "AddState", "SetState", "RemoveState", "SetDefaultState",
            "AddTransition", "SetTransition", "RemoveTransition",
        };

        public override string Summary
        {
            get { return "Animator Controller parameters, layers, states, blend trees and transitions"; }
        }

        public override bool TakesAssetList
        {
            get { return false; }
        }

        public override IEnumerable<string> Arguments
        {
            get { return new[] { "spec" }; }
        }

        public override ExitCode Execute(RunContext ctx)
        {
            JArray targets = Spec.LoadTargets(ctx);
            if (targets == null)
            {
                return ExitCode.Refused;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++)
            {
                if (!Spec.CheckKeys(ctx, targets[i], "Targets[" + i + "]", "AssetPath", "Ops"))
                {
                    return ExitCode.Refused;
                }
                // One target per controller: headless the undo revert of a failed target is not guaranteed,
                // and a later target saving the same controller would carry the failed target's changes.
                string opError = ValidateOps(Json.Get(targets[i], "Ops") as JArray, "Targets[" + i + "]");
                if (opError != null)
                {
                    ctx.Log.Error(opError);
                    return ExitCode.Refused;
                }
                string p = Json.Str(targets[i], "AssetPath");
                if (p != null && !seen.Add(p))
                {
                    ctx.Log.Error(p + " appears in more than one target, merge their Ops");
                    return ExitCode.Refused;
                }
            }

            int written = 0;
            foreach (JToken target in targets)
            {
                string path = Json.Str(target, "AssetPath") ?? "(no AssetPath)";
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                var ops = Json.Get(target, "Ops") as JArray;
                if (controller == null)
                {
                    ctx.FailTarget(path, "no Animator Controller at that path");
                    continue;
                }
                string refused = ReadOnly.Reason(path) ?? OpenWork.UnsavedIn(path);
                if (refused != null)
                {
                    ctx.FailTarget(path, refused);
                    continue;
                }
                if (ops == null)
                {
                    ctx.FailTarget(path, "needs an \"Ops\" array");
                    continue;
                }

                ctx.BeginTarget();
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("DotAsset: EditAnimatorController");
                int group = Undo.GetCurrentGroup();
                bool keep = false;
                bool wasDirty = AssetDatabase.LoadAllAssetsAtPath(path).Any(x => x != null && EditorUtility.IsDirty(x));
                try
                {
                    var editor = new ControllerEditor(controller, ctx.Log);
                    if (!editor.Apply(ops) || ctx.TargetFailed())
                    {
                        ctx.FailTarget(path, editor.Errors.Count > 0 ? editor.Errors[0] : ctx.TargetError);
                        continue;
                    }
                    if (ctx.DryRun)
                    {
                        ctx.Log.Info(path + ": " + ops.Count + " op(s) would apply");
                        foreach (JToken op in ops)
                        {
                            ctx.PlanFile("op", path, Json.Str(op, "Op"));
                        }
                        continue;
                    }
                    EditorUtility.SetDirty(controller);
                    RunJournal.Keep(AssetDatabase.GetAssetPath(controller));
                    AssetDatabase.SaveAssetIfDirty(controller);
                    keep = true;
                    ctx.WarnIfSavedWithErrors(path);
                    written++;
                    ctx.Log.Info("saved " + path + " (" + ops.Count + " op(s))");
                }
                finally
                {
                    if (keep)
                    {
                        Undo.CollapseUndoOperations(group);
                    }
                    else
                    {
                        Undo.RevertAllDownToGroup(group);
                        if (!wasDirty)
                        {
                            // The revert restores the content but leaves it marked dirty, a later Save Project would
                            // rewrite the file. Unsaved edits the user already had stay dirty.
                            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                            {
                                if (o != null)
                                {
                                    EditorUtility.ClearDirty(o);
                                }
                            }
                        }
                    }
                }
            }
            if (!ctx.DryRun)
            {
                ctx.Log.Info("saved " + written + " controller(s)");
            }
            return ctx.FailedTargets.Count > 0 ? ExitCode.Failed : ExitCode.Success;
        }

        // Checks op names and top-level keys before anything is created, for CreateAsset.
        public static string ValidateOps(JArray ops, string where)
        {
            return ControllerEditor.Validate(ops, where);
        }

        sealed class ControllerEditor
        {
            public static string Validate(JArray ops, string where)
            {
                if (ops == null)
                {
                    return null;
                }
                for (int i = 0; i < ops.Count; i++)
                {
                    string name = Json.Str(ops[i], "Op");
                    string[] keys;
                    if (name == null || !OpKeys.TryGetValue(name, out keys))
                    {
                        return where + " Ops[" + i + "]: unknown op '" + (name ?? "(none)") + "'. Ops: " + string.Join(", ", OpNames);
                    }
                    string strays = Json.UnknownKeys(ops[i], keys);
                    if (strays != null)
                    {
                        return where + " Ops[" + i + "] " + name + ": " + strays;
                    }
                }
                return null;
            }

            readonly AnimatorController controller;
            readonly RunLog log;
            public readonly List<string> Errors = new List<string>();
            string label;

            public ControllerEditor(AnimatorController controller, RunLog log)
            {
                this.controller = controller;
                this.log = log;
            }

            public bool Apply(JArray ops)
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    JToken op = ops[i];
                    string name = Json.Str(op, "Op");
                    label = "Ops[" + i + "] " + (name ?? "(no Op)");
                    int before = Errors.Count;
                    string[] keys;
                    if (name != null && OpKeys.TryGetValue(name, out keys) && !Keys(op, keys))
                    {
                        return false;
                    }
                    try
                    {
                        One(name, op);
                    }
                    catch (Exception e)
                    {
                        Fail(e.Message);
                    }
                    if (Errors.Count > before)
                    {
                        return false;
                    }
                }
                return true;
            }

            void Fail(string message)
            {
                Errors.Add(label + ": " + message);
            }

            static readonly string[] StateFields =
            {
                "NewName", "Motion", "Speed", "Tag", "WriteDefaultValues", "SpeedParameter", "TimeParameter", "Mirror",
                "MirrorParameter", "CycleOffset", "CycleOffsetParameter", "IKOnFeet", "Default", "Properties",
            };

            static readonly string[] TransitionFields =
            {
                "Name", "Solo", "Mute", "HasExitTime", "ExitTime", "Duration", "FixedDuration", "Offset", "Interruption",
                "OrderedInterruption", "CanTransitionToSelf", "Conditions",
            };

            static string[] With(string[] fields, params string[] keys)
            {
                return keys.Concat(fields).ToArray();
            }

            // Keys each op reads. A misspelt key ("Conditons") fails the op instead of being dropped.
            static readonly Dictionary<string, string[]> OpKeys = new Dictionary<string, string[]>
            {
                { "AddParameter", new[] { "Op", "Layer", "Name", "Type", "Default" } },
                { "SetParameter", new[] { "Op", "Layer", "Name", "Default" } },
                { "RenameParameter", new[] { "Op", "Layer", "Name", "NewName" } },
                { "RemoveParameter", new[] { "Op", "Layer", "Name" } },
                { "AddLayer", new[] { "Op", "Name", "Weight", "Blending", "IKPass", "AvatarMask" } },
                { "SetLayer", new[] { "Op", "Layer", "NewName", "Weight", "Blending", "IKPass", "AvatarMask" } },
                { "RemoveLayer", new[] { "Op", "Layer" } },
                { "AddStateMachine", new[] { "Op", "Layer", "Path", "Position" } },
                { "AddState", With(StateFields, "Op", "Layer", "Path", "Position") },
                { "SetState", With(StateFields, "Op", "Layer", "State") },
                { "RemoveState", new[] { "Op", "Layer", "State" } },
                { "SetDefaultState", new[] { "Op", "Layer", "State" } },
                { "AddTransition", With(TransitionFields, "Op", "Layer", "From", "To", "StateMachine") },
                { "SetTransition", With(TransitionFields, "Op", "Layer", "From", "To", "StateMachine", "Index") },
                { "RemoveTransition", new[] { "Op", "Layer", "From", "To", "StateMachine", "Index" } },
            };

            bool Keys(JToken token, string[] allowed)
            {
                var obj = token as JObject;
                if (obj == null)
                {
                    Fail("is not an object");
                    return false;
                }
                string strays = Json.UnknownKeys(obj, allowed);
                if (strays != null)
                {
                    Fail(strays);
                    return false;
                }
                return true;
            }

            void One(string name, JToken op)
            {
                switch (name)
                {
                    case "AddParameter":
                        AddParameter(op);
                        return;
                    case "SetParameter":
                        SetParameter(op);
                        return;
                    case "RenameParameter":
                        RenameParameter(op);
                        return;
                    case "RemoveParameter":
                    {
                        int index = ParameterIndex(Json.Str(op, "Name"));
                        if (index >= 0)
                        {
                            controller.RemoveParameter(index);
                            log.Info("removed parameter " + Json.Str(op, "Name"));
                        }
                        return;
                    }
                    case "AddLayer":
                        AddLayer(op);
                        return;
                    case "SetLayer":
                        SetLayer(op);
                        return;
                    case "RemoveLayer":
                    {
                        int index = LayerIndex(op);
                        if (index >= 0)
                        {
                            if (controller.layers.Length == 1)
                            {
                                Fail("a controller keeps at least one layer");
                                return;
                            }
                            AnimatorControllerLayer[] before = controller.layers;
                            for (int i = 0; i < before.Length; i++)
                            {
                                if (i != index && before[i].syncedLayerIndex == index)
                                {
                                    Fail("layer '" + before[i].name + "' is synced to it, remove that layer first");
                                    return;
                                }
                            }
                            controller.RemoveLayer(index);
                            // RemoveLayer leaves synced layers' indices as they were, shift those past the removed one.
                            AnimatorControllerLayer[] after = controller.layers;
                            bool shifted = false;
                            for (int i = 0; i < after.Length; i++)
                            {
                                if (after[i].syncedLayerIndex > index)
                                {
                                    after[i].syncedLayerIndex--;
                                    shifted = true;
                                }
                            }
                            if (shifted)
                            {
                                Undo.RecordObject(controller, "DotAsset: EditAnimatorController");
                                controller.layers = after;
                            }
                            log.Info("removed layer " + index);
                        }
                        return;
                    }
                    case "AddStateMachine":
                        AddStateMachine(op);
                        return;
                    case "AddState":
                        AddState(op);
                        return;
                    case "SetState":
                    {
                        AnimatorStateMachine owner;
                        AnimatorState state = FindState(op, "State", out owner);
                        if (state != null)
                        {
                            SetStateFields(state, owner, op);
                        }
                        return;
                    }
                    case "RemoveState":
                    {
                        AnimatorStateMachine owner;
                        AnimatorState state = FindState(op, "State", out owner);
                        if (state != null)
                        {
                            owner.RemoveState(state);
                            log.Info("removed state " + Json.Str(op, "State"));
                        }
                        return;
                    }
                    case "SetDefaultState":
                    {
                        AnimatorStateMachine owner;
                        AnimatorState state = FindState(op, "State", out owner);
                        if (state != null)
                        {
                            Undo.RecordObject(owner, "Default state");
                            owner.defaultState = state;
                        }
                        return;
                    }
                    case "AddTransition":
                        AddTransition(op);
                        return;
                    case "SetTransition":
                    {
                        AnimatorTransitionBase t = FindTransition(op);
                        if (t != null)
                        {
                            SetTransitionFields(t, op);
                        }
                        return;
                    }
                    case "RemoveTransition":
                        RemoveTransition(op);
                        return;
                    default:
                        Fail("unknown op. Ops: " + string.Join(", ", OpNames));
                        return;
                }
            }

            // ---------------------------------------------------------- parameters

            int ParameterIndex(string name)
            {
                var ps = controller.parameters;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].name == name)
                    {
                        return i;
                    }
                }
                Fail("no parameter '" + name + "'. Parameters: " + string.Join(", ", ps.Select(p => p.name)));
                return -1;
            }

            void AddParameter(JToken op)
            {
                string name = Json.Str(op, "Name");
                if (string.IsNullOrEmpty(name))
                {
                    Fail("needs \"Name\"");
                    return;
                }
                if (controller.parameters.Any(p => p.name == name))
                {
                    Fail("parameter '" + name + "' already exists");
                    return;
                }
                AnimatorControllerParameterType type;
                if (!Enum.TryParse(Json.Str(op, "Type", "Float"), true, out type))
                {
                    Fail("Type is Float, Int, Bool or Trigger");
                    return;
                }
                controller.AddParameter(name, type);
                if (Json.Has(op, "Default"))
                {
                    SetDefault(name, Json.Get(op, "Default"));
                }
                log.Info("added parameter " + name + " (" + type + ")");
            }

            void SetParameter(JToken op)
            {
                string name = Json.Str(op, "Name");
                if (ParameterIndex(name) < 0)
                {
                    return;
                }
                if (Json.Has(op, "Default"))
                {
                    SetDefault(name, Json.Get(op, "Default"));
                }
            }

            void SetDefault(string name, JToken value)
            {
                Undo.RecordObject(controller, "Parameter default");
                AnimatorControllerParameter[] ps = controller.parameters;
                int index = Array.FindIndex(ps, p => p.name == name);
                double d;
                switch (ps[index].type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (!Json.TryDouble(value, out d))
                        {
                            Fail("Default of a Float is a number");
                            return;
                        }
                        ps[index].defaultFloat = (float)d;
                        break;
                    case AnimatorControllerParameterType.Int:
                        if (!Json.TryDouble(value, out d))
                        {
                            Fail("Default of an Int is a number");
                            return;
                        }
                        if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
                        {
                            Fail("Default of an Int is a whole number in the int range");
                            return;
                        }
                        ps[index].defaultInt = (int)d;
                        break;
                    case AnimatorControllerParameterType.Bool:
                    {
                        bool b;
                        if (!TryBool(value, out b))
                        {
                            Fail("Default of a Bool is true or false");
                            return;
                        }
                        ps[index].defaultBool = b;
                        break;
                    }
                    default:
                        Fail("a Trigger has no default");
                        return;
                }
                controller.parameters = ps;
            }

            // True with the value when the key holds a number. A key that is there but holds something else fails the op,
            // "Weight": "0,5" must not pass as "no change".
            bool Number(JToken holder, string key, out double d)
            {
                d = 0;
                JToken value = Json.Get(holder, key);
                if (value == null || value.Type == JTokenType.Null)
                {
                    return false;
                }
                if (Json.TryDouble(value, out d))
                {
                    return true;
                }
                Fail(key + " is a number, got " + value.ToString(Newtonsoft.Json.Formatting.None));
                return false;
            }

            static bool TryBool(JToken value, out bool b)
            {
                b = false;
                if (value == null)
                {
                    return false;
                }
                if (value.Type == JTokenType.Boolean)
                {
                    b = (bool)value;
                    return true;
                }
                string text = value.Type == JTokenType.Integer || value.Type == JTokenType.String ? ((string)value ?? string.Empty).Trim().ToLowerInvariant() : null;
                if (text == "true" || text == "1")
                {
                    b = true;
                    return true;
                }
                return text == "false" || text == "0";
            }

            // Renames the parameter and every condition, blend tree and state field that reads it.
            void RenameParameter(JToken op)
            {
                string name = Json.Str(op, "Name");
                string newName = Json.Str(op, "NewName");
                if (ParameterIndex(name) < 0)
                {
                    return;
                }
                if (string.IsNullOrEmpty(newName) || controller.parameters.Any(p => p.name == newName))
                {
                    Fail("NewName is missing or already taken");
                    return;
                }
                Undo.RecordObject(controller, "Rename parameter");
                AnimatorControllerParameter[] ps = controller.parameters;
                ps[Array.FindIndex(ps, p => p.name == name)].name = newName;
                controller.parameters = ps;
                int uses = 0;
                foreach (AnimatorControllerLayer layer in controller.layers)
                {
                    if (layer.stateMachine != null)
                    {
                        uses += RenameIn(layer.stateMachine, name, newName);
                    }
                }
                log.Info("renamed parameter " + name + " to " + newName + ", " + uses + " use(s) updated");
            }

            int RenameIn(AnimatorStateMachine sm, string from, string to)
            {
                int uses = 0;
                foreach (AnimatorStateTransition t in sm.anyStateTransitions)
                {
                    uses += RenameConditions(t, from, to);
                }
                foreach (AnimatorTransition t in sm.entryTransitions)
                {
                    uses += RenameConditions(t, from, to);
                }
                foreach (ChildAnimatorState child in sm.states)
                {
                    AnimatorState s = child.state;
                    if (s == null)
                    {
                        continue;
                    }
                    Undo.RecordObject(s, "Rename parameter");
                    if (s.speedParameter == from) { s.speedParameter = to; uses++; }
                    if (s.mirrorParameter == from) { s.mirrorParameter = to; uses++; }
                    if (s.cycleOffsetParameter == from) { s.cycleOffsetParameter = to; uses++; }
                    if (s.timeParameter == from) { s.timeParameter = to; uses++; }
                    foreach (AnimatorStateTransition t in s.transitions)
                    {
                        uses += RenameConditions(t, from, to);
                    }
                    uses += RenameInTree(s.motion as BlendTree, from, to);
                }
                foreach (ChildAnimatorStateMachine child in sm.stateMachines)
                {
                    foreach (AnimatorTransition t in sm.GetStateMachineTransitions(child.stateMachine))
                    {
                        uses += RenameConditions(t, from, to);
                    }
                    uses += RenameIn(child.stateMachine, from, to);
                }
                return uses;
            }

            static int RenameConditions(AnimatorTransitionBase t, string from, string to)
            {
                AnimatorCondition[] conditions = t.conditions;
                int uses = 0;
                for (int i = 0; i < conditions.Length; i++)
                {
                    if (conditions[i].parameter == from)
                    {
                        conditions[i].parameter = to;
                        uses++;
                    }
                }
                if (uses > 0)
                {
                    Undo.RecordObject(t, "Rename parameter");
                    t.conditions = conditions;
                }
                return uses;
            }

            static int RenameInTree(BlendTree tree, string from, string to)
            {
                if (tree == null)
                {
                    return 0;
                }
                int uses = 0;
                Undo.RecordObject(tree, "Rename parameter");
                if (tree.blendParameter == from) { tree.blendParameter = to; uses++; }
                if (tree.blendParameterY == from) { tree.blendParameterY = to; uses++; }
                ChildMotion[] children = tree.children;
                bool changed = false;
                for (int i = 0; i < children.Length; i++)
                {
                    if (children[i].directBlendParameter == from)
                    {
                        children[i].directBlendParameter = to;
                        uses++;
                        changed = true;
                    }
                    uses += RenameInTree(children[i].motion as BlendTree, from, to);
                }
                if (changed)
                {
                    tree.children = children;
                }
                return uses;
            }

            // ---------------------------------------------------------- layers

            int LayerIndex(JToken op)
            {
                JToken layer = Json.Get(op, "Layer");
                AnimatorControllerLayer[] layers = controller.layers;
                if (layer == null || layer.Type == JTokenType.Null)
                {
                    return 0;
                }
                if (layer.Type == JTokenType.Integer)
                {
                    int i = (int)(long)layer;
                    if (i >= 0 && i < layers.Length)
                    {
                        return i;
                    }
                }
                else
                {
                    int i = Array.FindIndex(layers, l => l.name == (string)layer);
                    if (i >= 0)
                    {
                        return i;
                    }
                }
                Fail("no layer " + layer + ". Layers: " + string.Join(", ", layers.Select(l => l.name)));
                return -1;
            }

            void AddLayer(JToken op)
            {
                string name = Json.Str(op, "Name");
                if (string.IsNullOrEmpty(name))
                {
                    Fail("needs \"Name\"");
                    return;
                }
                if (controller.layers.Any(l => l.name == name))
                {
                    Fail("layer '" + name + "' already exists");
                    return;
                }
                controller.AddLayer(name);
                SetLayerFields(controller.layers.Length - 1, op);
                log.Info("added layer " + name);
            }

            void SetLayer(JToken op)
            {
                int index = LayerIndex(op);
                if (index >= 0)
                {
                    SetLayerFields(index, op);
                }
            }

            void SetLayerFields(int index, JToken op)
            {
                Undo.RecordObject(controller, "Layer");
                AnimatorControllerLayer[] layers = controller.layers;
                AnimatorControllerLayer layer = layers[index];
                double d;
                if (Number(op, "Weight", out d))
                {
                    layer.defaultWeight = (float)d;
                }
                if (Json.Has(op, "Blending"))
                {
                    AnimatorLayerBlendingMode mode;
                    if (!Enum.TryParse(Json.Str(op, "Blending"), true, out mode))
                    {
                        Fail("Blending is Override or Additive");
                        return;
                    }
                    layer.blendingMode = mode;
                }
                if (Json.Has(op, "IKPass"))
                {
                    layer.iKPass = Json.Bool(op, "IKPass", false);
                }
                if (Json.Get(op, "AvatarMask") != null)
                {
                    string maskPath = Json.Str(op, "AvatarMask");
                    AvatarMask mask = maskPath != null ? AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath) : null;
                    if (maskPath != null && mask == null)
                    {
                        Fail("no AvatarMask at " + maskPath);
                        return;
                    }
                    layer.avatarMask = mask;
                }
                if (Json.Has(op, "NewName"))
                {
                    string newName = Json.Str(op, "NewName");
                    if (string.IsNullOrEmpty(newName))
                    {
                        Fail("NewName is empty");
                        return;
                    }
                    if (newName != layer.name)
                    {
                        // Unity would quietly add a suffix to a taken name, a spec that names one is refused instead.
                        for (int i = 0; i < layers.Length; i++)
                        {
                            if (i != index && layers[i].name == newName)
                            {
                                Fail("another layer is already named '" + newName + "'");
                                return;
                            }
                        }
                        layer.name = newName;
                    }
                }
                layers[index] = layer;
                controller.layers = layers;
            }

            AnimatorStateMachine LayerMachine(JToken op)
            {
                int index = LayerIndex(op);
                if (index < 0)
                {
                    return null;
                }
                AnimatorStateMachine sm = controller.layers[index].stateMachine;
                if (sm == null)
                {
                    Fail("layer " + index + " is synced and has no state machine of its own");
                }
                return sm;
            }

            // ---------------------------------------------------------- state machines and states

            AnimatorStateMachine FindMachine(AnimatorStateMachine root, string path)
            {
                AnimatorStateMachine sm = root;
                if (string.IsNullOrEmpty(path))
                {
                    return sm;
                }
                foreach (string part in path.Split('/'))
                {
                    AnimatorStateMachine next = sm.stateMachines.Select(c => c.stateMachine).FirstOrDefault(m => m != null && m.name == part);
                    if (next == null)
                    {
                        Fail("no sub-state machine '" + part + "' in '" + sm.name + "'. There: " + string.Join(", ", sm.stateMachines.Select(c => c.stateMachine.name)));
                        return null;
                    }
                    sm = next;
                }
                return sm;
            }

            static void SplitPath(string path, out string machine, out string leaf)
            {
                int slash = path.LastIndexOf('/');
                machine = slash < 0 ? string.Empty : path.Substring(0, slash);
                leaf = slash < 0 ? path : path.Substring(slash + 1);
            }

            AnimatorState FindState(JToken op, string key, out AnimatorStateMachine owner)
            {
                owner = null;
                string path = Json.Str(op, key);
                if (string.IsNullOrEmpty(path))
                {
                    Fail("needs \"" + key + "\"");
                    return null;
                }
                AnimatorStateMachine root = LayerMachine(op);
                if (root == null)
                {
                    return null;
                }
                string machinePath, leaf;
                SplitPath(path, out machinePath, out leaf);
                owner = FindMachine(root, machinePath);
                if (owner == null)
                {
                    return null;
                }
                AnimatorState state = owner.states.Select(c => c.state).FirstOrDefault(s => s != null && s.name == leaf);
                if (state == null)
                {
                    Fail("no state '" + path + "'. States here: " + string.Join(", ", owner.states.Select(c => c.state.name)));
                }
                return state;
            }

            void AddStateMachine(JToken op)
            {
                string path = Json.Str(op, "Path");
                AnimatorStateMachine root = LayerMachine(op);
                if (root == null || string.IsNullOrEmpty(path))
                {
                    if (root != null)
                    {
                        Fail("needs \"Path\"");
                    }
                    return;
                }
                string parentPath, leaf;
                SplitPath(path, out parentPath, out leaf);
                AnimatorStateMachine parent = FindMachine(root, parentPath);
                if (parent == null)
                {
                    return;
                }
                if (parent.stateMachines.Any(c => c.stateMachine.name == leaf))
                {
                    Fail("sub-state machine '" + path + "' already exists");
                    return;
                }
                float[] pos;
                if (ValueCodec.Floats(Json.Get(op, "Position"), 2, out pos))
                {
                    parent.AddStateMachine(leaf, new Vector3(pos[0], pos[1], 0));
                }
                else
                {
                    parent.AddStateMachine(leaf);
                }
                log.Info("added sub-state machine " + path);
            }

            void AddState(JToken op)
            {
                string path = Json.Str(op, "Path");
                AnimatorStateMachine root = LayerMachine(op);
                if (root == null)
                {
                    return;
                }
                if (string.IsNullOrEmpty(path))
                {
                    Fail("needs \"Path\", the state name, prefixed by sub-state machines (\"Locomotion/Run\")");
                    return;
                }
                string machinePath, leaf;
                SplitPath(path, out machinePath, out leaf);
                AnimatorStateMachine owner = FindMachine(root, machinePath);
                if (owner == null)
                {
                    return;
                }
                if (owner.states.Any(c => c.state.name == leaf))
                {
                    Fail("state '" + path + "' already exists");
                    return;
                }
                float[] pos;
                AnimatorState state = ValueCodec.Floats(Json.Get(op, "Position"), 2, out pos)
                    ? owner.AddState(leaf, new Vector3(pos[0], pos[1], 0))
                    : owner.AddState(leaf);
                SetStateFields(state, owner, op);
                log.Info("added state " + path);
            }

            void SetStateFields(AnimatorState state, AnimatorStateMachine owner, JToken op)
            {
                Undo.RecordObject(state, "State");
                double d;
                if (Json.Get(op, "Motion") != null)
                {
                    Motion motion;
                    if (!ResolveMotion(Json.Get(op, "Motion"), out motion))
                    {
                        return;
                    }
                    // A blend tree the state owned lives inside the controller file, drop it rather than leave it orphaned.
                    var oldTree = state.motion as BlendTree;
                    state.motion = motion;
                    if (oldTree != null && oldTree != motion && !AssetDatabase.IsMainAsset(oldTree)
                        && AssetDatabase.GetAssetPath(oldTree) == AssetDatabase.GetAssetPath(controller) && !UsedElsewhere(oldTree, state))
                    {
                        DestroyTree(oldTree);
                    }
                }
                if (Number(op, "Speed", out d))
                {
                    state.speed = (float)d;
                }
                if (Json.Has(op, "Tag"))
                {
                    state.tag = Json.Str(op, "Tag");
                }
                if (Json.Has(op, "WriteDefaultValues"))
                {
                    state.writeDefaultValues = Json.Bool(op, "WriteDefaultValues", true);
                }
                if (Json.Get(op, "SpeedParameter") != null)
                {
                    string p = Json.Str(op, "SpeedParameter");
                    state.speedParameterActive = !string.IsNullOrEmpty(p);
                    state.speedParameter = p ?? string.Empty;
                }
                if (Json.Get(op, "TimeParameter") != null)
                {
                    string p = Json.Str(op, "TimeParameter");
                    state.timeParameterActive = !string.IsNullOrEmpty(p);
                    state.timeParameter = p ?? string.Empty;
                }
                if (Json.Has(op, "Mirror"))
                {
                    state.mirror = Json.Bool(op, "Mirror", false);
                }
                if (Json.Get(op, "MirrorParameter") != null)
                {
                    string p = Json.Str(op, "MirrorParameter");
                    state.mirrorParameterActive = !string.IsNullOrEmpty(p);
                    state.mirrorParameter = p ?? string.Empty;
                }
                if (Number(op, "CycleOffset", out d))
                {
                    state.cycleOffset = (float)d;
                }
                if (Json.Get(op, "CycleOffsetParameter") != null)
                {
                    string p = Json.Str(op, "CycleOffsetParameter");
                    state.cycleOffsetParameterActive = !string.IsNullOrEmpty(p);
                    state.cycleOffsetParameter = p ?? string.Empty;
                }
                if (Json.Has(op, "IKOnFeet"))
                {
                    state.iKOnFeet = Json.Bool(op, "IKOnFeet", false);
                }
                if (Json.Has(op, "NewName"))
                {
                    string newName = Json.Str(op, "NewName");
                    if (owner.states.Any(c => c.state != state && c.state.name == newName))
                    {
                        Fail("a state named '" + newName + "' already exists there");
                        return;
                    }
                    state.name = newName;
                }
                if (Json.Bool(op, "Default", false))
                {
                    Undo.RecordObject(owner, "Default state");
                    owner.defaultState = state;
                }
                var properties = Json.Get(op, "Properties") as JObject;
                if (properties != null)
                {
                    var wc = new WriteContext { UseUndo = true };
                    if (!PropertyIO.Write(state, properties, null, wc))
                    {
                        Fail(wc.Errors[0]);
                    }
                }
            }

            // Another state of any layer still playing this tree.
            bool UsedElsewhere(BlendTree tree, AnimatorState except)
            {
                var states = new Dictionary<AnimatorState, string>();
                foreach (AnimatorControllerLayer layer in controller.layers)
                {
                    if (layer.stateMachine != null)
                    {
                        AnimatorControllerExportCommand.IndexPaths(layer.stateMachine, string.Empty, states, new Dictionary<AnimatorStateMachine, string>());
                    }
                }
                return states.Keys.Any(s => s != except && s.motion == tree);
            }

            static void DestroyTree(BlendTree tree)
            {
                foreach (ChildMotion child in tree.children)
                {
                    var sub = child.motion as BlendTree;
                    if (sub != null && !AssetDatabase.IsMainAsset(sub))
                    {
                        DestroyTree(sub);
                    }
                }
                Undo.DestroyObjectImmediate(tree);
            }

            // A clip path, {"Asset", "Sub"} for a clip inside a model, null, or a blend tree block as exported.
            bool ResolveMotion(JToken value, out Motion motion)
            {
                motion = null;
                if (value.Type == JTokenType.Null)
                {
                    return true;
                }
                if (value is JObject && Json.Has(value, "BlendTree"))
                {
                    BlendTree tree;
                    if (!BuildTree(value, out tree))
                    {
                        return false;
                    }
                    motion = tree;
                    return true;
                }
                string error;
                motion = ObjectRefs.Resolve(value, typeof(Motion), RefScope.None, out error) as Motion;
                if (error != null)
                {
                    Fail("Motion: " + error);
                    return false;
                }
                return true;
            }

            bool BuildTree(JToken block, out BlendTree tree)
            {
                tree = null;
                if (!Keys(block, new[] { "BlendTree", "BlendType", "Parameter", "ParameterY", "Children" }))
                {
                    return false;
                }
                BlendTreeType type;
                if (!Enum.TryParse(Json.Str(block, "BlendType", "Simple1D"), true, out type))
                {
                    Fail("BlendType is one of " + string.Join(", ", Enum.GetNames(typeof(BlendTreeType))));
                    return false;
                }
                var children = Json.Get(block, "Children") as JArray;
                if (children == null)
                {
                    Fail("a blend tree needs \"Children\"");
                    return false;
                }
                var t = new BlendTree
                {
                    name = Json.Str(block, "BlendTree", "Blend Tree"),
                    blendType = type,
                    blendParameter = Json.Str(block, "Parameter", string.Empty),
                    blendParameterY = Json.Str(block, "ParameterY", string.Empty),
                    hideFlags = HideFlags.HideInHierarchy,
                };
                AssetDatabase.AddObjectToAsset(t, controller);
                Undo.RegisterCreatedObjectUndo(t, "Blend tree");
                var list = new List<ChildMotion>();
                foreach (JToken c in children)
                {
                    if (!Keys(c, new[] { "Motion", "Threshold", "Position", "DirectParameter", "TimeScale", "Mirror" }))
                    {
                        return false;
                    }
                    Motion m;
                    if (!ResolveMotion(Json.Get(c, "Motion") ?? JValue.CreateNull(), out m))
                    {
                        return false;
                    }
                    var child = new ChildMotion { motion = m, timeScale = 1f };
                    double d;
                    if (Number(c, "TimeScale", out d))
                    {
                        child.timeScale = (float)d;
                    }
                    if (Number(c, "Threshold", out d))
                    {
                        child.threshold = (float)d;
                    }
                    float[] pos;
                    if (ValueCodec.Floats(Json.Get(c, "Position"), 2, out pos))
                    {
                        child.position = new Vector2(pos[0], pos[1]);
                    }
                    child.directBlendParameter = Json.Str(c, "DirectParameter", string.Empty);
                    child.mirror = Json.Bool(c, "Mirror", false);
                    list.Add(child);
                }
                t.useAutomaticThresholds = false;
                t.children = list.ToArray();
                tree = t;
                return true;
            }

            // ---------------------------------------------------------- transitions

            // "From": a state path, "AnyState", or "Entry". "To": a state path, or "Exit".
            // AnyState and Entry belong to a state machine: "StateMachine" names it, the layer's top one by default.
            void AddTransition(JToken op)
            {
                string from = Json.Str(op, "From");
                string to = Json.Str(op, "To");
                if (from == null || to == null)
                {
                    Fail("needs \"From\" and \"To\"");
                    return;
                }
                AnimatorStateMachine root = LayerMachine(op);
                if (root == null)
                {
                    return;
                }
                AnimatorTransitionBase t;
                if (from == "AnyState" || from == "Entry")
                {
                    AnimatorStateMachine sm = FindMachine(root, Json.Str(op, "StateMachine"));
                    if (sm == null)
                    {
                        return;
                    }
                    if (to == "Exit")
                    {
                        Fail(from + " cannot transition to Exit");
                        return;
                    }
                    AnimatorState dest = StateAt(root, to);
                    if (dest == null)
                    {
                        return;
                    }
                    t = from == "AnyState" ? (AnimatorTransitionBase)sm.AddAnyStateTransition(dest) : sm.AddEntryTransition(dest);
                }
                else
                {
                    AnimatorState source = SourceState(op, from);
                    if (source == null)
                    {
                        return;
                    }
                    if (to == "Exit")
                    {
                        t = source.AddExitTransition();
                    }
                    else
                    {
                        AnimatorState dest = StateAt(root, to);
                        if (dest == null)
                        {
                            return;
                        }
                        t = source.AddTransition(dest);
                    }
                }
                SetTransitionFields(t, op);
                log.Info("added transition " + from + " -> " + to);
            }

            AnimatorState StateAt(AnimatorStateMachine root, string path)
            {
                string machinePath, leaf;
                SplitPath(path, out machinePath, out leaf);
                AnimatorStateMachine sm = FindMachine(root, machinePath);
                if (sm == null)
                {
                    return null;
                }
                AnimatorState s = sm.states.Select(c => c.state).FirstOrDefault(x => x != null && x.name == leaf);
                if (s == null)
                {
                    Fail("no state '" + path + "'");
                }
                return s;
            }

            // The state a transition op's "From" names, in the op's layer.
            AnimatorState SourceState(JToken op, string from)
            {
                AnimatorStateMachine owner;
                return FindState(new JObject { ["Layer"] = Json.Get(op, "Layer"), ["State"] = from }, "State", out owner);
            }

            List<AnimatorTransitionBase> Candidates(JToken op, out Action<AnimatorTransitionBase> remove)
            {
                remove = null;
                string from = Json.Str(op, "From");
                string to = Json.Str(op, "To");
                AnimatorStateMachine root = LayerMachine(op);
                if (root == null || from == null || to == null)
                {
                    if (root != null)
                    {
                        Fail("needs \"From\" and \"To\"");
                    }
                    return null;
                }
                IEnumerable<AnimatorTransitionBase> all;
                if (from == "AnyState" || from == "Entry")
                {
                    AnimatorStateMachine sm = FindMachine(root, Json.Str(op, "StateMachine"));
                    if (sm == null)
                    {
                        return null;
                    }
                    if (from == "AnyState")
                    {
                        all = sm.anyStateTransitions;
                        remove = t => sm.RemoveAnyStateTransition((AnimatorStateTransition)t);
                    }
                    else
                    {
                        all = sm.entryTransitions;
                        remove = t => sm.RemoveEntryTransition((AnimatorTransition)t);
                    }
                }
                else
                {
                    AnimatorState source = SourceState(op, from);
                    if (source == null)
                    {
                        return null;
                    }
                    all = source.transitions;
                    remove = t => source.RemoveTransition((AnimatorStateTransition)t);
                }
                // Match the destination by its full path, two sub-state machines may hold states of the same name.
                var paths = new Dictionary<AnimatorState, string>();
                AnimatorControllerExportCommand.IndexPaths(root, string.Empty, paths, new Dictionary<AnimatorStateMachine, string>());
                return all.Where(t =>
                {
                    if (to == "Exit")
                    {
                        return t.isExit;
                    }
                    string path;
                    return t.destinationState != null && paths.TryGetValue(t.destinationState, out path) && path == to;
                }).ToList();
            }

            AnimatorTransitionBase PickOne(JToken op, List<AnimatorTransitionBase> candidates)
            {
                if (candidates == null)
                {
                    return null;
                }
                int index = Json.Int(op, "Index", -1);
                if (candidates.Count == 0)
                {
                    Fail("no transition " + Json.Str(op, "From") + " -> " + Json.Str(op, "To"));
                    return null;
                }
                if (index < 0 && candidates.Count > 1)
                {
                    Fail(candidates.Count + " transitions match, add \"Index\" (0 to " + (candidates.Count - 1) + ")");
                    return null;
                }
                index = Math.Max(index, 0);
                if (index >= candidates.Count)
                {
                    Fail("Index " + index + " is out of range, " + candidates.Count + " match");
                    return null;
                }
                return candidates[index];
            }

            AnimatorTransitionBase FindTransition(JToken op)
            {
                Action<AnimatorTransitionBase> remove;
                return PickOne(op, Candidates(op, out remove));
            }

            void RemoveTransition(JToken op)
            {
                Action<AnimatorTransitionBase> remove;
                List<AnimatorTransitionBase> candidates = Candidates(op, out remove);
                AnimatorTransitionBase t = PickOne(op, candidates);
                if (t != null)
                {
                    remove(t);
                    log.Info("removed transition " + Json.Str(op, "From") + " -> " + Json.Str(op, "To"));
                }
            }

            void SetTransitionFields(AnimatorTransitionBase t, JToken op)
            {
                Undo.RecordObject(t, "Transition");
                double d;
                if (Json.Has(op, "Name"))
                {
                    t.name = Json.Str(op, "Name");
                }
                if (Json.Has(op, "Solo"))
                {
                    t.solo = Json.Bool(op, "Solo", false);
                }
                if (Json.Has(op, "Mute"))
                {
                    t.mute = Json.Bool(op, "Mute", false);
                }
                var st = t as AnimatorStateTransition;
                if (st != null)
                {
                    if (Json.Has(op, "HasExitTime"))
                    {
                        st.hasExitTime = Json.Bool(op, "HasExitTime", false);
                    }
                    if (Number(op, "ExitTime", out d))
                    {
                        st.exitTime = (float)d;
                    }
                    if (Number(op, "Duration", out d))
                    {
                        st.duration = (float)d;
                    }
                    if (Json.Has(op, "FixedDuration"))
                    {
                        st.hasFixedDuration = Json.Bool(op, "FixedDuration", true);
                    }
                    if (Number(op, "Offset", out d))
                    {
                        st.offset = (float)d;
                    }
                    if (Json.Has(op, "Interruption"))
                    {
                        TransitionInterruptionSource source;
                        if (!Enum.TryParse(Json.Str(op, "Interruption"), true, out source))
                        {
                            Fail("Interruption is one of " + string.Join(", ", Enum.GetNames(typeof(TransitionInterruptionSource))));
                            return;
                        }
                        st.interruptionSource = source;
                    }
                    if (Json.Has(op, "OrderedInterruption"))
                    {
                        st.orderedInterruption = Json.Bool(op, "OrderedInterruption", true);
                    }
                    if (Json.Has(op, "CanTransitionToSelf"))
                    {
                        st.canTransitionToSelf = Json.Bool(op, "CanTransitionToSelf", false);
                    }
                }
                var conditions = Json.Get(op, "Conditions") as JArray;
                if (conditions != null)
                {
                    var list = new List<AnimatorCondition>();
                    foreach (JToken c in conditions)
                    {
                        if (!Keys(c, new[] { "Parameter", "Mode", "Threshold" }))
                        {
                            return;
                        }
                        string parameter = Json.Str(c, "Parameter");
                        AnimatorControllerParameter p = controller.parameters.FirstOrDefault(x => x.name == parameter);
                        if (p == null)
                        {
                            Fail("condition on unknown parameter '" + parameter + "'");
                            return;
                        }
                        AnimatorConditionMode mode;
                        string modeText = Json.Str(c, "Mode", p.type == AnimatorControllerParameterType.Bool || p.type == AnimatorControllerParameterType.Trigger ? "If" : "Greater");
                        if (!Enum.TryParse(modeText, true, out mode))
                        {
                            Fail("condition Mode is one of " + string.Join(", ", Enum.GetNames(typeof(AnimatorConditionMode))));
                            return;
                        }
                        bool numeric = p.type == AnimatorControllerParameterType.Float || p.type == AnimatorControllerParameterType.Int;
                        bool numericMode = mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less || mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
                        if (numeric != numericMode || (p.type == AnimatorControllerParameterType.Float && (mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual)))
                        {
                            Fail("Mode " + mode + " does not fit the " + p.type + " parameter '" + parameter + "'");
                            return;
                        }
                        int before = Errors.Count;
                        Number(c, "Threshold", out d);
                        if (Errors.Count > before)
                        {
                            return;
                        }
                        list.Add(new AnimatorCondition { parameter = parameter, mode = mode, threshold = (float)d });
                    }
                    t.conditions = list.ToArray();
                }
            }
        }
    }
}
