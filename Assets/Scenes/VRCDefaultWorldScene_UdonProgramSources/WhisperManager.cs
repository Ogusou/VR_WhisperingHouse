using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// WhisperManager
/// 役割：
///  - 『話し手（ローカル）』の囁き判定
///  - 音声距離の切替／ローカルFX（ビネット・アイコン・ハプティクス・ダッキング）
///  - ネットワーク通知や『被囁き（リスナー）側の表示』は WhisperReply に委譲
///     ↳ 必要なら inspector で `reply` に WhisperReply を割り当ててください。
///  - 受信側安定性ログ（ReplyLabel）も受け取り表示可能（OnWhisperEnter/Ping/Exit）
/// </summary>
public class WhisperManager : UdonSharpBehaviour
{
    // ───────── 依存（ネットワーク配信は別コンポーネントへ） ─────────
    [Header("Relay (optional)")]
    [Tooltip("WhisperReply を指定すると、囁き開始/継続/終了タイミングで TalkerEnter/TalkerTick/TalkerExit を呼びます")]
    public UdonBehaviour reply; // WhisperReply 側に public メソッド名 "TalkerEnter/ TalkerTick/ TalkerExit" を用意

    // ───────── 基本設定 ─────────
    [Header("距離しきい値 (m)")]
    public float selfEarThreshold = 0.12f;
    public float otherEarThreshold = 0.12f;

    [Header("ソロデバッグ")]
    [Tooltip("ONにすると『相手との距離条件』を常に合格扱いにする（1人でも検証可）")]
    public bool debugPassOtherDistance = false;

    [Header("掌法線の算出")]
    [Tooltip("指数/小指/中指prox から掌法線を再構成（推奨）")]
    public bool usePalmNormalFromFingers = true;

    [Header("手の向きベース（掌法線フォールバック）")]
    [Tooltip("掌法線の軸 0=Forward, 1=Up, 2=Right（再構成が失敗したときに使用）")]
    public int palmAxis = 0;

    [Header("掌向きの符号調整")]
    [Tooltip("手のひらが口を向くときに +1 になるように符号を調整（逆なら -1 を指定）")]
    public float palmDotSign = 1f;

    [Header("指先フォールバック (dy 推定)")]
    [Tooltip("指先が無効のとき回転ベースのフォールバックを使う")]
    public bool useRotationFallbackForVertical = true;
    [Tooltip("手ローカルの “指方向” 軸 0=Forward,1=Up,2=Right（固定）")]
    public int fingerAxis = 1;
    [Tooltip("フォールバック dy の最大振幅（真上≈+この値）")]
    public float pseudoTargetAmplitude = 0.12f;
    [Tooltip("“真上” とみなす upDot の目安")]
    public float pseudoDotAtUp = 0.67f;
    [Tooltip("フォールバック dy の符号補正（+1/-1）")]
    public float pseudoDySign = 1f;

    [Header("Whisper 音声設定 (m)")]
    public float whisperFar = 0.25f;
    public float whisperNear = 0f;

    [Header("通常音声設定 (m)")]
    public float normalFar = 25f;
    public float normalNear = 0f;

    [Header("UI (TextMeshPro)")]
    public TextMeshProUGUI distanceLabel;
    public TextMeshProUGUI orientLabel;
    public TextMeshProUGUI fingerLabel;
    public TextMeshProUGUI profileLabel;
    public TextMeshProUGUI stateLabel;

    [Header("Reply Label (Listener)")]
    [Tooltip("受信安定性の表示先（どちらか/両方未設定でもOK）")]
    public TextMeshProUGUI replyLabelTMP;
    public Text replyLabelUGUI;

    [Header("背景/効果音（任意）")]
    public Image whisperBgImage;
    public Color whisperBgColor = new Color(1, 0, 0, 0.35f);
    public Color normalBgColor = new Color(0, 0, 0, 0);
    public AudioSource sfxSource;
    public AudioClip sfxEnterWhisper;
    public AudioClip sfxExitWhisper;
    [Range(0, 1)] public float sfxEnterVolume = 1f;
    [Range(0, 1)] public float sfxExitVolume = 1f;

    [Header("検出する手")]
    [Tooltip("0=右のみ / 1=左のみ / 2=両手")]
    public int activeHandsMode = 2;

    [Header("グリップで手選択（VR）")]
    public bool enableGripSwitch = true;
    [Tooltip("グリップ押下と判定するしきい値（0〜1）")]
    public float gripPressThreshold = 0.8f;
    private bool _prevGripR = false, _prevGripL = false;
    private int _selectedHand = -1;

    // ───────── Whisper FX（見た目：ビネット）─────────
    [Header("Whisper FX - Vignette (ローカル画面の周辺暗転)")]
    [Tooltip("画面を覆う Image（World Space か Screen Space - Camera 推奨）")]
    public Image vignetteImage;

    [Header("Vignette (Head-Locked)")]
    public RectTransform vignetteRect;        // = vignetteImage.rectTransform を割当
    public float vignetteDistance = 0.09f;                 // 頭の前m
    public Vector2 vignetteSizeMeters = new Vector2(0.28f, 0.18f); // 横m, 縦m
    [Range(0f, 1f)] public float vignetteEnterAlpha = 0.35f;
    [Range(0f, 1f)] public float vignetteExitAlpha = 0.0f;
    public float vignetteFadeInTime = 0.20f;
    public float vignetteFadeOutTime = 0.15f;
    public Color talkerVignetteTint   = new Color(1f, 0.55f, 0.55f, 1f); // 話し手色

    // ───────── Whisper FX（見た目：手首LED）─────────
    [Header("Whisper FX - 手首LED（エミッシブ点灯）")]
    [Tooltip("チェックOFFでLED機能を完全無効（自動で消灯）")]
    public bool enableWristLed = false;
    [Tooltip("手首や手の甲の小さなメッシュの Renderer（Emission 有効推奨）")]
    public Renderer wristLedRenderer;
    public Color wristLedColor = new Color(0.4f, 1f, 0.9f, 1f);
    public float wristLedOnIntensity = 1.6f;
    public float wristLedOffIntensity = 0.0f;
    public float wristLedFadeInTime = 0.20f;
    public float wristLedFadeOutTime = 0.15f;

    // Whisper FX - Icon (Head-Locked HUD)
    [Header("Whisper FX - Icon (Head-Locked HUD)")]
    [Tooltip("左下に出す囁き中アイコン（Sprite を割当）")]
    public Image whisperIconImage;
    public RectTransform whisperIconRect;     // = whisperIconImage.rectTransform を割当
    [Tooltip("視線基準のローカルオフセット[m]（左下は x<0, y<0）")]
    public Vector2 iconOffsetMeters = new Vector2(-0.10f, -0.06f);
    [Tooltip("アイコンの見かけサイズ[m]")]
    public Vector2 iconSizeMeters = new Vector2(0.05f, 0.05f);
    [Range(0f, 1f)] public float iconEnterAlpha = 1f;
    [Range(0f, 1f)] public float iconExitAlpha = 0f;
    [Tooltip("フェードイン/アウト(秒)")]
    public float iconFadeInTime = 0.20f;
    public float iconFadeOutTime = 0.15f;

    // ───────── Haptics ─────────
    [Header("Haptics (Whisper Enter)")]
    public bool enableEnterHaptics = true;
    [Range(0f, 0.5f)] public float hapticsDuration = 0.08f;
    [Range(0f, 1f)] public float hapticsAmplitude = 0.7f;
    public float hapticsFrequency = 180f;
    public bool hapticsFallbackBoth = true;

    [Header("Haptics (Whisper Exit)")]
    public bool enableExitHaptics = true;
    [Range(0f, 0.5f)] public float hapticsExitDuration = 0.05f;
    [Range(0f, 1f)] public float hapticsExitAmplitude = 0.6f;
    public float hapticsExitFrequency = 160f;
    public float doublePulseDelay = 0.10f;
    private int _cachedHapticHand = -1; // -1=不定/両手, 0=右, 1=左
    private bool _cachedHapticBoth = false;

    // ───────── しきい値等 ─────────
    [Header("ヒステリシス（解除側しきい値）")]
    public bool useExitLoosenedThresholds = true;
    public float selfEarThresholdExit = 0.24f;
    public float otherEarThresholdExit = 0.24f;
    public float exitDotMin = 0.0f;
    public float exitDotMax = 1.0f;

    [Header("指伸展 条件")]
    public float fingerCurlThresholdDeg = 40f;
    public int minExtendedFingersEnter = 4;
    public int minExtendedFingersExit = 3;

    // 追加：指伸展 検出オプション（誤検出対策）
    [Tooltip("指ボーン位置が取れない場合に回転フォールバックで代用するか（誤検出が出る場合はOFF推奨）")]
    public bool fingerUseRotationFallback = false;

    [Tooltip("位置ベース判定で使う各指セグメントの最小長[m]。これ未満なら無効指として扱う")]
    public float fingerMinSegmentLen = 0.01f;

    [Header("モード判定（しきい値切替）")]
    public bool enableModeDetection = false;
    public float coverDotSignedThresh = 0.35f;
    public float dyNormThresh = 0.75f;

    [Header("固定しきい値（enableModeDetection=OFF時に使用）")]
    public float fixedDotMin = 0.45f;
    public float fixedDotMax = 0.70f;
    public float fixedDyRawMin = 0.09f;

    [Header("デバッグ：キューブ Interact でトグル")]
    public bool interactToToggle = false;

    // ───────── Ambient Ducking（ローカル・BGM/SFXを一時的に絞る）─────────
    [Header("Ambient Ducking (Local BGM/SFX only)")]
    [Tooltip("囁き中に音量を絞りたい AudioSource（BGM, 環境音など）")]
    public AudioSource[] duckTargets;

    [Range(0f, 1f)]
    [Tooltip("囁き中の相対音量（元音量×この係数） 例: 0.25 で -12dB 相当")]
    public float duckLevel = 0.25f;

    [Tooltip("こもらせる（ローパス）を使うか")]
    public bool duckUseLowpass = true;

    [Range(100, 22000)]
    [Tooltip("ローパス時のカットオフ周波数（Hz） 例: 900Hz")]
    public int duckLowpassCutoff = 900;

    [Tooltip("囁きに入るときのダック時間(秒)")]
    public float duckFadeInTime = 0.15f;

    [Tooltip("囁きを解除するときの戻し時間(秒)")]
    public float duckFadeOutTime = 0.20f;

    [Header("判定しきい値（耳の区別なし：near = min(dR,dL)）")]
    [Tooltip("受信開始（これ以下でON候補）")]
    public float enterDistance = 0.40f;
    [Tooltip("受信終了（これ以上でOFF候補）")]
    public float exitDistance = 0.50f;

    [Header("安定化パラメータ")]
    [Tooltip("ON/OFFに遷移させるのに必要な連続一致回数")]
    public int confirmCount = 3;
    [Tooltip("最後のPingからのタイムアウト秒数（超えたら停止扱い）")]
    public float pingTimeoutSec = 1.5f;

    private const string LOG = "[WhisperCheck]";
    private bool isReceiving;
    private int stableCounter;
    private int unstableCounter;
    private float lastPingTime;

    // ───────── 内部状態（ローカルのみ） ─────────
    private VRCPlayerApi localPlayer;
    private bool isWhispering;
    private bool debugForced = false;

    // Ducking 内部状態
    private float[] _duckOrigVol;
    private AudioLowPassFilter[] _duckLPF;
    private int[] _duckOrigCutoff;
    private bool[] _duckOrigLPFEnabled;
    private float _duckAlpha = 0f, _duckTarget = 0f;

    // ビネット/LED の内部状態
    private float _vigAlpha = 0f, _vigTarget = 0f;

    // アイコン/LED の内部状態
    private float _iconAlpha = 0f, _iconTarget = 0f;
    private float _ledIntensity = 0f, _ledTarget = 0f;
    private MaterialPropertyBlock _mpbLed;

    // ───────── ライフサイクル ─────────
    void Start()
    {
        localPlayer = Networking.LocalPlayer;
        UpdateStateLabel(false);
        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
        if (sfxSource != null) { sfxSource.spatialBlend = 0f; sfxSource.playOnAwake = false; }

        // Vignette 初期化（非表示）
        if (vignetteImage != null)
        {
            var c = vignetteImage.color; c.a = 0f; vignetteImage.color = c;
            _vigAlpha = 0f; _vigTarget = 0f;
        }

        // 手首LED 初期化
        if (wristLedRenderer != null)
        {
            _mpbLed = new MaterialPropertyBlock();
            wristLedRenderer.GetPropertyBlock(_mpbLed);
            _ledIntensity = 0f;
            _ledTarget = enableWristLed ? wristLedOffIntensity : 0f; // 無効時は常に0
            _ApplyWristLed();
        }

        UpdateLabel("受信: 未判定");
        Debug.Log($"{LOG} init enter={enterDistance:F2} exit={exitDistance:F2} confirm={confirmCount} timeout={pingTimeoutSec:F1}");

        // 囁きアイコン初期化
        if (whisperIconImage != null)
        {
            var c = whisperIconImage.color; c.a = 0f;
            whisperIconImage.color = c;
            _iconAlpha = 0f; _iconTarget = 0f;
        }

        // Ducking 初期化
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
    }

    public override void Interact()
    {
        if (!interactToToggle) return;
        _DebugToggleWhisper();
    }

    void Update()
    {
        if (localPlayer == null) return;

        // ───── プロファイル表示（常時更新） ─────
        if (profileLabel != null)
        {
            string hands = (activeHandsMode == 0) ? "Right" : (activeHandsMode == 1) ? "Left" : "Both";
            string sel = (activeHandsMode == 2 && enableGripSwitch)
                            ? (_selectedHand == 0 ? "Right" : _selectedHand == 1 ? "Left" : "Auto")
                            : "N/A";
            profileLabel.text = $"Hands:{hands}  Sel:{sel}  ModeDet:{(enableModeDetection ? "ON" : "OFF")}";
        }

        // デバッグ強制 ON
        if (debugForced)
        {
            if (!isWhispering)
            {
                EnableWhisper();
                TriggerEnterHaptics(_selectedHand, true, true);
            }

            _TickVignetteFade();
            _TickIconFade();
            _TickWristLedFade();
            _TickVignetteTransform();
            _TickIconTransform();
            _TickDuck();

            UpdateBoolTMP(distanceLabel, true, "距離");
            UpdateBoolTMP(fingerLabel, true, "指");
            if (orientLabel != null) orientLabel.text = "掌向き: Debug";
            return;
        }

        // 手の選択
        bool evalRight = (activeHandsMode != 1);
        bool evalLeft = (activeHandsMode != 0);

        // グリップで手選択（両手モードのみ）
        if (enableGripSwitch && activeHandsMode == 2 && localPlayer.IsUserInVR())
        {
            float gripR = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryHandTrigger");
            float gripL = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryHandTrigger");
            bool rNow = gripR >= gripPressThreshold;
            bool lNow = gripL >= gripPressThreshold;
            bool rDown = rNow && !_prevGripR;
            bool lDown = lNow && !_prevGripL;
            _prevGripR = rNow; _prevGripL = lNow;

            if (rDown && !lDown) _selectedHand = 0;
            else if (lDown && !rDown) _selectedHand = 1;
            else if (rDown && lDown) _selectedHand = 0;

            if (_selectedHand == 0) { evalRight = true; evalLeft = false; }
            else if (_selectedHand == 1) { evalRight = false; evalLeft = true; }
            else { evalRight = false; evalLeft = false; }
        }

        // 手ごとの評価
        bool rOK = false, lOK = false, rOrient = false, lOrient = false;
        float rDot = 0f, lDot = 0f, rDy = 0f, lDy = 0f;

        bool loosened = useExitLoosenedThresholds && isWhispering;
        if (evalRight) rOK = EvaluateHand(true, loosened, out rDot, out rDy, out rOrient);
        if (evalLeft)  lOK = EvaluateHand(false, loosened, out lDot, out lDy, out lOrient);

        bool anyWhisper = rOK || lOK;

        // 表示（代表手）
        bool useRight = rOK ? true : (lOK ? false : (evalRight && !evalLeft));
        float showDot = useRight ? rDot : lDot;
        float showDy = useRight ? rDy : lDy;
        bool showOrientOK = useRight ? rOrient : lOrient;

        if (orientLabel != null)
            orientLabel.text = "掌向き" + (loosened ? "(Exit)" : "(Enter)") + ": " + (showOrientOK ? "OK" : "NG") +
                               "  dot=" + showDot.ToString("F2") +
                               "  dy=" + showDy.ToString("F2") + "m" +
                               (enableModeDetection
                                 ? $"  (dot≥{coverDotSignedThresh:F2}, dyNorm≥{dyNormThresh:F2})"
                                 : $"  (dot {fixedDotMin:F2}–{fixedDotMax:F2}, dy≥{fixedDyRawMin:F2})");

        // 囁き状態の切り替え
        if (anyWhisper && !isWhispering)
        {
            EnableWhisper();
            int hapticHand = (_selectedHand >= 0) ? _selectedHand : (useRight ? 0 : 1);
            TriggerEnterHaptics(hapticHand, evalRight, evalLeft);
        }
        else if (!anyWhisper && isWhispering)
        {
            DisableWhisper();
            int hapticHand = (_selectedHand >= 0) ? _selectedHand : (evalRight ? 0 : (evalLeft ? 1 : 0));
            TriggerExitHaptics(hapticHand, evalRight, evalLeft);
        }

        // ネットワーク配信（WhisperReply に委譲）
        if (isWhispering && reply != null) reply.SendCustomEvent("TalkerTick");

        // FX 更新
        _TickVignetteFade();
        _TickIconFade();
        _TickWristLedFade();
        _TickVignetteTransform();
        _TickIconTransform();
        _TickDuck();

        // Pingが途切れたら停止扱い（受信安定性表示）
        if (isReceiving && (Time.time - lastPingTime) > pingTimeoutSec)
        {
            isReceiving = false;
            stableCounter = 0;
            Debug.Log($"{LOG} RECV_STOP reason=timeout");
            UpdateLabel("受信: ❌ (timeout)");
        }
    }

    // ───────────────── 判定ひとまとめ ─────────────────
    private bool EvaluateHand(bool isRight, bool loosened, out float dotSigned, out float dyRaw, out bool orientPass)
    {
        dotSigned = 0f; dyRaw = 0f; orientPass = false;

        int needFingers = loosened ? Mathf.Max(1, minExtendedFingersExit) : Mathf.Max(1, minExtendedFingersEnter);
        bool fingersOK = AreFingersExtended(isRight, needFingers);

        float dotS, dyRawS, dyNormS;
        bool orientSelf = IsPalmFacingEarByThreshold(localPlayer, isRight, loosened, out dotS, out dyRawS, out dyNormS);
        float selfThr = loosened ? selfEarThresholdExit : selfEarThreshold;
        bool distSelf = IsHandNearHead(localPlayer, selfThr, isRight);

        VRCPlayerApi other = FindNearestAny(isRight);
        bool orientOther = false; float dotO = 0f, dyRawO = 0f, dyNormO = 0f; bool distOther = false;
        if (other != null)
        {
            orientOther = IsPalmFacingEarByThreshold(other, isRight, loosened, out dotO, out dyRawO, out dyNormO);
            float otherThr = loosened ? otherEarThresholdExit : otherEarThreshold;
            distOther = IsOtherDistanceWithThreshold(other, isRight, otherThr);
        }

        bool bothDistOK = (distSelf && distOther);
        orientPass = (orientSelf || orientOther);
        bool geomOK = bothDistOK && orientPass;

        UpdateBoolTMP(distanceLabel, bothDistOK, "距離");
        UpdateBoolTMP(fingerLabel, fingersOK, "指");

        dotSigned = orientOther ? dotO : dotS;
        dyRaw = orientOther ? dyRawO : dyRawS;

        return geomOK && fingersOK;
    }

    // ───────────────── 向き＆しきい値 ─────────────────
    private bool IsPalmFacingEarByThreshold(VRCPlayerApi target, bool isRight, bool loosened,
                                            out float dotSigned, out float dyRaw, out float dyNorm)
    {
        Vector3 head = target.GetBonePosition(HumanBodyBones.Head);
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        Quaternion headRot = target.GetBoneRotation(HumanBodyBones.Head);
        Vector3 mouthPos = head + headRot * new Vector3(0f, -0.07f, 0.10f);
        Vector3 handToMouth = (mouthPos - wrist).normalized;

        Vector3 palmNormal = usePalmNormalFromFingers ? ComputePalmNormal(isRight) : ComputePalmNormalFallback(isRight);
        float sign = (palmDotSign >= 0f) ? 1f : -1f;
        dotSigned = sign * Vector3.Dot(palmNormal, handToMouth);

        dyRaw = ComputeDyRaw(isRight);
        dyNorm = GetDyNorm(dyRaw, isRight);

        if (!loosened)
        {
            if (!enableModeDetection)
            {
                bool cover = (dotSigned >= fixedDotMin) && (dotSigned <= fixedDotMax);
                bool vertical = (dyRaw >= fixedDyRawMin);
                return cover && vertical;
            }
            else
            {
                bool cover = dotSigned >= coverDotSignedThresh;
                bool vertical = dyNorm >= dyNormThresh;
                return cover && vertical;
            }
        }
        else
        {
            bool coverExit = (dotSigned >= exitDotMin) && (dotSigned <= exitDotMax);
            if (!enableModeDetection)
            {
                bool vertical = (dyRaw >= fixedDyRawMin);
                return coverExit && vertical;
            }
            else
            {
                bool vertical = (dyNorm >= dyNormThresh);
                return coverExit && vertical;
            }
        }
    }

    private bool IsOtherDistanceWithThreshold(VRCPlayerApi other, bool isRight, float threshold)
    {
        if (debugPassOtherDistance) return true;
        if (other == null) return false;
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 head = other.GetBonePosition(HumanBodyBones.Head);
        return Vector3.Distance(wrist, head) < threshold;
    }

    private bool IsHandNearHead(VRCPlayerApi target, float threshold, bool isRight)
    {
        Vector3 headPos = target.GetBonePosition(HumanBodyBones.Head);
        Vector3 wristPos = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        return Vector3.Distance(headPos, wristPos) < threshold;
    }

    // ── dyRaw を取得（優先度：指ボーン→掌ベース指方向→手軸フォールバック）
    private float ComputeDyRaw(bool isRight)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        bool tipValid; Vector3 tip = GetValidFingerTip(isRight, out tipValid);
        if (tipValid) return tip.y - wrist.y;

        if (!useRotationFallbackForVertical) return 0f;

        Vector3 midP = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
        if (midP != Vector3.zero)
        {
            Vector3 fingerDirFromPalm = (midP - wrist).normalized;
            float upDotPalm = Vector3.Dot(fingerDirFromPalm, Vector3.up);
            float normPalm = (pseudoDotAtUp > 0.01f) ? Mathf.Clamp(upDotPalm / pseudoDotAtUp, -1f, 1f) : upDotPalm;
            return normPalm * pseudoTargetAmplitude * 1f;
        }

        Quaternion handRot = localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 baseAxis = (fingerAxis == 1) ? Vector3.up : (fingerAxis == 2 ? Vector3.right : Vector3.forward);
        Vector3 fingerDir = (handRot * baseAxis).normalized;
        float upDot = Vector3.Dot(fingerDir, Vector3.up);
        float norm = (pseudoDotAtUp > 0.01f) ? Mathf.Clamp(upDot / pseudoDotAtUp, -1f, 1f) : upDot;
        return norm * pseudoTargetAmplitude * pseudoDySign;
    }

    // ── 掌法線：指数/小指/中指prox + 手首 から再構成
    private Vector3 ComputePalmNormal(bool isRight)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 idxP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexProximal  : HumanBodyBones.LeftIndexProximal);
        Vector3 litP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal);
        Vector3 midP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);

        if (wrist == Vector3.zero || idxP == Vector3.zero || litP == Vector3.zero || midP == Vector3.zero)
            return ComputePalmNormalFallback(isRight);

        Vector3 across = isRight ? (idxP - litP) : (litP - idxP);
        Vector3 upPalm = (midP - wrist);
        if (across.sqrMagnitude < 1e-6f || upPalm.sqrMagnitude < 1e-6f)
            return ComputePalmNormalFallback(isRight);

        across.Normalize(); upPalm.Normalize();
        Vector3 n = Vector3.Cross(across, upPalm);
        if (n.sqrMagnitude < 1e-6f) return ComputePalmNormalFallback(isRight);
        return n.normalized;
    }

    // フォールバック：handRot * palmAxis
    private Vector3 ComputePalmNormalFallback(bool isRight)
    {
        Quaternion handRot = localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 axis = (palmAxis == 1) ? Vector3.up : (palmAxis == 2 ? Vector3.right : Vector3.forward);
        Vector3 n = (handRot * axis);
        return (n.sqrMagnitude < 1e-6f) ? Vector3.forward : n.normalized;
    }

    // ───────────────── 指伸展（位置ベース） ─────────────────
    private bool AreFingersExtended(bool isRight, int requiredCount)
    {
        float th = Mathf.Clamp(fingerCurlThresholdDeg, 1f, 90f);
        int count = 0;
        if (IsFingerExtendedByPose(
            isRight ? HumanBodyBones.RightIndexProximal : HumanBodyBones.LeftIndexProximal,
            isRight ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate,
            isRight ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal, th, isRight)) count++;

        if (IsFingerExtendedByPose(
            isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal,
            isRight ? HumanBodyBones.RightMiddleIntermediate : HumanBodyBones.LeftMiddleIntermediate,
            isRight ? HumanBodyBones.RightMiddleDistal : HumanBodyBones.LeftMiddleDistal, th, isRight)) count++;

        if (IsFingerExtendedByPose(
            isRight ? HumanBodyBones.RightRingProximal : HumanBodyBones.LeftRingProximal,
            isRight ? HumanBodyBones.RightRingIntermediate : HumanBodyBones.LeftRingIntermediate,
            isRight ? HumanBodyBones.RightRingDistal : HumanBodyBones.LeftRingDistal, th, isRight)) count++;

        if (IsFingerExtendedByPose(
            isRight ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal,
            isRight ? HumanBodyBones.RightLittleIntermediate : HumanBodyBones.LeftLittleIntermediate,
            isRight ? HumanBodyBones.RightLittleDistal : HumanBodyBones.LeftLittleDistal, th, isRight)) count++;

        bool ok = count >= Mathf.Clamp(requiredCount, 1, 4);

        if (fingerLabel != null)
            fingerLabel.text = $"指({(isRight ? "R" : "L")}): {count}/{Mathf.Clamp(requiredCount,1,4)}";

        return ok;
    }

    private bool IsFingerExtendedByPose(HumanBodyBones prox, HumanBodyBones inter, HumanBodyBones dist, float th, bool isRight)
    {
        Vector3 p0 = localPlayer.GetBonePosition(prox);
        Vector3 p1 = localPlayer.GetBonePosition(inter);
        Vector3 p2 = localPlayer.GetBonePosition(dist);

        float minLen2 = fingerMinSegmentLen * fingerMinSegmentLen;
        bool segOK = (p0 != Vector3.zero && p1 != Vector3.zero && p2 != Vector3.zero)
                     && ((p1 - p0).sqrMagnitude >= minLen2) && ((p2 - p1).sqrMagnitude >= minLen2);

        if (segOK)
        {
            Vector3 v1 = (p1 - p0);
            Vector3 v2 = (p2 - p1);
            float bend = Vector3.Angle(v1, v2); // 0°に近いほど真っ直ぐ
            return bend <= th;
        }

        if (!fingerUseRotationFallback) return false;

        Quaternion rProx = localPlayer.GetBoneRotation(prox);
        Quaternion rDist = localPlayer.GetBoneRotation(dist);

        Vector3 f0 = rProx * Vector3.forward;
        Vector3 f1 = rDist * Vector3.forward;
        if (f0.sqrMagnitude < 1e-6f || f1.sqrMagnitude < 1e-6f) return false;

        float bendFallback = Vector3.Angle(f0, f1);
        return bendFallback <= th;
    }

    // ───────────────── 音声制御 & UI ─────────────────
    private void EnableWhisper()
    {
        localPlayer.SetVoiceDistanceNear(whisperNear);
        localPlayer.SetVoiceDistanceFar(whisperFar);
        localPlayer.SetVoiceLowpass(false);
        isWhispering = true;
        UpdateStateLabel(true);

        if (whisperBgImage != null) whisperBgImage.color = whisperBgColor;
        if (sfxSource != null && sfxEnterWhisper != null) sfxSource.PlayOneShot(sfxEnterWhisper, sfxEnterVolume);

        // 話し手のビネット色（RGB）適用（アルファはフェーダで）
        if (vignetteImage != null)
        {
            var c = vignetteImage.color;
            c.r = talkerVignetteTint.r; c.g = talkerVignetteTint.g; c.b = talkerVignetteTint.b;
            vignetteImage.color = c;
        }

        _iconTarget = iconEnterAlpha;
        _vigTarget = vignetteEnterAlpha;
        _ledTarget = enableWristLed ? wristLedOnIntensity : 0f;

        // ダッキング（話し手）
        _duckTarget = 1f;

        // ネットワーク配信（WhisperReply に委譲）
        if (reply != null) reply.SendCustomEvent("TalkerEnter");
    }

    private void DisableWhisper()
    {
        localPlayer.SetVoiceDistanceNear(normalNear);
        localPlayer.SetVoiceDistanceFar(normalFar);
        localPlayer.SetVoiceLowpass(false);
        isWhispering = false;
        UpdateStateLabel(false);

        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
        if (sfxSource != null && sfxExitWhisper != null) sfxSource.PlayOneShot(sfxExitWhisper, sfxExitVolume);

        _vigTarget = vignetteExitAlpha;
        _iconTarget = iconExitAlpha;
        _ledTarget = 0f;
        _duckTarget = 0f;

        // ネットワーク配信（WhisperReply に委譲）
        if (reply != null) reply.SendCustomEvent("TalkerExit");
    }

    private void UpdateBoolTMP(TextMeshProUGUI tmp, bool ok, string label)
    {
        if (tmp == null) return;
        tmp.text = label + ": " + (ok ? "Yes" : "No");
    }

    private void UpdateStateLabel(bool on)
    {
        if (stateLabel == null) return;
        stateLabel.text = on ? "Whispering" : "Normal";
    }

    // ───────── FX（ビネット）─────────
    private void _TickVignetteFade()
    {
        if (vignetteImage == null) return;
        float dur = (_vigTarget > _vigAlpha) ? Mathf.Max(0.01f, vignetteFadeInTime) : Mathf.Max(0.01f, vignetteFadeOutTime);
        float step = Time.deltaTime / dur;
        _vigAlpha = Mathf.MoveTowards(_vigAlpha, _vigTarget, step);
        var c = vignetteImage.color; c.a = _vigAlpha; vignetteImage.color = c;
    }

    // 頭に固定＆メートル指定サイズにスケーリング（耳寄せはリスナー側でのみ行うためここではセンター固定）
    private void _TickVignetteTransform()
    {
        if (vignetteRect == null || localPlayer == null) return;
        var td = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

        Vector3 planeCenter = td.position + td.rotation * (Vector3.forward * vignetteDistance);
        Quaternion rot = td.rotation * Quaternion.Euler(0f, 180f, 0f);

        vignetteRect.SetPositionAndRotation(planeCenter, rot);

        Vector2 px = vignetteRect.sizeDelta;
        if (px.x < 1f) px = new Vector2(1024f, 1024f);
        float sx = vignetteSizeMeters.x / px.x;
        float sy = vignetteSizeMeters.y / px.y;
        vignetteRect.localScale = new Vector3(sx, sy, 1f);
    }

    // アイコンのフェード
    private void _TickIconFade()
    {
        if (whisperIconImage == null) return;
        float dur = (_iconTarget > _iconAlpha) ? Mathf.Max(0.01f, iconFadeInTime)
                                               : Mathf.Max(0.01f, iconFadeOutTime);
        float step = Time.deltaTime / dur;
        _iconAlpha = Mathf.MoveTowards(_iconAlpha, _iconTarget, step);
        var c = whisperIconImage.color; c.a = _iconAlpha; whisperIconImage.color = c;
    }

    // アイコンのヘッドロック配置
    private void _TickIconTransform()
    {
        if (whisperIconRect == null || localPlayer == null) return;

        var td = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

        Vector3 planeCenter = td.position + td.rotation * (Vector3.forward * vignetteDistance);
        Vector3 localOffset = new Vector3(iconOffsetMeters.x, iconOffsetMeters.y, 0f);
        Vector3 pos = planeCenter + td.rotation * localOffset;

        Quaternion rot = td.rotation * Quaternion.Euler(0f, 180f, 0f);
        whisperIconRect.SetPositionAndRotation(pos, rot);

        Vector2 px = whisperIconRect.sizeDelta;
        if (px.x < 1f) px = new Vector2(256f, 256f);
        float sx = iconSizeMeters.x / px.x;
        float sy = iconSizeMeters.y / px.y;
        whisperIconRect.localScale = new Vector3(sx, sy, 1f);
    }

    // ───────── FX（手首LED）─────────
    private void _TickWristLedFade()
    {
        if (wristLedRenderer == null) return;

        if (!enableWristLed)
        {
            if (_ledIntensity > 0f)
            {
                float stepOff = Time.deltaTime / Mathf.Max(0.01f, wristLedFadeOutTime);
                _ledIntensity = Mathf.MoveTowards(_ledIntensity, 0f, stepOff);
                _ApplyWristLed();
            }
            return;
        }

        float dur = (_ledTarget > _ledIntensity) ? Mathf.Max(0.01f, wristLedFadeInTime)
                                                 : Mathf.Max(0.01f, wristLedFadeOutTime);
        float step = Time.deltaTime / dur;
        _ledIntensity = Mathf.MoveTowards(_ledIntensity, _ledTarget, step);
        _ApplyWristLed();
    }

    private void _ApplyWristLed()
    {
        if (wristLedRenderer == null) return;
        if (_mpbLed == null) _mpbLed = new MaterialPropertyBlock();
        wristLedRenderer.GetPropertyBlock(_mpbLed);

        Color emit = wristLedColor * Mathf.Max(0f, _ledIntensity);
        _mpbLed.SetColor("_EmissionColor", emit);
        _mpbLed.SetColor("_Color", new Color(wristLedColor.r, wristLedColor.g, wristLedColor.b, 1f));
        wristLedRenderer.SetPropertyBlock(_mpbLed);
    }

    // ───────── Haptics ─────────
    private void TriggerEnterHaptics(int selectedHand, bool evalRight, bool evalLeft)
    {
        if (!enableEnterHaptics || localPlayer == null || !localPlayer.IsUserInVR()) return;
        float dur = Mathf.Max(0f, hapticsDuration);
        float amp = Mathf.Clamp01(hapticsAmplitude);
        float freq = Mathf.Max(0f, hapticsFrequency);

        if (selectedHand == 0) { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq); return; }
        if (selectedHand == 1) { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq); return; }

        bool did = false;
        if (evalRight) { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq); did = true; }
        if (!did && evalLeft) { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq); did = true; }
        if (!did && hapticsFallbackBoth)
        {
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq);
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq);
        }
    }

    private void TriggerExitHaptics(int selectedHand, bool evalRight, bool evalLeft)
    {
        if (!enableExitHaptics || localPlayer == null || !localPlayer.IsUserInVR()) return;
        float dur = Mathf.Max(0f, hapticsExitDuration);
        float amp = Mathf.Clamp01(hapticsExitAmplitude);
        float freq = Mathf.Max(0f, hapticsExitFrequency);

        if (selectedHand == 0)
        {
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq);
            _cachedHapticHand = 0; _cachedHapticBoth = false;
            SendCustomEventDelayedSeconds(nameof(_HapticExitAgain), Mathf.Max(0.01f, doublePulseDelay));
            return;
        }
        if (selectedHand == 1)
        {
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq);
            _cachedHapticHand = 1; _cachedHapticBoth = false;
            SendCustomEventDelayedSeconds(nameof(_HapticExitAgain), Mathf.Max(0.01f, doublePulseDelay));
            return;
        }

        bool did = false;
        if (evalRight) { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq); did = true; _cachedHapticHand = 0; _cachedHapticBoth = false; }
        if (!did && evalLeft) { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq); did = true; _cachedHapticHand = 1; _cachedHapticBoth = false; }
        if (!did)
        {
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq);
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq);
            _cachedHapticHand = -1; _cachedHapticBoth = true;
        }
        SendCustomEventDelayedSeconds(nameof(_HapticExitAgain), Mathf.Max(0.01f, doublePulseDelay));
    }

    public void _HapticExitAgain()
    {
        if (!enableExitHaptics || localPlayer == null || !localPlayer.IsUserInVR()) return;
        float dur = Mathf.Max(0f, hapticsExitDuration);
        float amp = Mathf.Clamp01(hapticsExitAmplitude);
        float freq = Mathf.Max(0f, hapticsExitFrequency);

        if (_cachedHapticBoth)
        {
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq);
            localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq);
            return;
        }
        if (_cachedHapticHand == 0) localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, dur, amp, freq);
        else if (_cachedHapticHand == 1) localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, dur, amp, freq);
    }

    // ───────────────── デバッグ ─────────────────
    public void _DebugToggleWhisper()
    {
        if (debugForced)
        {
            debugForced = false;
            if (isWhispering)
            {
                DisableWhisper();
                TriggerExitHaptics(_selectedHand, true, true);
            }
        }
        else
        {
            debugForced = true;
            if (!isWhispering)
            {
                EnableWhisper();
                TriggerEnterHaptics(_selectedHand, true, true);
            }
        }
    }

    // ───────────────── 補助 ─────────────────
    private Vector3 GetValidFingerTip(bool isRight, out bool valid)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        Vector3 tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleDistal : HumanBodyBones.LeftMiddleDistal);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleIntermediate : HumanBodyBones.LeftMiddleIntermediate);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        valid = false; return Vector3.zero;
    }

    private VRCPlayerApi FindNearestAny(bool isRight)
    {
        VRCPlayerApi[] list = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(list);
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        float min = 1e9f; VRCPlayerApi best = null;
        foreach (var p in list)
        {
            if (p == null || !p.IsValid() || p.isLocal) continue;
            float dist = Vector3.Distance(wrist, p.GetBonePosition(HumanBodyBones.Head));
            if (dist < min) { min = dist; best = p; }
        }
        return best;
    }

    private float GetDyNorm(float dyRaw, bool isRight)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 fore = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
        float refLen = (wrist != Vector3.zero && fore != Vector3.zero) ? Vector3.Distance(wrist, fore) : 0.11f;
        if (refLen < 0.07f) refLen = 0.11f;
        float n = dyRaw / refLen; if (n < 0f) n = 0f; if (n > 1.5f) n = 1.5f;
        return n;
    }

    // ===== Listener 安定性表示（WhisperRelay から転送呼び出し）=====
    public void OnWhisperEnter(float dR, float dL)  { OnSample(dR, dL, "ENTER"); }
    public void OnWhisperPing(float dR, float dL, bool keepAlive) { OnSample(dR, dL, keepAlive ? "PING_KEEPALIVE" : "PING"); }
    public void OnWhisperExit()
    {
        lastPingTime = Time.time;
        unstableCounter = confirmCount;
        stableCounter = 0;
        if (isReceiving)
        {
            isReceiving = false;
            Debug.Log($"{LOG} RECV_STOP reason=exit");
            UpdateLabel("受信: ❌ (exit)");
        }
    }

    private void OnSample(float dR, float dL, string tag)
    {
        lastPingTime = Time.time;

        float near = Mathf.Min(dR, dL);            // 耳の区別なし
        bool inRange  = near <= enterDistance;     // ON候補
        bool outRange = near >= exitDistance;      // OFF候補

        Debug.Log($"{LOG} SAMPLE tag={tag} near={near:F2}");

        if (inRange)
        {
            stableCounter++;
            unstableCounter = 0;

            if (!isReceiving && stableCounter >= confirmCount)
            {
                isReceiving = true;
                Debug.Log($"{LOG} RECV_START near={near:F2}");
                UpdateLabel($"受信: ✅ ({near:F2}m)");
            }
            else if (isReceiving)
            {
                UpdateLabel($"受信: ✅ ({near:F2}m)");
            }
        }
        else if (outRange)
        {
            unstableCounter++;
            stableCounter = 0;

            if (isReceiving && unstableCounter >= confirmCount)
            {
                isReceiving = false;
                Debug.Log($"{LOG} RECV_STOP near={near:F2}");
                UpdateLabel($"受信: ❌ ({near:F2}m)");
            }
            else if (!isReceiving)
            {
                UpdateLabel($"受信: ❌ ({near:F2}m)");
            }
        }
        // ヒステリシス帯（enter < near < exit）は状態維持
    }

    private void UpdateLabel(string text)
    {
        if (replyLabelTMP  != null) replyLabelTMP.text  = text;
        if (replyLabelUGUI != null) replyLabelUGUI.text = text;
    }

    // ───────────────── Ducking ─────────────────
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

            // 音量（元音量→元×duckLevel へ補間）
            float v0 = (_duckOrigVol != null && i < _duckOrigVol.Length) ? _duckOrigVol[i] : 1f;
            a.volume = Mathf.Lerp(v0, v0 * duckLevel, _duckAlpha);

            // ローパス（元cutoff→duckLowpassCutoff へ補間）
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
                    // ローパスを使わない設定なら、元の有効状態に戻す
                    if (_duckOrigLPFEnabled != null && i < _duckOrigLPFEnabled.Length)
                        lpf.enabled = _duckOrigLPFEnabled[i];
                }
            }
        }
    }
}
