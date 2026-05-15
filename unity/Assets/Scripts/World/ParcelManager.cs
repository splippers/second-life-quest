using System;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.Avatar;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Tracks which parcel the local avatar is standing on.
    ///
    /// Polls avatar position every <see cref="checkIntervalSec"/> seconds and
    /// requests fresh parcel data whenever the parcel changes.  Publishes
    /// <see cref="ParcelChangedEvent"/> so any UI element can react.
    ///
    /// Also enforces parcel flags:
    ///   AllowFly      — disables flight when false
    ///   AllowPush     — zeroes external push impulses when false (server-side
    ///                   enforcement, but we can also clamp locally)
    ///   RestrictPushing, NoFly already handled by the sim; we mirror them
    ///                   into LocalAvatar so the VR movement system respects them.
    /// </summary>
    public sealed class ParcelManager : MonoBehaviour
    {
        [SerializeField] private float checkIntervalSec = 2f;

        public Parcel CurrentParcel { get; private set; }

        private SLNetworkManager _net;
        private LocalAvatar      _local;
        private float            _timer;
        private Vector3          _lastPos = Vector3.positiveInfinity;
        private int              _lastLocalId = -1;

        public event Action<Parcel> OnParcelChanged;

        private void Awake()
        {
            _net   = SLApplication.Instance?.Network     ?? FindObjectOfType<SLNetworkManager>();
            _local = SLApplication.Instance?.LocalAvatar ?? FindObjectOfType<LocalAvatar>();
        }

        private void OnEnable()
        {
            if (_net?.Client?.Parcels != null)
                _net.Client.Parcels.ParcelProperties += OnParcelProperties;
        }

        private void OnDisable()
        {
            if (_net?.Client?.Parcels != null)
                _net.Client.Parcels.ParcelProperties -= OnParcelProperties;
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < checkIntervalSec) return;
            _timer = 0f;

            var sim = _net?.Client?.Network?.CurrentSim;
            if (sim == null || _local == null) return;

            Vector3 pos = _local.transform.position;
            if (Vector3.SqrMagnitude(pos - _lastPos) < 4f) return; // < 2 m movement
            _lastPos = pos;

            // SL parcel coordinates are in region-local metres (same axes as SL, which
            // we map 1:1 to Unity X/Z since the region is our scene origin).
            _net.Client.Parcels.RequestParcelProperties(sim,
                (int)pos.x, (int)pos.z,      // SL x, y (Unity x, z)
                (int)(pos.x + 1), (int)(pos.z + 1),
                0, false);
        }

        private void OnParcelProperties(object sender, ParcelPropertiesEventArgs e)
        {
            if (e.Result != ParcelResult.Single && e.Result != ParcelResult.NoData) return;

            var parcel = e.Parcel;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (parcel.LocalID == _lastLocalId) return;
                _lastLocalId   = parcel.LocalID;
                CurrentParcel  = parcel;

                ApplyParcelFlags(parcel);
                OnParcelChanged?.Invoke(parcel);
                EventBus.Publish(new ParcelChangedEvent(parcel));
            });
        }

        private void ApplyParcelFlags(Parcel parcel)
        {
            if (_local == null) return;

            bool canFly = (parcel.Flags & ParcelFlags.AllowFly) != 0;
            _local.SetFlightAllowed(canFly);
        }
    }
}
