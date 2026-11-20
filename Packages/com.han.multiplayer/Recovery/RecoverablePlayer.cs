// 玩家快照在服务器保存；新的 NetworkObject 绑定原逻辑身份，不能假设 NGO 的对象/连接 ID 不变。
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Han.Multiplayer.Recovery
{
    public interface IPlayerRecoveryState
    {
        string RecoveryKey { get; }
        byte[] CaptureRecoveryState();
        void RestoreRecoveryState(byte[] state);
    }

    public sealed class PlayerRecoverySnapshot
    {
        public Vector3 Position, Scale;
        public Quaternion Rotation;
        public readonly Dictionary<string, byte[]> Parts = new Dictionary<string, byte[]>();
    }

    [RequireComponent(typeof(NetworkObject))]
    public sealed class RecoverablePlayer : NetworkBehaviour
    {
        public readonly NetworkVariable<FixedString64Bytes> SessionId = new NetworkVariable<FixedString64Bytes>();
        RecoveryService service;
        protected override void OnNetworkPostSpawn()
        {
            service = NetworkManager.GetComponent<RecoveryService>();
            if (service == null) return;
            // PostSpawn 在其他玩法 OnNetworkSpawn 之后执行，避免出生点覆盖恢复位置。
            if (IsServer) service.BindPlayer(this);
            if (IsOwner) service.LocalPlayerSpawned();
        }
        public override void OnNetworkDespawn()
        {
            if (service == null) return;
            if (IsServer && !NetworkManager.ShutdownInProgress) service.CapturePlayer(this);
            if (IsOwner) service.LocalPlayerDespawned();
        }
        internal PlayerRecoverySnapshot Capture()
        {
            var snapshot = new PlayerRecoverySnapshot { Position = transform.position, Rotation = transform.rotation, Scale = transform.localScale };
            int total = 0;
            foreach (var component in GetComponents<MonoBehaviour>())
                if (component is IPlayerRecoveryState provider)
                {
                    string key = provider.RecoveryKey;
                    var data = provider.CaptureRecoveryState();
                    if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || data == null || data.Length > 16384 ||
                        snapshot.Parts.Count >= 32 || total + data.Length > 65536 || snapshot.Parts.ContainsKey(key))
                        throw new InvalidOperationException("Invalid or oversized player recovery snapshot.");
                    total += data.Length;
                    snapshot.Parts.Add(key, (byte[])data.Clone());
                }
            return snapshot;
        }
        internal void Restore(PlayerRecoverySnapshot snapshot)
        {
            int restoredParts = 0;
            foreach (var component in GetComponents<MonoBehaviour>())
                if (component is IPlayerRecoveryState provider)
                {
                    if (!snapshot.Parts.TryGetValue(provider.RecoveryKey, out var data)) throw new InvalidOperationException("Recovery state schema does not match.");
                    provider.RestoreRecoveryState((byte[])data.Clone());
                    restoredParts++;
                }
            if (restoredParts != snapshot.Parts.Count) throw new InvalidOperationException("Recovery state schema does not match.");
            var synchronized = GetComponent<NetworkTransform>();
            if (synchronized != null) synchronized.Teleport(snapshot.Position, snapshot.Rotation, snapshot.Scale);
            else { transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation); transform.localScale = snapshot.Scale; }
        }
    }
}
