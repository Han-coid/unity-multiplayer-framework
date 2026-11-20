// 可选玩法配置：只在会话开始时冻结移动世界；本地开关不改变服务器权威或网络协议。
using System;
using UnityEngine;

namespace Han.Multiplayer.Prediction
{
    public enum MovementPredictionMode { On, Off, Legacy }
    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class MovementPredictionSession : MonoBehaviour
    {
        public MovementSettings Settings = new MovementSettings();
        [SerializeField] MovementPredictionMode mode = MovementPredictionMode.On;
        MultiplayerSession session;
        MovementWorld world;
        MovementPredictionMode activeMode;
        bool frozen;
        public MovementPredictionMode Mode => frozen ? activeMode : mode;
        public bool UsesCommandMovement => Mode != MovementPredictionMode.Legacy;
        public bool PredictLocally => Mode == MovementPredictionMode.On;
        public MovementWorld World => world ?? (world = new MovementWorld(Settings));
        public bool CanSelect => session != null && !session.Network.IsListening && !session.Network.ShutdownInProgress &&
            (session.State == SessionState.Offline || session.State == SessionState.Faulted);
        void Awake() { session = GetComponent<MultiplayerSession>(); }
        void OnEnable() { session.StateChanged += OnState; }
        void OnDisable() { if (session != null) session.StateChanged -= OnState; }
        void OnState(SessionState state)
        {
            if (state == SessionState.Starting)
            { world = new MovementWorld(Settings); activeMode = mode; frozen = true; }
            if (state == SessionState.Offline) { frozen = false; world = null; }
        }
        public void Select(MovementPredictionMode value)
        {
            if (!CanSelect || !Enum.IsDefined(typeof(MovementPredictionMode), value)) throw new InvalidOperationException("Disconnect before changing movement prediction.");
            mode = value; world = null;
        }
        public void ConfigureArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++) if (args[i] == "--net-prediction")
            {
                if (++i >= args.Length || !Enum.TryParse(args[i], true, out MovementPredictionMode value) || !Enum.IsDefined(typeof(MovementPredictionMode), value))
                    throw new ArgumentException("--net-prediction must be on, off or legacy.");
                Select(value);
            }
        }
    }
    public interface IMovementAuthorityPolicy { bool CanSimulateMovement { get; } }
    public interface IMovementPredictionObserver
    {
        void OnMovementInputSent(uint sequence, double sampledAt, double sentAt);
        void OnMovementConfirmed(uint sequence, double now, float serverQueueMs, float serverWorkMs, float serverHz);
        void OnMovementPresentation(Vector3 position, bool locallyPredicted);
        void OnMovementEpochReset();
    }
}
