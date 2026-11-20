// 本机响应测量：区分输入排队、服务端确认和静止起步位置响应，不等同于 UTP RTT。
using System;
using UnityEngine;

namespace Han.Multiplayer.Diagnostics
{
    [Serializable]
    public sealed class NetworkTimingSnapshot
    {
        public bool responsesEnabled;
        public bool localPlayerAvailable;
        public double framesPerSecond;
        public double observedInputHz;
        public double observedServerSimulationHz;
        public NetworkTimingSettings settings;
        public TimingSummary frameTime, inputQueue, confirmation, serverQueue, serverWork, visibleOnset;
    }

    [Serializable]
    public sealed class NetworkTimingMeasurements
    {
        public string scope = "Local monotonic clock. Confirmation includes network/server/return; visible onset is stationary-to-moving position observed at LateUpdate, not physical input-to-photon latency.";
        public NetworkTimingSnapshot latest;
        public double[] inputQueueSamplesMs, confirmationSamplesMs, serverQueueSamplesMs, serverWorkSamplesMs, visibleOnsetSamplesMs;
    }

    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class NetworkResponseMetrics : MonoBehaviour
    {
        struct PendingInput { public bool valid; public uint sequence; public double sentAt, sampledAt; }
        readonly PendingInput[] pending = new PendingInput[256];
        readonly TimingSeries frameTimes = new TimingSeries();
        readonly TimingSeries inputQueue = new TimingSeries();
        readonly TimingSeries confirmation = new TimingSeries();
        readonly TimingSeries serverQueue = new TimingSeries();
        readonly TimingSeries serverWork = new TimingSeries();
        readonly TimingSeries visibleOnset = new TimingSeries();
        readonly EventRate inputRate = new EventRate();
        MultiplayerSession session;
        bool collectResponses = true;
        bool localPlayerAvailable;
        ulong localObject;
        double measuredServerHz;
        public bool CollectResponses
        {
            get => collectResponses && (Application.isEditor || Debug.isDebugBuild);
            set { if (collectResponses != value) { collectResponses = value; ClearMeasurements(); } }
        }

        void Awake() { session = GetComponent<MultiplayerSession>(); }
        void OnEnable() { session.StateChanged += OnState; }
        void OnDisable() { if (session != null) session.StateChanged -= OnState; }
        void Update() { if (Time.unscaledDeltaTime > 0) frameTimes.Add(Time.unscaledDeltaTime * 1000, MonotonicClock.Now); }
        void OnState(SessionState state)
        {
            if (state == SessionState.Starting) { localPlayerAvailable = false; ClearMeasurements(); }
            if (state == SessionState.Offline || state == SessionState.Faulted) localPlayerAvailable = false;
        }

        /// <summary>每次本地玩家重新生成都清除旧命令，防止连接/对象 ID 被复用时误配回执。</summary>
        public void RegisterLocalPlayer(ulong objectId) { localObject = objectId; localPlayerAvailable = true; ClearMeasurements(); }
        public void UnregisterLocalPlayer(ulong objectId) { if (localObject == objectId) localPlayerAvailable = false; }
        public void ClearMeasurements()
        {
            Array.Clear(pending, 0, pending.Length);
            inputQueue.Clear(); confirmation.Clear(); serverQueue.Clear(); serverWork.Clear(); visibleOnset.Clear();
            inputRate.Clear(); measuredServerHz = 0;
        }

        public void RecordSend(uint sequence, double sampledAt, double sentAt)
        {
            if (!FiniteNonnegative(sampledAt) || !FiniteNonnegative(sentAt) || sentAt < sampledAt) return;
            inputRate.Record(sentAt);
            if (!CollectResponses) return;
            pending[sequence % (uint)pending.Length] = new PendingInput { valid = true, sequence = sequence, sampledAt = sampledAt, sentAt = sentAt };
            inputQueue.Add((sentAt - sampledAt) * 1000, sentAt);
        }

        /// <summary>只匹配仍在缓冲中的命令；重复、迟到或已覆盖的回执不产生统计。</summary>
        public bool RecordConfirmation(uint sequence, double now, double serverQueueMs, double serverWorkMs, double serverHz)
        {
            if (!CollectResponses || !FiniteNonnegative(now)) return false;
            int index = (int)(sequence % (uint)pending.Length);
            var entry = pending[index];
            if (!entry.valid || entry.sequence != sequence || now < entry.sentAt || now < entry.sampledAt ||
                !FiniteNonnegative(serverQueueMs) || !FiniteNonnegative(serverWorkMs) || !FiniteNonnegative(serverHz)) return false;
            pending[index].valid = false;
            confirmation.Add((now - entry.sampledAt) * 1000, now);
            serverQueue.Add(serverQueueMs, now);
            serverWork.Add(serverWorkMs, now);
            measuredServerHz = serverHz;
            return true;
        }

        public void RecordVisibleOnset(double milliseconds) { if (CollectResponses) visibleOnset.Add(milliseconds, MonotonicClock.Now); }
        public static bool IsNewerSequence(uint sequence, uint previous) => unchecked((int)(sequence - previous)) > 0;
        static bool FiniteNonnegative(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

        public NetworkTimingSnapshot Capture()
        {
            double now = MonotonicClock.Now;
            var frames = frameTimes.Summary(now);
            var controller = GetComponent<NetworkTimingController>();
            return new NetworkTimingSnapshot
            {
                responsesEnabled = CollectResponses, localPlayerAvailable = localPlayerAvailable,
                framesPerSecond = frames.available && frames.meanMs > 0 ? 1000 / frames.meanMs : 0,
                observedInputHz = inputRate.Read(now), observedServerSimulationHz = measuredServerHz,
                settings = controller != null ? controller.Active : null, frameTime = frames,
                inputQueue = inputQueue.Summary(now), confirmation = confirmation.Summary(now), serverQueue = serverQueue.Summary(now),
                serverWork = serverWork.Summary(now), visibleOnset = visibleOnset.Summary(now)
            };
        }

        public NetworkTimingMeasurements Export() => new NetworkTimingMeasurements
        {
            latest = Capture(), inputQueueSamplesMs = inputQueue.Values(), confirmationSamplesMs = confirmation.Values(),
            serverQueueSamplesMs = serverQueue.Values(), serverWorkSamplesMs = serverWork.Values(), visibleOnsetSamplesMs = visibleOnset.Values()
        };
    }
}
