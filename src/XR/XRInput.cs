using Silk.NET.OpenXR;
using SLQuest.Core;

namespace SLQuest.XR
{
    public enum Hand { Left, Right }

    public readonly record struct ControllerState(
        Vector3    GripPosition,
        Quaternion GripRotation,
        Vector3    AimPosition,
        Quaternion AimRotation,
        float      TriggerValue,
        float      GripValue,
        Vector2    ThumbstickAxis,
        bool       ButtonA,
        bool       ButtonB,
        bool       Menu,
        bool       ThumbstickClick
    );

    /// <summary>
    /// OpenXR action-based input for both controllers and hand tracking.
    /// Actions are created once and queried every frame.
    /// </summary>
    public sealed unsafe class XRInput : IDisposable
    {
        private readonly XRSession _xr;
        private ActionSet _actionSet;

        // Actions
        private Action _triggerAction, _gripAction, _thumbstickAction;
        private Action _buttonAAction, _buttonBAction, _menuAction, _thumbstickClickAction;
        private Action _poseGripAction, _poseAimAction;

        // Spaces for pose actions
        private Space _leftGripSpace, _leftAimSpace;
        private Space _rightGripSpace, _rightAimSpace;

        private bool _disposed;

        public XRInput(XRSession xr)
        {
            _xr = xr;
            CreateActions();
            SuggestBindings();
            AttachActions();
        }

        private void CreateActions()
        {
            fixed (byte* name = "slquest_actions\0"u8)
            fixed (byte* local = "SLQuest Actions\0"u8)
            {
                var asInfo = new ActionSetCreateInfo
                {
                    Type     = StructureType.ActionSetCreateInfo,
                    Priority = 0,
                };
                Buffer.MemoryCopy(name,  asInfo.ActionSetName,   64, 16);
                Buffer.MemoryCopy(local, asInfo.LocalizedActionSetName, 128, 16);

                ActionSet set;
                _xr.XrApi.CreateActionSet(_xr.XrInstance, &asInfo, &set);
                _actionSet = set;
            }

            _triggerAction       = CreateAction("trigger",        ActionType.FloatInput);
            _gripAction          = CreateAction("grip",           ActionType.FloatInput);
            _thumbstickAction    = CreateAction("thumbstick",     ActionType.Vector2fInput);
            _buttonAAction       = CreateAction("button_a",       ActionType.BooleanInput);
            _buttonBAction       = CreateAction("button_b",       ActionType.BooleanInput);
            _menuAction          = CreateAction("menu",           ActionType.BooleanInput);
            _thumbstickClickAction = CreateAction("thumbstick_click", ActionType.BooleanInput);
            _poseGripAction      = CreateAction("pose_grip",      ActionType.PoseInput);
            _poseAimAction       = CreateAction("pose_aim",       ActionType.PoseInput);
        }

        private Action CreateAction(string name, ActionType type)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* nb = nameBytes)
            {
                var info = new ActionCreateInfo
                {
                    Type            = StructureType.ActionCreateInfo,
                    ActionType      = type,
                    CountSubactionPaths = 0,
                };
                Buffer.MemoryCopy(nb, info.ActionName, 64, nameBytes.Length);
                Buffer.MemoryCopy(nb, info.LocalizedActionName, 128, nameBytes.Length);

                Silk.NET.OpenXR.Action action;
                _xr.XrApi.CreateAction(_actionSet, &info, &action);
                return action;
            }
        }

        private void SuggestBindings()
        {
            // Suggest bindings for Meta Quest Touch controllers
            const string PROFILE = "/interaction_profiles/oculus/touch_controller";
            var profilePath = GetPath(PROFILE);

            var bindings = new SuggestedActionBinding[]
            {
                Bind(_triggerAction,         "/user/hand/left/input/trigger/value"),
                Bind(_triggerAction,         "/user/hand/right/input/trigger/value"),
                Bind(_gripAction,            "/user/hand/left/input/squeeze/value"),
                Bind(_gripAction,            "/user/hand/right/input/squeeze/value"),
                Bind(_thumbstickAction,      "/user/hand/left/input/thumbstick"),
                Bind(_thumbstickAction,      "/user/hand/right/input/thumbstick"),
                Bind(_buttonAAction,         "/user/hand/right/input/a/click"),
                Bind(_buttonBAction,         "/user/hand/right/input/b/click"),
                Bind(_buttonAAction,         "/user/hand/left/input/x/click"),
                Bind(_buttonBAction,         "/user/hand/left/input/y/click"),
                Bind(_menuAction,            "/user/hand/left/input/menu/click"),
                Bind(_thumbstickClickAction, "/user/hand/left/input/thumbstick/click"),
                Bind(_thumbstickClickAction, "/user/hand/right/input/thumbstick/click"),
                Bind(_poseGripAction,        "/user/hand/left/input/grip/pose"),
                Bind(_poseGripAction,        "/user/hand/right/input/grip/pose"),
                Bind(_poseAimAction,         "/user/hand/left/input/aim/pose"),
                Bind(_poseAimAction,         "/user/hand/right/input/aim/pose"),
            };

            fixed (SuggestedActionBinding* bp = bindings)
            {
                var suggest = new InteractionProfileSuggestedBinding
                {
                    Type                     = StructureType.InteractionProfileSuggestedBinding,
                    InteractionProfile       = profilePath,
                    CountSuggestedBindings   = (uint)bindings.Length,
                    SuggestedBindings        = bp,
                };
                _xr.XrApi.SuggestInteractionProfileBinding(_xr.XrInstance, &suggest);
            }

            // Create spaces for grip/aim poses
            _leftGripSpace  = CreateActionSpace(_poseGripAction, "/user/hand/left");
            _leftAimSpace   = CreateActionSpace(_poseAimAction,  "/user/hand/left");
            _rightGripSpace = CreateActionSpace(_poseGripAction, "/user/hand/right");
            _rightAimSpace  = CreateActionSpace(_poseAimAction,  "/user/hand/right");
        }

        private SuggestedActionBinding Bind(Action action, string path) => new()
        {
            Action  = action,
            Binding = GetPath(path),
        };

        private Space CreateActionSpace(Action action, string subPath)
        {
            var info = new ActionSpaceCreateInfo
            {
                Type             = StructureType.ActionSpaceCreateInfo,
                Action           = action,
                SubactionPath    = GetPath(subPath),
                PoseInActionSpace = new Posef
                {
                    Orientation = new Quaternionf { W = 1 },
                },
            };
            Space space;
            _xr.XrApi.CreateActionSpace(_xr.XrSessionHandle, &info, &space);
            return space;
        }

        private void AttachActions()
        {
            var set = _actionSet;
            var info = new SessionActionSetsAttachInfo
            {
                Type           = StructureType.SessionActionSetsAttachInfo,
                CountActionSets = 1,
                ActionSets     = &set,
            };
            _xr.XrApi.AttachSessionActionSets(_xr.XrSessionHandle, &info);
        }

        // ── Query per frame ───────────────────────────────────────────────────

        public void SyncActions()
        {
            var set = _actionSet;
            var syncInfo = new ActionsSyncInfo
            {
                Type           = StructureType.ActionsSyncInfo,
                CountActiveActionSets = 1,
                ActiveActionSets = new ActiveActionSet { ActionSet = set },
            };
            // Note: SyncActions takes a pointer to ActionsSyncInfo
            _xr.XrApi.SyncActions(_xr.XrSessionHandle, &syncInfo);
        }

        public ControllerState GetController(Hand hand)
        {
            var subPath = hand == Hand.Left ? "/user/hand/left" : "/user/hand/right";
            var sub = GetPath(subPath);

            return new ControllerState(
                GripPosition    = GetPosePosition(hand == Hand.Left ? _leftGripSpace : _rightGripSpace),
                GripRotation    = GetPoseRotation(hand == Hand.Left ? _leftGripSpace : _rightGripSpace),
                AimPosition     = GetPosePosition(hand == Hand.Left ? _leftAimSpace  : _rightAimSpace),
                AimRotation     = GetPoseRotation(hand == Hand.Left ? _leftAimSpace  : _rightAimSpace),
                TriggerValue    = GetFloat(_triggerAction, sub),
                GripValue       = GetFloat(_gripAction,   sub),
                ThumbstickAxis  = GetVector2(_thumbstickAction, sub),
                ButtonA         = GetBool(_buttonAAction, GetPath("/user/hand/right")),
                ButtonB         = GetBool(_buttonBAction, GetPath("/user/hand/right")),
                Menu            = GetBool(_menuAction,    GetPath("/user/hand/left")),
                ThumbstickClick = GetBool(_thumbstickClickAction, sub)
            );
        }

        private Vector3 GetPosePosition(Space space)
        {
            var info = new SpaceLocation { Type = StructureType.SpaceLocation };
            _xr.XrApi.LocateSpace(space, _xr.LocalSpace, _xr.XrApi.GetCurrentFrameTime(), &info);
            return new Vector3(info.Pose.Position.X, info.Pose.Position.Y, info.Pose.Position.Z);
        }

        private Quaternion GetPoseRotation(Space space)
        {
            var info = new SpaceLocation { Type = StructureType.SpaceLocation };
            _xr.XrApi.LocateSpace(space, _xr.LocalSpace, _xr.XrApi.GetCurrentFrameTime(), &info);
            var q = info.Pose.Orientation;
            return new Quaternion(q.X, q.Y, q.Z, q.W);
        }

        private float GetFloat(Action action, ulong subPath)
        {
            var state = new ActionStateFloat { Type = StructureType.ActionStateFloat };
            var info  = new ActionStateGetInfo
            {
                Type         = StructureType.ActionStateGetInfo,
                Action       = action,
                SubactionPath = subPath,
            };
            _xr.XrApi.GetActionStateFloat(_xr.XrSessionHandle, &info, &state);
            return state.CurrentState;
        }

        private Vector2 GetVector2(Action action, ulong subPath)
        {
            var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2F };
            var info  = new ActionStateGetInfo
            {
                Type          = StructureType.ActionStateGetInfo,
                Action        = action,
                SubactionPath = subPath,
            };
            _xr.XrApi.GetActionStateVector2(_xr.XrSessionHandle, &info, &state);
            return new Vector2(state.CurrentState.X, state.CurrentState.Y);
        }

        private bool GetBool(Action action, ulong subPath)
        {
            var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
            var info  = new ActionStateGetInfo
            {
                Type          = StructureType.ActionStateGetInfo,
                Action        = action,
                SubactionPath = subPath,
            };
            _xr.XrApi.GetActionStateBoolean(_xr.XrSessionHandle, &info, &state);
            return state.CurrentState != 0;
        }

        private ulong GetPath(string str)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(str + "\0");
            ulong path;
            fixed (byte* p = bytes)
                _xr.XrApi.StringToPath(_xr.XrInstance, p, &path);
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var sp in new[] { _leftGripSpace, _leftAimSpace, _rightGripSpace, _rightAimSpace })
                if (sp.Handle != 0) _xr.XrApi.DestroySpace(sp);
            if (_actionSet.Handle != 0) _xr.XrApi.DestroyActionSet(_actionSet);
        }
    }
}
