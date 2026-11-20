// 示例玩法：客户端发送输入，服务器移动角色，再由 NetworkTransform 同步位置。
using Unity.Netcode;
using Unity.Netcode.Components;
using Han.Multiplayer.Diagnostics;
using UnityEngine;
using Han.Multiplayer.UI;
using Han.Multiplayer.Lobby;
using Han.Multiplayer.Recovery;
using Han.Multiplayer.Prediction;

namespace Han.Multiplayer.Demo
{
    /// <summary>连接可选移动/跳跃预测的教学玩法；不包含动态物理回滚或射击命中回溯。</summary>
    public sealed class DemoPlayer : NetworkBehaviour, IPlayerRecoveryState, IMovementAuthorityPolicy, IMovementPredictionObserver
    {
        /// <summary>准备状态默认由服务器写入、客户端读取，后加入玩家也能取得当前值。</summary>
        public readonly NetworkVariable<bool> Ready = new NetworkVariable<bool>();
        public string RecoveryKey => "demo.player.v2";
        /// <summary>根位置由恢复模块保存；这里额外保存垂直速度，落地标记从静态几何重算，绝不保存待执行跳跃。</summary>
        public byte[] CaptureRecoveryState()
        {
            var data = new byte[5]; data[0] = Ready.Value ? (byte)1 : (byte)0;
            float velocity = predictedMovement != null && predictedMovement.IsActive ? predictedMovement.AuthoritativeState.VerticalVelocity : 0;
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(velocity), 0, data, 1, 4); return data;
        }
        public void RestoreRecoveryState(byte[] state)
        {
            if (!IsServer || state == null || state.Length != 5 || state[0] > 1) throw new System.ArgumentException("Invalid demo recovery state.");
            Ready.Value = state[0] == 1;
            predictedMovement?.PrepareRecoveryVelocityOnServer(System.BitConverter.ToSingle(state, 1));
            serverInput = Vector2.zero; receivedSequence = receiptRequested = false;
        }
        Vector2 serverInput; // 服务器最近收到的移动意图，x/y 映射到世界 x/z 轴。
        Vector2 uiInput;     // 屏幕方向按钮产生的输入，不直接修改角色位置。
        float lastInputTime; // 超过期限未收到输入时停止移动，避免沿用丢失连接前的指令。
        double nextSendTime; // 原版基线专用的逐帧限频时间。
        Vector2 sampledInput, previousSample;
        double sampledAt, lastMovedAt, lastZeroAt;
        double onsetAt, nextReceiptRequest, nextServerReceipt;
        Vector3 previousPosition, onsetPosition;
        bool onsetPending, receivedSequence, receiptRequested;
        uint nextSequence, serverSequence;
        double serverReceivedAt;
        NetworkTickSystem registeredTicks;
        NetworkTimingSettings timing;
        NetworkResponseMetrics metrics;
        MultiplayerSession session;
        PredictedMovement predictedMovement;
        GameObject predictionVisual;
        bool originalRendererEnabled, originalColliderEnabled;
        public bool CanSimulateMovement => CanPlay();
        readonly EventRate serverSteps = new EventRate();
        bool latencyTestMovement;
        double latencyMotionOrigin;
        /// <summary>仅自动化测量启用的“静止—移动—静止”输入，产生可重复的起步响应样本。</summary>
        public bool LatencyTestMovement
        {
            get => latencyTestMovement;
            set { latencyTestMovement = value; latencyMotionOrigin = MonotonicClock.Now; }
        }
        float nextReadyTime; // 服务器准备状态切换节流时间。
        /// <summary>仅供独立进程测试启用的持续移动，正常游戏不应主动开启。</summary>
        public bool AutomatedMovement { get; set; }
        public bool UseScriptedInput { get; set; }
        public Vector2 ScriptedInput { get; set; }

        /// <summary>网络对象生成后，由服务器设置初始位置，各客户端设置本地显示颜色。</summary>
        public override void OnNetworkSpawn()
        {
            session = NetworkManager.GetComponent<MultiplayerSession>();
            predictedMovement = GetComponent<PredictedMovement>();
            // 大厅示例跨场景保留玩家；基础示例不需要安装大厅模块组件。
            if (NetworkManager.GetComponent<LobbyService>() != null) NetworkObject.DestroyWithScene = false;
            if (IsServer)
            {
                // 将玩家分散到圆周的八个示例出生点；超过八人时会重复使用位置。
                float angle = (OwnerClientId % 8) * Mathf.PI / 4;
                transform.position = new Vector3(Mathf.Cos(angle) * 3, 1, Mathf.Sin(angle) * 3);
            }
#if !UNITY_SERVER
            // 专用服务器不执行角色着色；属性块避免为每个玩家克隆一份材质。
            var block = new MaterialPropertyBlock();
            var color = Color.HSVToRGB((OwnerClientId * 0.23f + 0.5f) % 1, 0.65f, 0.95f);
            // 同时覆盖 Built-in Standard 与 URP Lit 常用的颜色属性。
            block.SetColor("_Color", color);
            block.SetColor("_BaseColor", color);
            GetComponent<Renderer>().SetPropertyBlock(block);
#endif
            var controller = NetworkManager.GetComponent<NetworkTimingController>();
            timing = controller != null ? controller.Active : new NetworkTimingSettings
            {
                preset = NetworkTimingPreset.Aligned30, networkTickRate = NetworkManager.NetworkConfig.TickRate,
                useNetworkTickSimulation = true, interpolatePosition = true,
                extraPositionSmoothing = true, positionSmoothingSeconds = 0.1f
            };
            var synchronizedTransform = GetComponent<NetworkTransform>();
            if (synchronizedTransform != null)
            {
                synchronizedTransform.Interpolate = timing.interpolatePosition;
                synchronizedTransform.PositionLerpSmoothing = timing.extraPositionSmoothing;
                // 只配置位置；不要通过 SetMaxInterpolationBound 同时覆盖旋转与缩放的参数。
                synchronizedTransform.PositionMaxInterpolationTime = timing.positionSmoothingSeconds;
            }
            metrics = NetworkManager.GetComponent<NetworkResponseMetrics>();
            if (IsOwner) metrics?.RegisterLocalPlayer(NetworkObjectId);
            sampledAt = lastMovedAt = lastZeroAt = MonotonicClock.Now;
            previousPosition = transform.position;
            registeredTicks = NetworkManager.NetworkTickSystem;
            registeredTicks.Tick += OnNetworkTick;
        }

        public void OnMovementInputSent(uint sequence, double sampleTime, double sentTime) => metrics?.RecordSend(sequence, sampleTime, sentTime);
        public void OnMovementConfirmed(uint sequence, double now, float queued, float worked, float hz) => metrics?.RecordConfirmation(sequence, now, queued, worked, hz);
        public void OnMovementEpochReset() { metrics?.ClearMeasurements(); onsetPending = false; }
        /// <summary>预测只驱动本地可见模型，不移动服务器权威的根对象或 NetworkTransform。</summary>
        public void OnMovementPresentation(Vector3 position, bool locallyPredicted)
        {
#if !UNITY_SERVER
            if (Application.isBatchMode) return;
            if (!locallyPredicted)
            {
                if (predictionVisual != null)
                {
                    Destroy(predictionVisual); predictionVisual = null;
                    GetComponent<Renderer>().enabled = originalRendererEnabled;
                    var collider = GetComponent<Collider>(); if (collider != null) collider.enabled = originalColliderEnabled;
                }
                return;
            }
            if (predictionVisual == null)
            {
                var source = GetComponent<MeshRenderer>();
                predictionVisual = new GameObject("Local predicted visual"); predictionVisual.transform.SetParent(transform, false);
                predictionVisual.AddComponent<MeshFilter>().sharedMesh = GetComponent<MeshFilter>().sharedMesh;
                var renderer = predictionVisual.AddComponent<MeshRenderer>(); renderer.sharedMaterials = source.sharedMaterials;
                var block = new MaterialPropertyBlock(); source.GetPropertyBlock(block); renderer.SetPropertyBlock(block);
                originalRendererEnabled = source.enabled; source.enabled = false;
                var collider = GetComponent<Collider>(); if (collider != null) { originalColliderEnabled = collider.enabled; collider.enabled = false; }
            }
            predictionVisual.transform.position = position;
#endif
        }

        /// <summary>每渲染帧采样输入；发送和服务器模拟由选择的时序配置决定。</summary>
        void Update()
        {
            if (!IsSpawned || !IsOwner) return;
            double now = MonotonicClock.Now;
            Vector2 input = Vector2.zero;
            if (CanPlay())
            {
                input = uiInput;
#if ENABLE_LEGACY_INPUT_MANAGER
                if (GUIUtility.keyboardControl == 0 && Application.isFocused)
                {
                    input += new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
                    if (!UseScriptedInput && Input.GetKeyDown(KeyCode.Space)) predictedMovement?.RequestJump();
                }
#endif
                if (AutomatedMovement) input = new Vector2(Mathf.Cos(Time.unscaledTime * 0.7f), Mathf.Sin(Time.unscaledTime * 0.7f)) * 0.6f;
                if (latencyTestMovement)
                {
                    double elapsed = now - latencyMotionOrigin;
                    double phase = elapsed % 4;
                    input = phase >= 1 && phase < 2.2 ? new Vector2(((int)(elapsed / 4) & 1) == 0 ? 0.7f : -0.7f, 0) : Vector2.zero;
                }
                if (UseScriptedInput) input = ScriptedInput;
            }
            input = Vector2.ClampMagnitude(input, 1);
            // 只测量已经静止的角色起步，避免把之前残留的移动当成新输入的响应。
            if (input.sqrMagnitude < 0.001f && previousSample.sqrMagnitude >= 0.001f) lastZeroAt = now;
            if (input.sqrMagnitude >= 0.001f && previousSample.sqrMagnitude < 0.001f &&
                now - lastMovedAt >= 0.2 && now - lastZeroAt >= 0.2 && metrics != null && metrics.CollectResponses)
            { onsetPending = true; onsetAt = now; onsetPosition = transform.position; }
            previousSample = sampledInput = input;
            sampledAt = now;
            if (predictedMovement != null && predictedMovement.ConfigurationMismatch) { onsetPending = false; return; }
            if (predictedMovement != null && predictedMovement.IsActive) { predictedMovement.SetInput(input, now); return; }
            if (!CanPlay()) { onsetPending = false; return; }
            if (!timing.useNetworkTickSimulation && now >= nextSendTime)
            {
                nextSendTime = now + 1.0 / 30;
                SendInput();
            }
        }

        /// <summary>NGO 在本次 Tick 回调之后收集 NetworkTransform 状态，使模拟结果进入同一更新周期。</summary>
        void OnNetworkTick()
        {
            if (!IsSpawned) return;
            if (predictedMovement != null && predictedMovement.IsActive) return;
            if (IsOwner && CanPlay() && timing.useNetworkTickSimulation) SendInput();
            if (IsServer && timing.useNetworkTickSimulation) Simulate(1f / registeredTicks.TickRate);
        }

        /// <summary>命令只包含意图、序号和测量请求；不发送客户端决定的位移或时间步长。</summary>
        void SendInput()
        {
            double now = MonotonicClock.Now;
            uint sequence = ++nextSequence;
            metrics?.RecordSend(sequence, sampledAt, now);
            bool receipt = metrics != null && metrics.CollectResponses && now >= nextReceiptRequest;
            if (receipt) nextReceiptRequest = now + 0.1; // 默认最多每秒 10 个测量回执。
            MoveRpc(sampledInput, sequence, receipt);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner, Delivery = RpcDelivery.Unreliable)]
        void MoveRpc(Vector2 input, uint sequence, bool requestReceipt, RpcParams rpc = default)
        {
            if (predictedMovement != null && predictedMovement.IsActive) return;
            bool valid = !float.IsNaN(input.x) && !float.IsNaN(input.y) && !float.IsInfinity(input.x) && !float.IsInfinity(input.y) && input.sqrMagnitude <= 1.001f;
            if (session.ValidateRequest(FrameworkRequestRules.Move, rpc.Receive.SenderClientId, OwnerClientId, 13, valid, CanPlay(), sequence, NetworkObjectId) != null) return;
            // 丢包无需补发旧输入；乱序、重复输入不得覆盖较新的服务器指令。
            if (receivedSequence && !NetworkResponseMetrics.IsNewerSequence(sequence, serverSequence)) return;
            receivedSequence = true;
            serverSequence = sequence;
            serverInput = Vector2.ClampMagnitude(input, 1);
            serverReceivedAt = MonotonicClock.Now;
            // 正式服务器即使收到开发客户端的请求，也不产生诊断回执。
            receiptRequested = requestReceipt && (Application.isEditor || Debug.isDebugBuild);
            lastInputTime = Time.unscaledTime;
        }

        /// <summary>仅原版基线沿用全局物理步长；新配置不修改整个项目的 Fixed Timestep。</summary>
        void FixedUpdate()
        {
            if (IsSpawned && IsServer && timing != null && !(predictedMovement != null && predictedMovement.IsActive) && !timing.useNetworkTickSimulation) Simulate(Time.fixedDeltaTime);
        }

        void Simulate(float deltaTime)
        {
            if (!CanPlay()) { serverInput = Vector2.zero; return; }
            double began = MonotonicClock.Now;
            serverSteps.Record(began);
            if (Time.unscaledTime - lastInputTime > 0.25f) serverInput = Vector2.zero;
            var position = transform.position + new Vector3(serverInput.x, 0, serverInput.y) * (4 * deltaTime);
            position.x = Mathf.Clamp(position.x, -8, 8);
            position.z = Mathf.Clamp(position.z, -8, 8);
            transform.position = position;
            double completed = MonotonicClock.Now;
            // 回执表示该序号实际参与了服务器模拟；它不表示客户端已显示该位置。
            if (receiptRequested && began >= nextServerReceipt)
            {
                nextServerReceipt = began + 0.1;
                receiptRequested = false;
                InputAppliedRpc(serverSequence, (float)((began - serverReceivedAt) * 1000),
                    (float)((completed - began) * 1000), (float)serverSteps.Read(completed));
            }
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server, Delivery = RpcDelivery.Unreliable)]
        void InputAppliedRpc(uint sequence, float queueMs, float workMs, float serverHz)
        {
            if (IsOwner && metrics != null) metrics.RecordConfirmation(sequence, MonotonicClock.Now, queueMs, workMs, serverHz);
        }

        /// <summary>帧末观察本地对象位置，测量静止起步响应；不包含 GPU/显示器扫描延迟。</summary>
        void LateUpdate()
        {
            if (!IsSpawned || !IsOwner) return;
            double now = MonotonicClock.Now;
            Vector3 observed = predictedMovement != null && predictedMovement.LocalPredictionActive ? predictedMovement.PresentationPosition : transform.position;
            if ((observed - previousPosition).sqrMagnitude > 0.0000001f) lastMovedAt = now;
            previousPosition = observed;
            if (!onsetPending) return;
            if (!CanPlay() || sampledInput.sqrMagnitude < 0.001f || now - onsetAt > 2) { onsetPending = false; return; }
            if ((observed - onsetPosition).sqrMagnitude >= 0.0004f)
            { metrics?.RecordVisibleOnset((now - onsetAt) * 1000); onsetPending = false; }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer) session?.ForgetRequestScope(OwnerClientId, NetworkObjectId);
            OnMovementPresentation(transform.position, false);
            if (registeredTicks != null) registeredTicks.Tick -= OnNetworkTick;
            if (IsOwner && metrics != null) metrics.UnregisterLocalPlayer(NetworkObjectId);
            receivedSequence = receiptRequested = onsetPending = false;
        }

        /// <summary>本地 UI 的准备按钮入口；仅对象所有者可以发起请求。</summary>
        public void ToggleReady()
        {
            if (IsSpawned && IsOwner) ToggleReadyRpc();
        }

        /// <summary>服务器校验切换频率，并修改会同步给所有客户端的准备状态。</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void ToggleReadyRpc(RpcParams rpc = default)
        {
            if (session.ValidateRequest(FrameworkRequestRules.PlayerReady, rpc.Receive.SenderClientId, OwnerClientId, 0, phaseAllowed: CanPlay()) != null) return;
            if (Time.unscaledTime < nextReadyTime) return;
            nextReadyTime = Time.unscaledTime + 0.25f;
            Ready.Value = !Ready.Value;
        }

        /// <summary>窗口失去焦点时清除屏幕按钮输入，避免鼠标释放事件丢失后继续移动。</summary>
        void OnApplicationFocus(bool focused) { if (!focused) uiInput = Vector2.zero; }

        /// <summary>为当前玩家绘制简易方向键和准备按钮；无图形服务器和远端角色不绘制。</summary>
        void OnGUI()
        {
            if (Application.isBatchMode) return;
            using (ChineseGui.Use()) DrawChineseGui();
        }

        // 在同一中文皮肤作用域中绘制控件，结束后恢复外部界面样式。
        void DrawChineseGui()
        {
            if (!IsSpawned || !IsOwner || !CanPlay() || Application.isBatchMode) return;
            float scale = Mathf.Max(0.6f, Mathf.Min(Screen.width / 960f, Screen.height / 640f));
            // 暂时缩放到演示界面的设计尺寸，绘制结束后必须恢复，避免影响其他 OnGUI。
            var previous = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            float top = Screen.height / scale - 180;
            GUILayout.BeginArea(new Rect(20, top, 320, 170), GUI.skin.box);
            GUILayout.Label("移动：WASD / 方向键；跳跃：空格");
            Vector2 input = Vector2.zero;
            GUILayout.BeginHorizontal();
            if (GUILayout.RepeatButton("左", GUILayout.Height(30))) input.x -= 1;
            if (GUILayout.RepeatButton("前", GUILayout.Height(30))) input.y += 1;
            if (GUILayout.RepeatButton("后", GUILayout.Height(30))) input.y -= 1;
            if (GUILayout.RepeatButton("右", GUILayout.Height(30))) input.x += 1;
            GUILayout.EndHorizontal();
            // IMGUI 每帧会多次调用；只在显示/释放阶段更新持有输入，避免布局阶段覆盖它。
            if (Event.current.type == EventType.Repaint || Event.current.type == EventType.MouseUp) uiInput = input;
            if (predictedMovement != null && predictedMovement.SupportsJump)
            {
                if (GUILayout.Button("跳跃（按一次）", GUILayout.Height(26))) predictedMovement.RequestJump();
                var state = predictedMovement.IsServer ? predictedMovement.AuthoritativeState : predictedMovement.SimulatedState;
                GUILayout.Label((state.Grounded ? "已落地" : "空中") + " | 垂直速度 " + state.VerticalVelocity.ToString("F2") + " 米/秒");
            }
            if (NetworkManager.GetComponent<LobbyService>() == null && GUILayout.Button(Ready.Value ? "已准备（点击取消）" : "未准备（点击准备）", GUILayout.Height(28))) ToggleReady();
            GUILayout.EndArea();
            GUI.matrix = previous;
        }

        /// <summary>只有安装大厅服务的场景才受大厅阶段约束，原有示例行为保持可用。</summary>
        bool CanPlay()
        {
            var lobby = NetworkManager.GetComponent<LobbyService>();
            return lobby == null || (lobby.Room != null && lobby.Room.Phase.Value == LobbyPhase.Playing);
        }
    }
}
