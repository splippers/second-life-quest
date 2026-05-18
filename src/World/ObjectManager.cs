using OpenMetaverse;
using System.Collections.Concurrent;
using SLQuest.Core;
using SLQuest.Network;
using OMPrimType = OpenMetaverse.PrimType;

namespace SLQuest.World
{
    public enum SLPrimShape { Box, Cylinder, Sphere, Torus, Mesh, Other }

    public sealed class SLObject
    {
        public uint       LocalId        { get; set; }
        public Guid       FullId         { get; set; }
        public Vector3    Position       { get; set; }
        public Quaternion Rotation       { get; set; } = Quaternion.Identity;
        public Vector3    Scale          { get; set; } = Vector3.One;
        public SLPrimShape Shape         { get; set; }
        public bool       IsAvatar       { get; set; }
        public Guid       TextureId      { get; set; }
        public string     HoverText      { get; set; } = string.Empty;
        public Vector3    HoverTextColor { get; set; } = Vector3.One;  // RGB, each 0..1
    }

    public sealed class ObjectManager
    {
        private readonly ConcurrentDictionary<uint, SLObject> _objects = new();

        public IReadOnlyDictionary<uint, SLObject> Objects => _objects;

        public ObjectManager(SLNetworkManager net, RegionManager region, Assets.AssetManager assets)
        {
            var omObjects = net.Client.Objects;
            omObjects.ObjectUpdate += OnPrimUpdate;
            omObjects.KillObject   += OnKillObject;
            omObjects.AvatarUpdate += OnAvatarUpdate;

            // Clear state when we cross a region boundary
            EventBus.Subscribe<SimConnectedEvent>(_ => _objects.Clear());
        }

        private void OnPrimUpdate(object? sender, PrimEventArgs e)
        {
            var prim = e.Prim;
            var obj  = _objects.GetOrAdd(prim.LocalID, id => new SLObject { LocalId = id });

            obj.FullId    = prim.ID.Guid;
            obj.Position  = MathEx.SLToWorld(prim.Position);
            obj.Rotation  = MathEx.SLToWorld(prim.Rotation);
            // SL prim Scale is in SL coords (no axis swap needed for scale)
            obj.Scale     = new Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);
            obj.Shape     = ToShape(prim.Type);
            obj.IsAvatar  = false;
            obj.TextureId = prim.Textures?.DefaultTexture?.TextureID.Guid ?? Guid.Empty;

            // Hover text (llSetText) — empty string means no label
            obj.HoverText = prim.Text ?? string.Empty;
            if (prim.TextColor is { } tc)
                obj.HoverTextColor = new Vector3(tc.R, tc.G, tc.B);

            EventBus.Publish(new ObjectUpdateEvent(prim.LocalID, prim.ID.Guid,
                obj.Position, obj.Rotation, obj.Scale));
        }

        private void OnAvatarUpdate(object? sender, AvatarUpdateEventArgs e)
        {
            var av  = e.Avatar;
            var obj = _objects.GetOrAdd(av.LocalID, id => new SLObject { LocalId = id });

            obj.FullId   = av.ID.Guid;
            obj.Position = MathEx.SLToWorld(av.Position);
            obj.Rotation = MathEx.SLToWorld(av.Rotation);
            obj.Scale    = new Vector3(0.5f, 1.85f, 0.5f); // avatar capsule approximation
            obj.Shape    = SLPrimShape.Cylinder;
            obj.IsAvatar = true;

            EventBus.Publish(new AvatarUpdateEvent(av.ID.Guid, obj.Position, obj.Rotation));
        }

        private void OnKillObject(object? sender, KillObjectEventArgs e)
        {
            _objects.TryRemove(e.ObjectLocalID, out _);
            EventBus.Publish(new ObjectRemovedEvent(e.ObjectLocalID));
        }

        private static SLPrimShape ToShape(OMPrimType t) => t switch
        {
            OMPrimType.Box      => SLPrimShape.Box,
            OMPrimType.Cylinder => SLPrimShape.Cylinder,
            OMPrimType.Sphere   => SLPrimShape.Sphere,
            OMPrimType.Torus    => SLPrimShape.Torus,
            OMPrimType.Mesh     => SLPrimShape.Mesh,
            _                   => SLPrimShape.Other,
        };
    }
}
