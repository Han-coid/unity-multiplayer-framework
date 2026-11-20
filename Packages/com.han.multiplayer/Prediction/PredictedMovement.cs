// 输入驱动的服务器权威移动：客户端预测/回放只改变展示，NetworkTransform 根对象始终由服务器写。
using System;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Han.Multiplayer.Prediction
{
    [DefaultExecutionOrder(-100), DisallowMultipleComponent, RequireComponent(typeof(NetworkObject), typeof(NetworkTransform))]
    public sealed class PredictedMovement : NetworkBehaviour
    {
        readonly NetworkVariable<uint> worldSignature = new NetworkVariable<uint>();
        readonly PredictionHistory history = new PredictionHistory();
        readonly ServerMovementQueue inbox = new ServerMovementQueue();
        MultiplayerSession session;
        MovementPredictionSession configuration;
        MovementWorld world;
        NetworkTickSystem ticks;
        IMovementAuthorityPolicy[] policies;
        IMovementPredictionObserver[] observers;
        Vector3 visualOffset;
        MovementState serverState, authority;
        bool jumpPending, hasRecoveryVelocity;
        float recoveryVelocity;
        ushort input;
        double sampledAt, lastLocalTick, lastAckProgress, missingSince, nextResync;
        uint serverEpoch = 1, clientEpoch, serverTick, lastSnapshotTick, signature;
        ulong observedOwner;
        bool initialized, hasSnapshot, waitingRebase, policyWasAllowed, wasLocalOwner;
        double serverRateAt = MovementClock.Now;
        int serverSteps;
        float measuredServerHz, queueMs, workMs;
        public bool IsActive => configuration != null && configuration.UsesCommandMovement;
        public bool LocalPredictionActive => IsActive && IsOwner && !IsServer && configuration.PredictLocally && hasSnapshot && !waitingRebase && !ConfigurationMismatch;
        public bool ConfigurationMismatch { get; private set; }
        public Vector3 PresentationPosition { get; private set; }
        public Vector3 SimulatedPosition => history.Position;
        public MovementState SimulatedState => history.State;
        public MovementState AuthoritativeState => IsServer ? serverState : authority;
        public Vector3 LatestAuthority => authority.Position;
        public bool SupportsJump => IsActive && world != null && world.VerticalEnabled;
        public float LastVelocityCorrection { get; private set; }
        public int PendingInputs => history.Count;
        public int Corrections { get; private set; }
        public int ReplayedInputs { get; private set; }
        public int RebaseCount { get; private set; }
        public float LastCorrectionError { get; private set; }
        public float MaximumCorrectionError { get; private set; }
        public uint Epoch => clientEpoch;
        public uint LastAcknowledged => history.LastAcknowledged;
        public bool WaitingForAuthority => !hasSnapshot || waitingRebase;
        public double AckAgeSeconds => hasSnapshot ? Math.Max(0, MovementClock.Now - lastAckProgress) : 0;

        public override void OnNetworkSpawn()
        {
            session = NetworkManager.GetComponent<MultiplayerSession>(); configuration = NetworkManager.GetComponent<MovementPredictionSession>();
            if (IsServer) worldSignature.Value = IsActive ? configuration.World.Fingerprint(NetworkManager.NetworkConfig.TickRate) : 0;
        }
        protected override void OnNetworkPostSpawn()
        {
            // 先让其他玩法的 OnNetworkSpawn 和恢复组件的 PostSpawn 完成，再读取出生/恢复位置。
            policies = Array.FindAll(GetComponents<MonoBehaviour>(), value => value is IMovementAuthorityPolicy).ConvertAll<IMovementAuthorityPolicy>();
            observers = Array.FindAll(GetComponents<MonoBehaviour>(), value => value is IMovementPredictionObserver).ConvertAll<IMovementPredictionObserver>();
            ConfigurationMismatch = (IsActive ? configuration.World.Fingerprint(NetworkManager.NetworkConfig.TickRate) : 0) != worldSignature.Value;
            PresentationPosition = transform.position;
            if (!IsActive || ConfigurationMismatch) return;
            world = configuration.World; signature = world.Fingerprint(NetworkManager.NetworkConfig.TickRate);
            if (IsServer && !world.IsFree(transform.position)) { ConfigurationMismatch = true; return; }
            // 客户端等待首个权威包；初始 NetworkTransform 可能尚未应用出生/恢复位置。
            serverState = authority = IsServer ? world.CreateState(transform.position, hasRecoveryVelocity ? recoveryVelocity : 0) : new MovementState(transform.position);
            hasRecoveryVelocity = false;
            observedOwner = OwnerClientId; policyWasAllowed = Allowed();
            history.Reset(serverState); sampledAt = lastLocalTick = lastAckProgress = MovementClock.Now;
            initialized = true; ticks = NetworkManager.NetworkTickSystem; ticks.Tick += OnTick;
            if (IsServer) SendState();
        }
        public void SetInput(Vector2 value, double sampleTime)
        { input = PackedMovementInput.Encode(value); sampledAt = sampleTime; }
        /// <summary>在按下事件调用一次，保留到下个 Tick；不在每帧持续按住时调用，也不在空中缓存跳跃。</summary>
        public void RequestJump()
        { if (IsSpawned && IsOwner && SupportsJump && hasSnapshot && !waitingRebase && Allowed()) jumpPending = true; }
        /// <summary>恢复适配器在 PostSpawn 初始化前提交服务器保存的速度；位置由已有恢复组件还原。</summary>
        public void PrepareRecoveryVelocityOnServer(float velocity)
        {
            if (!IsServer || initialized || !PackedMovementInput.Finite(velocity)) throw new InvalidOperationException("Recovery velocity must be prepared on server before movement initialization.");
            hasRecoveryVelocity = true; recoveryVelocity = velocity;
        }
        bool Allowed()
        {
            if (policies == null) return true;
            foreach (var policy in policies) if (!policy.CanSimulateMovement) return false;
            return true;
        }
        void OnTick()
        {
            if (!IsSpawned || !initialized || ConfigurationMismatch) return;
            double now = MovementClock.Now;
            if (IsOwner)
            {
                if (!wasLocalOwner) { hasSnapshot = false; waitingRebase = false; jumpPending = false; history.Reset(transform.position); }
                wasLocalOwner = true;
                if (IsServer && !hasSnapshot) AcceptInitialState(serverEpoch, inbox.LastApplied, serverState);
                if (hasSnapshot && !waitingRebase && Allowed())
                {
                    if (history.Count > 0 && now - lastAckProgress > 1.5) BeginRebase();
                    else if (!history.Append(input, world, 1f / ticks.TickRate, jumpPending)) BeginRebase();
                    else
                    {
                        lastLocalTick = now;
                        foreach (var observer in observers) observer.OnMovementInputSent(history.LastGenerated, sampledAt, now);
                        InputsRpc(clientEpoch, signature, history.BuildBatch());
                    }
                }
                jumpPending = false; // 一个按下事件最多分配一个输入序号；补帧不会重复起跳。
            }
            else wasLocalOwner = false;
            if (!IsServer) return;
            serverTick = unchecked(serverTick + 1);
            bool allowed = Allowed();
            if (observedOwner != OwnerClientId || allowed != policyWasAllowed)
            { observedOwner = OwnerClientId; policyWasAllowed = allowed; ResetServerEpoch(); }
            double began = MovementClock.Now;
            serverSteps++;
            if (began - serverRateAt >= 1)
            { measuredServerHz = (float)(serverSteps / (began - serverRateAt)); serverSteps = 0; serverRateAt = began; }
            if (allowed && inbox.TryConsume(out ushort command, out bool jump, out double received))
            {
                missingSince = 0; serverState = world.Step(serverState, command, jump, 1f / ticks.TickRate);
                queueMs = (float)((began - received) * 1000);
            }
            else if (allowed)
            {
                // 没有输入时只推进重力，不沿用旧移动/跳跃，也不伪造输入确认；客户端会校正完整空中状态。
                serverState = world.Step(serverState, 0, false, 1f / ticks.TickRate); queueMs = 0;
                if (inbox.Count > 0)
                {
                    if (missingSince == 0) missingSince = began;
                    if (began - missingSince > .25) ResetServerEpoch();
                }
            }
            transform.position = serverState.Position;
            workMs = (float)((MovementClock.Now - began) * 1000);
            if (serverTick % Math.Max(1u, (ticks.TickRate + 19) / 20) == 0) SendState();
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner, Delivery = RpcDelivery.Unreliable)]
        void InputsRpc(uint epoch, uint fingerprint, MovementInputBatch batch, RpcParams rpc = default)
        {
            if (!initialized || !IsActive) return;
            bool valid = epoch == serverEpoch && fingerprint == signature && batch.Count > 0 && batch.Count <= MovementInputBatch.Capacity;
            if (session.ValidateRequest(FrameworkRequestRules.PredictedInput, rpc.Receive.SenderClientId, OwnerClientId, 81, valid, Allowed()) != null) return;
            inbox.Enqueue(batch, MovementClock.Now); // 重叠窗口幂等，越过服务器允许的提前量则整体拒绝。
        }
        void SendState()
        {
            StateRpc(serverEpoch, serverTick, inbox.LastApplied, signature, serverState, Allowed(), queueMs, workMs, measuredServerHz);
        }
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server, Delivery = RpcDelivery.Unreliable)]
        void StateRpc(uint epoch, uint tick, uint acknowledged, uint fingerprint, MovementState state, bool allowed, float queued, float worked, float hz)
        {
            if (!IsOwner || !initialized || !IsActive) return;
            if (fingerprint != signature || !world.IsValid(state)) { ConfigurationMismatch = true; return; }
            if (hasSnapshot && epoch != clientEpoch && !MovementClock.Newer(epoch, clientEpoch)) return;
            if (hasSnapshot && epoch == clientEpoch && !MovementClock.Newer(tick, lastSnapshotTick)) return;
            if (waitingRebase && epoch == clientEpoch) return;
            authority = state; lastSnapshotTick = tick;
            if (!hasSnapshot || epoch != clientEpoch)
            { AcceptInitialState(epoch, acknowledged, state); return; }
            Vector3 before = history.Position;
            bool progressed = MovementClock.Newer(acknowledged, history.LastAcknowledged);
            if (!history.Reconcile(acknowledged, state, world, 1f / ticks.TickRate, out float error, out int replayed)) { BeginRebase(); return; }
            if (progressed)
            {
                lastAckProgress = MovementClock.Now;
                foreach (var observer in observers) observer.OnMovementConfirmed(acknowledged, MovementClock.Now, queued, worked, hz);
            }
            if (error > .001f || history.LastVelocityError > .001f || history.LastGroundedMismatch)
            {
                LastVelocityCorrection = history.LastVelocityError;
                Corrections++; LastCorrectionError = error; MaximumCorrectionError = Mathf.Max(MaximumCorrectionError, error); ReplayedInputs += replayed;
                visualOffset += before - history.Position;
                if (visualOffset.magnitude > 2) visualOffset = Vector3.zero;
            }
            if (!allowed) visualOffset = Vector3.zero;
        }
        void AcceptInitialState(uint epoch, uint acknowledged, MovementState state)
        {
            if (hasSnapshot) RebaseCount++;
            clientEpoch = epoch; history.Reset(state, acknowledged); hasSnapshot = true; waitingRebase = false; jumpPending = false;
            visualOffset = Vector3.zero; authority = state; PresentationPosition = state.Position; lastAckProgress = lastLocalTick = MovementClock.Now;
            foreach (var observer in observers) observer.OnMovementEpochReset();
        }
        void BeginRebase()
        {
            if (IsServer) { ResetServerEpoch(); return; }
            waitingRebase = true; jumpPending = false;
            if (MovementClock.Now >= nextResync)
            { nextResync = MovementClock.Now + 1; RebaseRpc(clientEpoch); }
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void RebaseRpc(uint epoch, RpcParams rpc = default)
        {
            if (!initialized || !IsActive || session.ValidateRequest(FrameworkRequestRules.PredictionRebase, rpc.Receive.SenderClientId, OwnerClientId, 4) != null) return;
            if (epoch == serverEpoch) ResetServerEpoch(); else SendState();
        }
        void ResetServerEpoch()
        {
            serverEpoch = unchecked(serverEpoch + 1); if (serverEpoch == 0) serverEpoch = 1;
            inbox.Reset(); missingSince = 0; SendState();
        }
        void LateUpdate()
        {
            if (!initialized) return;
            if (IsOwner && waitingRebase && MovementClock.Now >= nextResync) BeginRebase();
            bool predicted = LocalPredictionActive;
            Vector3 presented = transform.position;
            if (predicted)
            {
                float partial = Allowed() ? (float)Math.Min(1.0 / ticks.TickRate, Math.Max(0, MovementClock.Now - lastLocalTick)) : 0;
                Vector3 target = world.Step(history.State, input, false, partial).Position;
                visualOffset *= Mathf.Exp(-Time.unscaledDeltaTime / .08f);
                presented = target + visualOffset;
                if (!world.IsFree(presented)) { visualOffset = Vector3.zero; presented = target; }
            }
            PresentationPosition = presented;
            foreach (var observer in observers) observer.OnMovementPresentation(presented, predicted);
        }
        /// <summary>服务器传送会开启新输入代次，拒绝所有传送前滞留的输入。</summary>
        public void TeleportOnServer(Vector3 position)
        {
            if (!IsServer || !initialized || !world.IsFree(position)) throw new InvalidOperationException("Invalid authoritative movement teleport.");
            serverState = world.CreateState(position);
            GetComponent<NetworkTransform>().Teleport(position, transform.rotation, transform.localScale);
            ResetServerEpoch();
        }
        /// <summary>服务器业务施加校正，同一代次的未确认输入仍可在客户端重放。</summary>
        public void CorrectOnServer(Vector3 position)
        {
            if (!IsServer || !initialized || !world.IsFree(position)) throw new InvalidOperationException("Invalid authoritative movement correction.");
            CorrectOnServer(world.CreateState(position, serverState.VerticalVelocity));
        }
        /// <summary>业务可同时校正位置和垂直速度；非法落地标记或速度会被拒绝。</summary>
        public void CorrectOnServer(MovementState state)
        {
            if (!IsServer || !initialized || !world.IsValid(state)) throw new InvalidOperationException("Invalid authoritative movement correction.");
            serverState = state; transform.position = state.Position; SendState();
        }
        public bool InjectLocalErrorForDevelopment(Vector3 delta)
        {
            if (!(Application.isEditor || Debug.isDebugBuild) || !LocalPredictionActive || !world.IsFree(history.Position + delta)) return false;
            history.OffsetForDevelopment(delta); return true;
        }
        public override void OnNetworkDespawn()
        {
            if (ticks != null) ticks.Tick -= OnTick;
            if (observers != null) foreach (var observer in observers) observer.OnMovementPresentation(transform.position, false);
            initialized = hasSnapshot = waitingRebase = jumpPending = hasRecoveryVelocity = false; history.Reset(Vector3.zero); inbox.Reset();
        }
    }
    static class MovementComponentConversion
    {
        public static T[] ConvertAll<T>(this MonoBehaviour[] values)
        { var result = new T[values.Length]; for (int i = 0; i < values.Length; i++) result[i] = (T)(object)values[i]; return result; }
    }
}
