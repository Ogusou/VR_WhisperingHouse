using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;

public class WhisperRelay : UdonSharpBehaviour
{
    [Header("受信側ゲート（m）")]
    public float listenerStartDistance = 0.45f;

    [Header("受信側の自動解除")]
    public float listenerTimeout = 1.6f;
    public float listenerEndDistance = 2.0f;

    [Header("Ping 間隔（秒）")]
    public float whisperPingInterval = 0.5f;

    // === ReplyLabel と同様の安定化ゲート（near = min(dR,dL)） ===
    [Header("Listener Visual Gate (ReplyLabel と同等)")]
    [Tooltip("受信開始（これ以下でON候補）")]
    public float enterDistance = 0.40f;
    [Tooltip("受信終了（これ以上でOFF候補）")]
    public float exitDistance = 0.50f;
    [Tooltip("ON/OFFに遷移させるのに必要な連続一致回数")]
    public int confirmCount = 3;
    [Tooltip("最後のPingからのタイムアウト秒数（超えたら停止扱い）")]
    public float pingTimeoutSec = 1.5f;

    // ───────── WhisperManager へのブリッジ（受信安定性ログ用） ─────────
    [Header("WhisperManager hook (optional)")]
    [Tooltip("受信距離(dR,dL)を WhisperManager に転送して安定性ログ/ReplyLabel を更新します")]
    public WhisperManager manager;
    public bool forwardToManager = true;

    // ───────── 見た目（リスナー側：話し手と同じヘッドロック配置） ─────────
    [Header("Listener Vignette (Head-Locked, Talkerと同位置)")]
    public Image vignetteImage;
    public RectTransform vignetteRect;
    public Color listenerVignetteTint = new Color(0.55f, 1f, 1f, 1f);
    [Range(0f, 1f)] public float vignetteAlphaOn = 0.35f;
    [Range(0f, 1f)] public float vignetteAlphaOff = 0f;
    [Tooltip("頭の前方距離[m]（WhisperManager の Vignette Distance と同値推奨）")]
    public float vignetteDistance = 0.085f;
    [Tooltip("見かけサイズ[m]（WhisperManager と同値推奨）")]
    public Vector2 vignetteSizeMeters = new Vector2(0.26f, 0.21f);
    public float vignetteFadeInTime = 0.20f;
    public float vignetteFadeOutTime = 0.15f;

    [Header("Listener Icon (Head-Locked, Talkerと同位置)")]
    public Image whisperedIconImage;
    public RectTransform whisperedIconRect;
    [Tooltip("視線基準のローカルオフセット[m]（WhisperManager と同値推奨）")]
    public Vector2 iconOffsetMeters = new Vector2(-0.065f, 0.02f);
    [Tooltip("見かけサイズ[m]（WhisperManager と同値推奨）")]
    public Vector2 iconSizeMeters = new Vector2(0.035f, 0.035f);
    [Range(0f, 1f)] public float iconAlphaOn = 1f;
    [Range(0f, 1f)] public float iconAlphaOff = 0f;
    public float iconFadeInTime = 0.20f;
    public float iconFadeOutTime = 0.15f;
    // ★ 追加: リスナーダッキングの詳細パラメータ（話し手側と同等）
    [Header("Listener Ducking (Local)")]
    public AudioSource[] duckTargets;
    [Range(0f, 1f)] public float duckLevelListener = 0.35f;
    [Tooltip("ローパスを使う場合は ON")]
    public bool duckUseLowpass = true;
    [Range(100, 22000)] public int duckLowpassCutoff = 900;
    [Tooltip("ダックの入り時間(秒)")]
    public float duckFadeInTime = 0.15f;
    [Tooltip("ダックの戻し時間(秒)")]
    public float duckFadeOutTime = 0.20f;

    // ★ 追加: ダッキング内部状態
    private float[] _duckOrigVol;
    private AudioLowPassFilter[] _duckLPF;
    private int[] _duckOrigCutoff;
    private bool[] _duckOrigLPFEnabled;
    private float _duckAlpha = 0f, _duckTarget = 0f;

    // ───────── Logging ─────────
    [Header("Logging")]
    public bool logVerbose = true;
    public bool logOwnerTrace = true;
    [Tooltip("1人検証時は自分にも飛ばして受信ログを確認（本番はOFF推奨）")]
    public bool loopbackInSolo = true;
    public string logTag = "[Whisper]";

    [Header("Voice Gate pool (for target check)")]
    public WhisperGatePool gatePool;


    // 状態
    private VRCPlayerApi _local;
    private bool _listenerActive;
    private int _speakerId = -1;
    private float _aliveUntil;
    private bool _earRight = true;   // ※耳寄せは使わないが保持はしておく
    private float _nextPing;


    private bool _debugReplyLatched = false;


    // 安定化 / タイムアウト
    private int _stableCounter;
    private int _unstableCounter;
    private float _lastPingTime;

    // フェード
    private float _vigAlpha = 0f, _vigTarget = 0f;
    private float _iconAlpha = 0f, _iconTarget = 0f;

    void Start()
    {
        _local = Networking.LocalPlayer;

        // ★ 変更: Duck 初期化を拡張（LPFも握る）
        if (duckTargets != null && duckTargets.Length > 0)
        {
            int n = duckTargets.Length;
            _duckOrigVol = new float[n];
            _duckLPF = new AudioLowPassFilter[n];
            _duckOrigCutoff = new int[n];
            _duckOrigLPFEnabled = new bool[n];

            for (int i = 0; i < n; i++)
            {
                var a = duckTargets[i];
                if (a == null) continue;

                _duckOrigVol[i] = a.volume;

                var lpf = a.GetComponent<AudioLowPassFilter>();
                _duckLPF[i] = lpf;
                if (lpf != null)
                {
                    _duckOrigLPFEnabled[i] = lpf.enabled;
                    _duckOrigCutoff[i] = Mathf.RoundToInt(lpf.cutoffFrequency);
                }
            }
        }

        // 画像の初期アルファ＝0（非表示）
        if (vignetteImage != null)
        {
            var c = listenerVignetteTint; c.a = 0f;
            vignetteImage.color = c;
            _vigAlpha = 0f; _vigTarget = 0f;
        }
        if (whisperedIconImage != null)
        {
            var ic = whisperedIconImage.color; ic.a = 0f;
            whisperedIconImage.color = ic;
            _iconAlpha = 0f; _iconTarget = 0f;
        }

        _SetListenerVisual(false, true);

        L($"Start | isOwner={Networking.IsOwner(gameObject)} players={VRCPlayerApi.GetPlayerCount()}");
    }

    // ==== 話し手から呼ぶAPI ====
    public void TalkerEnter()
    {
        if (_local == null) return;

        L($"SEND Enter (request owner transfer)");
        Networking.SetOwner(_local, gameObject);
        L($"Owner after SetOwner = {Networking.GetOwner(gameObject).playerId}:{Networking.GetOwner(gameObject).displayName}");

        var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
            ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
            : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

        SendCustomNetworkEvent(target, nameof(W_Enter));
        L($"SEND Enter -> {target}");

        SendCustomEventDelayedSeconds(nameof(_EchoEnter), 0.10f);
        _nextPing = Time.time + 0.20f;
    }

    public void TalkerTick()
    {
        if (_local == null) return;
        if (Time.time >= _nextPing)
        {
            Networking.SetOwner(_local, gameObject);
            var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
                ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
                : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

            SendCustomNetworkEvent(target, nameof(W_Ping));
            L($"SEND Ping -> {target}");
            _nextPing = Time.time + whisperPingInterval;
        }
    }

    public void TalkerExit()
    {
        if (_local == null) return;
        Networking.SetOwner(_local, gameObject);

        var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
            ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
            : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

        SendCustomNetworkEvent(target, nameof(W_Exit));
        L($"SEND Exit -> {target}");
    }

    public void _EchoEnter()
    {
        var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
            ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
            : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

        SendCustomNetworkEvent(target, nameof(W_Enter));
        L($"SEND Enter (echo) -> {target}");
    }

    // ==== 受信側（全員が動く） ====
    public void W_Enter()
    {
        var sp = Networking.GetOwner(gameObject);
        if (sp == null || sp.isLocal) return;

        bool earRight; float dR, dL;
        bool near = _IsMyHeadNearSpeakersHandEx(sp, listenerStartDistance, out earRight, out dR, out dL);
        bool allow = near && _ShouldShowForSpeaker(sp);

        _speakerId = sp.playerId;
        _earRight = earRight;

        if (allow) { _MarkAlive(); _SetListenerVisual(true, _earRight); }
        else       { _listenerActive = false; _SetListenerVisual(false, _earRight); }

        if (forwardToManager && manager != null) manager.OnWhisperEnter(dR, dL);
        L($"RECV Enter from {sp.playerId}:{sp.displayName} allow={allow} near={near} dR={dR:F2} dL={dL:F2}");
    }


    public void W_Ping()
{
    var sp = Networking.GetOwner(gameObject);
    if (sp == null || sp.isLocal) return;

    if (_speakerId == sp.playerId)
    {
        bool earRight; float dR, dL;
        _IsMyHeadNearSpeakersHandEx(sp, listenerStartDistance * 1.25f, out earRight, out dR, out dL);
        _earRight = earRight;

        bool allow = _ShouldShowForSpeaker(sp);
        _MarkAlive();
        _SetListenerVisual(allow, _earRight);

        if (forwardToManager && manager != null) manager.OnWhisperPing(dR, dL, true);
        L($"RECV Ping keepAlive allow={allow} dR={dR:F2} dL={dL:F2}");
        return;
    }

    // “遅れて条件を満たした”人向け
    bool ear; float dR2, dL2;
    bool near = _IsMyHeadNearSpeakersHandEx(sp, listenerStartDistance, out ear, out dR2, out dL2);
    bool allowLate = near && _ShouldShowForSpeaker(sp);

    if (allowLate)
    {
        _speakerId = sp.playerId;
        _earRight = ear;
        _MarkAlive();
        _SetListenerVisual(true, _earRight);
        L($"RECV Ping (late activate) allowLate=true dR={dR2:F2} dL={dL2:F2}");
    }
    else
    {
        L($"RECV Ping ignored allowLate=false near={near} dR={dR2:F2} dL={dL2:F2}");
    }
}


    public void W_Exit()
    {
        var sp = Networking.GetOwner(gameObject);
        if (sp == null || sp.isLocal) return;

        if (_listenerActive && _speakerId == sp.playerId)
        {
            _listenerActive = false;
            _SetListenerVisual(false, _earRight);

            if (forwardToManager && manager != null) manager.OnWhisperExit();
            L($"RECV Exit from {sp.playerId}:{sp.displayName} -> listener OFF");
        }
        else
        {
            L($"RECV Exit from {sp.playerId}:{sp.displayName} ignored (not my speaker)");
        }
    }

    void Update()
    {
        // ヘッドロックの追従
        _TickVignetteTransform();
        _TickIconTransform();
        _TickVignetteFade();
        _TickIconFade();

        // ★ 追加: 毎フレーム ダックの補間を回す
        _TickDuck();

        // デバッグ・ラッチ時は消灯ロジックをスキップ
        if (_debugReplyLatched)
        {
            _listenerActive = true;
            _aliveUntil = Time.time + 3600f;
            return;
        }

        if (!_listenerActive) return;


        if (Time.time >= _aliveUntil)
        {
            _listenerActive = false;
            _SetListenerVisual(false, _earRight);
            L("listener timeout -> OFF");
            return;
        }

        var sp = VRCPlayerApi.GetPlayerById(_speakerId);
        if (sp != null && sp.IsValid())
        {
            var myHead = _local.GetBonePosition(HumanBodyBones.Head);
            var spHead = sp.GetBonePosition(HumanBodyBones.Head);
            if (myHead != Vector3.zero && spHead != Vector3.zero)
            {
                float dd = Vector3.Distance(myHead, spHead);
                if (dd > listenerEndDistance)
                {
                    _listenerActive = false;
                    _SetListenerVisual(false, _earRight);
                    if (forwardToManager && manager != null) manager.OnWhisperExit();
                    L($"listener end by distance dd={dd:F2} -> OFF");
                }
            }
        }
    }

    // 所有権ログ
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (logOwnerTrace)
            L($"OnOwnershipTransferred -> now owner {player.playerId}:{player.displayName}");
    }

    // ==== 補助 ====
    private void _MarkAlive()
    {
        _listenerActive = true;
        _aliveUntil = Time.time + listenerTimeout;
    }

    // 距離計算（Ex はログ＆転送用に dR/dL を返す）
    private bool _IsMyHeadNearSpeakersHandEx(VRCPlayerApi speaker, float thr, out bool rightEar, out float dR, out float dL)
    {
        rightEar = true; dR = 1e9f; dL = 1e9f;
        if (speaker == null || _local == null) return false;

        Vector3 myHead = _local.GetBonePosition(HumanBodyBones.Head);
        if (myHead == Vector3.zero) return false;

        Vector3 rh = speaker.GetBonePosition(HumanBodyBones.RightHand);
        Vector3 lh = speaker.GetBonePosition(HumanBodyBones.LeftHand);

        dR = (rh == Vector3.zero) ? 1e9f : Vector3.Distance(rh, myHead);
        dL = (lh == Vector3.zero) ? 1e9f : Vector3.Distance(lh, myHead);

        if (dR <= dL) { rightEar = true; return dR < thr; }
        else { rightEar = false; return dL < thr; }
    }

    private void _SetListenerVisual(bool on, bool earRight /*unused*/)
    {
        // Vignette 色・アルファ目標
        if (vignetteImage != null)
        {
            var c = vignetteImage.color;
            c.r = listenerVignetteTint.r; c.g = listenerVignetteTint.g; c.b = listenerVignetteTint.b;
            vignetteImage.color = c;
        }
        _vigTarget = on ? vignetteAlphaOn : vignetteAlphaOff;

        // アイコン目標
        _iconTarget = on ? iconAlphaOn : iconAlphaOff;

        // ★ 変更: ダックは即時セットではなく「目標値」にする（フェードで滑らかに）
        _duckTarget = on ? 1f : 0f;

        L($"visual {(on ? "ON" : "OFF")} (head-locked, ear offset unused)");
    }

    // ★ 追加: リスナー側のダック処理（話し手の WhisperManager._TickDuck を簡約移植）
    private void _TickDuck()
    {
        if (duckTargets == null || duckTargets.Length == 0) return;

        float dur = (_duckTarget > _duckAlpha) ? Mathf.Max(0.01f, duckFadeInTime)
                                            : Mathf.Max(0.01f, duckFadeOutTime);
        float step = Time.deltaTime / dur;
        _duckAlpha = Mathf.MoveTowards(_duckAlpha, _duckTarget, step);

        for (int i = 0; i < duckTargets.Length; i++)
        {
            var a = duckTargets[i];
            if (a == null) continue;

            // 元音量 → 元×duckLevelListener へ補間
            float v0 = (_duckOrigVol != null && i < _duckOrigVol.Length) ? _duckOrigVol[i] : a.volume;
            a.volume = Mathf.Lerp(v0, v0 * duckLevelListener, _duckAlpha);

            var lpf = (_duckLPF != null && i < _duckLPF.Length) ? _duckLPF[i] : null;
            if (lpf != null)
            {
                if (duckUseLowpass)
                {
                    lpf.enabled = true;
                    float c0 = (_duckOrigCutoff != null && i < _duckOrigCutoff.Length && _duckOrigCutoff[i] > 0)
                            ? _duckOrigCutoff[i] : 22000f;
                    lpf.cutoffFrequency = Mathf.Lerp(c0, duckLowpassCutoff, _duckAlpha);
                }
                else
                {
                    // ローパスを使わないなら、元の有効状態に戻す
                    if (_duckOrigLPFEnabled != null && i < _duckOrigLPFEnabled.Length)
                        lpf.enabled = _duckOrigLPFEnabled[i];
                }
            }
        }
    }


    // ── Vignette フェード
    private void _TickVignetteFade()
    {
        if (vignetteImage == null) return;
        float dur = (_vigTarget > _vigAlpha) ? Mathf.Max(0.01f, vignetteFadeInTime)
                                             : Mathf.Max(0.01f, vignetteFadeOutTime);
        float step = Time.deltaTime / dur;
        _vigAlpha = Mathf.MoveTowards(_vigAlpha, _vigTarget, step);
        var c = vignetteImage.color; c.a = _vigAlpha; vignetteImage.color = c;
    }

    // ── Vignette ヘッドロック配置（WhisperManager と同等）
    private void _TickVignetteTransform()
    {
        if (vignetteRect == null || _local == null) return;

        var td = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

        Vector3 planeCenter = td.position + td.rotation * (Vector3.forward * vignetteDistance);
        Quaternion rot = td.rotation * Quaternion.Euler(0f, 180f, 0f);
        vignetteRect.SetPositionAndRotation(planeCenter, rot);

        Vector2 px = vignetteRect.sizeDelta;
        if (px.x < 1f) px = new Vector2(1024f, 1024f);
        float sx = vignetteSizeMeters.x / px.x;
        float sy = vignetteSizeMeters.y / px.y;
        vignetteRect.localScale = new Vector3(sx, sy, 1f);
    }

    // ── Icon フェード
    private void _TickIconFade()
    {
        if (whisperedIconImage == null) return;
        float dur = (_iconTarget > _iconAlpha) ? Mathf.Max(0.01f, iconFadeInTime)
                                               : Mathf.Max(0.01f, iconFadeOutTime);
        float step = Time.deltaTime / dur;
        _iconAlpha = Mathf.MoveTowards(_iconAlpha, _iconTarget, step);
        var c = whisperedIconImage.color; c.a = _iconAlpha; whisperedIconImage.color = c;
    }

    // ── Icon ヘッドロック配置（WhisperManager と同等）
    private void _TickIconTransform()
    {
        if (whisperedIconRect == null || _local == null) return;

        var td = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

        Vector3 planeCenter = td.position + td.rotation * (Vector3.forward * vignetteDistance);
        Vector3 localOffset = new Vector3(iconOffsetMeters.x, iconOffsetMeters.y, 0f);
        Vector3 pos = planeCenter + td.rotation * localOffset;

        Quaternion rot = td.rotation * Quaternion.Euler(0f, 180f, 0f);
        whisperedIconRect.SetPositionAndRotation(pos, rot);

        Vector2 px = whisperedIconRect.sizeDelta;
        if (px.x < 1f) px = new Vector2(256f, 256f);
        float sx = iconSizeMeters.x / px.x;
        float sy = iconSizeMeters.y / px.y;
        whisperedIconRect.localScale = new Vector3(sx, sy, 1f);
    }

    // ── 短縮ログ
    private void L(string msg)
    {
        if (!logVerbose) return;
        string who = (_local != null) ? $"{_local.playerId}:{_local.displayName}" : "?(local)";
        Debug.Log($"{logTag} {who} | {msg}");
    }



    // ===== Debug API: ReplySwitch から呼ぶ =====
    public void DebugReplyOn()
    {
        _debugReplyLatched = true;
        _listenerActive = true;
        // 耳方向はどっちでもOK（センター表示にしたい場合はオフセットを 0 に）
        _SetListenerVisual(true, _earRight);
        L("DEBUG reply ON (latched)");
    }

    public void DebugReplyOff()
    {
        _debugReplyLatched = false;
        _listenerActive = false;
        _SetListenerVisual(false, _earRight);
        L("DEBUG reply OFF (latched)");
    }

    private bool _ShouldShowForSpeaker(VRCPlayerApi speaker)
    {
        if (speaker == null) return false;
        if (gatePool == null) return true; // 参照が無い場合は従来挙動（距離のみ）

        var g = gatePool.GetGateAssignedTo(speaker.playerId);
        var lp = _local ?? Networking.LocalPlayer;
        if (g == null || lp == null) return false;

        // 「話者のGateがON」かつ「targetPidが自分」だけ可
        return g.gateOn && g.targetPid == lp.playerId;
    }


}
