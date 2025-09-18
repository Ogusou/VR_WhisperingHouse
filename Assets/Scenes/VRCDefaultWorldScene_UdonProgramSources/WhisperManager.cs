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
    public Color talkerVignetteTint = new Color(1f, 0.55f, 0.55f, 1f); // 話し手色


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

    // 追加（インスペクタで GatePool を割り当て）
    [Header("Voice Gate")]
    public WhisperGatePool gatePool;
    private WhisperVoiceGate _myGate;
    private int _lastTargetPid = -1;
    private float _nextGateUpdateTime = 0f;


    // ===== Target selection tuning =====
    [Header("Target Selection")]
    [Tooltip("候補として認める最大距離(m)（otherEarThreshold と同程度〜少し広め推奨）")]
    public float targetCandidateMaxDist = 0.60f;
    [Tooltip("新候補が現在ターゲットよりどれだけ近ければ即切替するか（例0.85=15%近い）")]
    [Range(0.5f, 1.0f)] public float targetPreferCloserFactor = 0.85f;
    [Tooltip("新候補が連続でこの回数 合格したら切替（揺れ対策）")]
    public int targetSwitchConfirm = 3;
    [Tooltip("最後の切替からこの秒数は粘る（スティッキー）")]
    public float targetStickMinSeconds = 0.5f;

    private int _switchCounter = 0;
    private float _lastSwitchTime = -999f;

    // ▼ 追加フィールド
    [Header("Reply Assist (after being whispered)")]
    [Tooltip("直近で囁かれたあと、この秒数は“自分の口元だけ”でEnterを許可")]
    public float replyAssistWindow = 3.0f;
    public bool enableReplyAssist = true;
    private float _replyAssistUntil = 0f;

    // 便宜関数
    private bool InReplyAssist() => enableReplyAssist && (Time.time < _replyAssistUntil);


    [Header("Whisper Talker Debounce")]
    [Tooltip("囁きOFFと判定されても、この秒数までは継続（一瞬の途切れを無視）")]
    public float talkerExitGraceSec = 0.35f;

    [Tooltip("ターゲット切替は、この回数連続で同じ候補になったら確定")]

    private float _talkerHoldUntil = 0f;
    private int _switchCount = 0;
    private int _candidatePid = -1;


    [Header("Hand Selection Strategy")]
    [Tooltip("頭(口)の周囲に入っている手だけを常時評価（ひそひそ開始/返答どちらも）")]
    public bool autoPickHandByHeadBubble = true;

    [Tooltip("両手がバブルに入っているときの優先：true=掌向き(dot)優先 / false=口との距離優先")]
    public bool preferBetterDotWhenBoth = true;

    [Tooltip("グリップによる明示選択がある場合はそれを優先（ただしバブル内にある手のみ有効）")]
    public bool preferGripIfSelected = true;

    [Tooltip("口-手 距離(Enter半径)")]
    public float headBubbleRadiusEnter = 0.18f;

    [Tooltip("口-手 距離(Exit半径) ※ヒステリシス用に Enter より少し大きく")]
    public float headBubbleRadiusExit = 0.24f;

    [Tooltip("バブル判定の粘り（秒）")]
    public float handGateStickSeconds = 0.25f;

    [Tooltip("両手イン時に手を切り替える最小間隔（秒）")]
    public float handSwitchCooldown = 0.25f;

        // Hand Selection Strategy の近くに追加
    [Header("Hand Lock (during whisper)")]
    [Tooltip("入室を確定させた手を一定時間ロック（両手イン時の勝手な切替を防止）")]
    public bool lockActiveHandWhileWhispering = true;

    [Tooltip("入室後この秒数は必ずロックを維持")]
    public float handLockMinSeconds = 0.60f;

    [Tooltip("ロックしている手がバブルExit外判定になった連続フレーム数の閾値（これを超えたらロック解除可）")]
    public int handLockReleaseConfirm = 3;

    // 内部
    private int   _lockedHand = -1;      // -1=未ロック, 0=右, 1=左
    private float _handLockUntil = 0f;   // 時間で強制ロック維持
    private int _handOutCount = 0;     // 連続アウト計数


    // 内部
    private float _rInUntil, _lInUntil;
    private int   _autoChosenHand = -1; // -1=未選, 0=右, 1=左
    private float _lastHandSwitchTime = -999f;

    // ★追加：実際にWhisperを“開始”させた手（0=右,1=左,-1=不定）
    private int _activeWhisperHand = -1;



    // 受け手推定のために保持
    private VRCPlayerApi _FindNearestListener()
    {
        // 右手/左手どちらでも近い方でOK（実装簡略）
        var a = FindNearestAny(true);
        if (a != null) return a;
        return FindNearestAny(false);
    }

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
        bool evalLeft  = (activeHandsMode != 0);

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

            if (_selectedHand == 0) { evalRight = true;  evalLeft = false; }
            else if (_selectedHand == 1) { evalRight = false; evalLeft = true; }
            else { evalRight = false; evalLeft = false; }
        }

        // ── 追加：口バブルで常時“使う手”を自動決定（返答時だけでなく開始時も） ──
        if (autoPickHandByHeadBubble)
        {
            bool bubbleloosened = useExitLoosenedThresholds && isWhispering;
            bool inR = IsWristInMouthBubble(true,  bubbleloosened);
            bool inL = IsWristInMouthBubble(false, bubbleloosened);

            // 両手が入っている場合の自動候補
            int want = -1;
            if (inR ^ inL) want = inR ? 0 : 1;
            else if (inR && inL)
            {
                if (preferBetterDotWhenBoth)
                {
                    float dotR = LooseDotToMouth(true);
                    float dotL = LooseDotToMouth(false);
                    want = (dotR >= dotL) ? 0 : 1;
                }
                else
                {
                    float dR = DistWristToMouth(true);
                    float dL = DistWristToMouth(false);
                    want = (dR <= dL) ? 0 : 1;
                }
            }

            int useHand = -1;

            // グリップ明示選択があれば優先（ただし“口バブル内”の手のみ有効）
            if (preferGripIfSelected && enableGripSwitch && _selectedHand >= 0)
            {
                if ((_selectedHand == 0 && inR) || (_selectedHand == 1 && inL))
                    useHand = _selectedHand;
                else
                    useHand = -1; // 選ばれていてもバブル外なら無効
            }
            else
            {
                // オート選択（クールダウンで揺れ抑制）
                if (want >= 0 && (Time.time - _lastHandSwitchTime) >= handSwitchCooldown)
                {
                    _autoChosenHand    = want;
                    _lastHandSwitchTime = Time.time;
                }
                useHand = _autoChosenHand;
            }

            // ★ロック中は基本その手だけを評価
            if (lockActiveHandWhileWhispering && isWhispering && _lockedHand >= 0)
            {
                bool inLocked = IsWristInMouthBubble(_lockedHand == 0, bubbleloosened);

                // 連続でExit外になったフレームをカウント
                if (!inLocked) _handOutCount++;
                else           _handOutCount = 0;

                bool mustKeepLock =
                    (Time.time < _handLockUntil)      // 最低ロック時間中
                    || inLocked                       // まだバブル内
                    || (_handOutCount < Mathf.Max(1, handLockReleaseConfirm)); // 少しの外れは許容

                if (mustKeepLock)
                {
                    useHand = _lockedHand;            // 代表手を固定
                    _autoChosenHand = _lockedHand;    // 内部状態も合わせておく
                }
                else
                {
                    // ロック解除（以降は通常の自動選択に戻す）
                    _lockedHand = -1;
                    _handOutCount = 0;
                    _handLockUntil = 0f;
                }
            }


            // “評価する手”を最終決定（他方は完全に無視＝干渉なし）
            evalRight = (useHand == 0);
            evalLeft  = (useHand == 1);
        }

        // ───── 手ごとの評価 ─────
        bool rOK = false, lOK = false, rOrient = false, lOrient = false;
        float rDot = 0f, lDot = 0f, rDy = 0f, lDy = 0f;

        bool loosened = useExitLoosenedThresholds && isWhispering;
        if (evalRight) rOK = EvaluateHand(true,  loosened, out rDot, out rDy, out rOrient);
        if (evalLeft)  lOK = EvaluateHand(false, loosened, out lDot, out lDy, out lOrient);

        bool anyWhisper = rOK || lOK;

        // 表示（代表手）
        bool useRight = rOK ? true : (lOK ? false : (evalRight && !evalLeft));
        float showDot = useRight ? rDot : lDot;
        float showDy  = useRight ? rDy  : lDy;
        bool showOrientOK = useRight ? rOrient : lOrient;

        // このフレームで“有効だった手”を決定
        int activeHand = -1;
        if (rOK && !lOK) activeHand = 0;                     // 右のみOK
        else if (lOK && !rOK) activeHand = 1;                // 左のみOK
        else if (rOK && lOK) activeHand = (useRight ? 0 : 1);// 両方OKなら代表手に合わせる

        // ---- ここから デバウンス（粘り）＋状態遷移 ----
        // ONが取れている間は毎フレームExit猶予を延長
        if (anyWhisper)
        {
            _talkerHoldUntil = Time.time + talkerExitGraceSec;
        }

        // 今フレームの希望状態（一瞬のNGではOFFにしない）
        bool wantWhisper = anyWhisper || (isWhispering && Time.time < _talkerHoldUntil);

        // 状態切替（★旧ブロックは削除してこの1つだけに）
        if (wantWhisper && !isWhispering)
        {
            EnableWhisper();

            // 実際に判定OKだった手でハプティクス
            _activeWhisperHand = activeHand;
            TriggerEnterHaptics(_activeWhisperHand, evalRight, evalLeft);
            // 実際に入室を成立させた手でロック開始
        if (lockActiveHandWhileWhispering && _activeWhisperHand >= 0)
        {
            _lockedHand = _activeWhisperHand;
            _handLockUntil = Time.time + Mathf.Max(0.1f, handLockMinSeconds);
            _handOutCount = 0;
        }
        }
        else if (!wantWhisper && isWhispering)
        {
            DisableWhisper();
            _lockedHand = -1;
            _handOutCount = 0;
            _handLockUntil = 0f;

            // 入室時に使った手を優先して退出ハプティクス
            int hapticHand = (_activeWhisperHand >= 0)
                ? _activeWhisperHand
                : (evalRight ? 0 : (evalLeft ? 1 : -1));

            TriggerExitHaptics(hapticHand, evalRight, evalLeft);
            _activeWhisperHand = -1; // リセット
        }

        // デバッグ表示
        if (orientLabel != null)
            orientLabel.text =
                $" 掌向き" + (loosened ? "(Exit)" : "(Enter)") + ": " + (showOrientOK ? "OK" : "NG") +
                "  dot=" + showDot.ToString("F2") +
                "  dy="  + showDy.ToString("F2") + "m" +
                (enableModeDetection
                    ? $"  (dot≥{coverDotSignedThresh:F2}, dyNorm≥{dyNormThresh:F2})"
                    : $"  (dot {fixedDotMin:F2}–{fixedDotMax:F2}, dy≥{fixedDyRawMin:F2})");

        // （このあと）ネットワーク配信・FX更新は従来どおり
        if (isWhispering && reply != null) reply.SendCustomEvent("TalkerTick");
        _TickVignetteFade();
        _TickIconFade();
        _TickVignetteTransform();
        _TickIconTransform();
        _TickDuck();


        // ───── ここから Gate ターゲットの“粘り + 確定切替”ロジック ─────
        if (isWhispering && Time.time >= _nextGateUpdateTime)
        {
            _nextGateUpdateTime = Time.time + 0.25f;

            // ★ 追記：Gate を遅延取得（入室直後などで null の場合に備える）
            if (_myGate == null && gatePool != null)
                _myGate = gatePool.GetGateForLocal();

            // 代表手の優先度（検出状況に応じて手を優先）
            bool preferRight = (activeHandsMode != 1) && (rOK || (useRight && evalRight));
            bool preferLeft  = (activeHandsMode != 0) && (lOK || (!useRight && evalLeft));

            // ★ 追記：今しがた Gate を掴んだ/消えていた場合にブートストラップ
            if (_myGate != null && !_myGate.gateOn)
            {
                int bootTarget = (_lastTargetPid >= 0)
                    ? _lastTargetPid
                    : _SelectTargetSmart(preferRight, preferLeft, /*fallbackNearest:*/ true);

                _myGate.OwnerStart(bootTarget);
                _lastTargetPid  = bootTarget;
                _lastSwitchTime = Time.time;
                _switchCounter  = 0;
            }

            // 現在のジェスチャーに基づくスマート候補を取得（フォールバックなし）
            int newCandidate = _SelectTargetSmart(preferRight, preferLeft, /*fallbackNearest:*/ false);

            if (_IsTargetStillValid(_lastTargetPid))
            {
                if (newCandidate >= 0 && newCandidate != _lastTargetPid)
                {
                    // 有意に近い or 規定回数連続で検出 → 切替確定
                    if (_IsSignificantlyCloser(newCandidate, _lastTargetPid) || _switchCounter >= targetSwitchConfirm)
                    {
                        if (_myGate != null) _myGate.OwnerUpdateTarget(newCandidate);
                        _lastTargetPid  = newCandidate;
                        _lastSwitchTime = Time.time;
                        _switchCounter  = 0;
                    }
                    else
                    {
                        _switchCounter++; // まだ粘る
                    }
                }
                else
                {
                    _switchCounter = 0; // 維持
                    // まれな競合対策として再適用（Owner が自分である限り無害）
                    if (_myGate != null && _lastTargetPid >= 0) _myGate.OwnerUpdateTarget(_lastTargetPid);
                }
            }
            else
            {
                // 既存ターゲットが無効化された（遠ざかった/離脱など）
                if (newCandidate >= 0 && newCandidate != _lastTargetPid)
                {
                    if (_myGate != null) _myGate.OwnerUpdateTarget(newCandidate);
                    _lastTargetPid  = newCandidate;
                    _lastSwitchTime = Time.time;
                    _switchCounter  = 0;
                }
                else
                {
                    // 候補が無くても維持（-1 にはしない）
                    // まれな競合対策として再適用
                    if (_myGate != null && _lastTargetPid >= 0) _myGate.OwnerUpdateTarget(_lastTargetPid);
                }
            }
        }

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
   // 置き換え
// EvaluateHand を差し替え（ロジックだけ）
private bool EvaluateHand(bool isRight, bool loosened, out float dotSigned, out float dyRaw, out bool orientPass)
{
    dotSigned = 0f; dyRaw = 0f; orientPass = false;

    // 追加：口バブル外の手は即NG（常時）
    if (autoPickHandByHeadBubble)
    {
        bool inBubble = IsWristInMouthBubble(isRight, loosened);
        if (!inBubble) return false;
    }


    int needFingers = loosened ? Mathf.Max(1, minExtendedFingersExit) : Mathf.Max(1, minExtendedFingersEnter);
    bool fingersOK = AreFingersExtended(isRight, needFingers);

    // 自分口元に対する判定（共通で計算）
    float dotS, dyRawS, dyNormS;
    bool orientSelf = IsPalmFacingEarByThreshold(localPlayer, isRight, loosened, out dotS, out dyRawS, out dyNormS);
    float selfThr   = loosened ? selfEarThresholdExit : selfEarThreshold;
    bool distSelf   = IsHandNearHead(localPlayer, selfThr, isRight);

    // ▼ ここが肝：返答支援ウィンドウ中だけ“自分の口元のみ”でOKにする
    if (InReplyAssist())
    {
        orientPass = orientSelf;
        bool geomOK = distSelf && orientPass;

        UpdateBoolTMP(distanceLabel, distSelf, "距離(自口/Assist)");
        UpdateBoolTMP(fingerLabel,  fingersOK, "指");

        dotSigned = dotS;
        dyRaw     = dyRawS;
        return geomOK && fingersOK;
    }

    // ── 通常時：従来どおり「相手との距離＆向き」も必要 ──
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
    bool geomOK2 = bothDistOK && orientPass;

    UpdateBoolTMP(distanceLabel, bothDistOK, "距離");
    UpdateBoolTMP(fingerLabel,  fingersOK, "指");

    dotSigned = orientOther ? dotO : dotS;
    dyRaw     = orientOther ? dyRawO : dyRawS;

    return geomOK2 && fingersOK;
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
        Vector3 idxP = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexProximal : HumanBodyBones.LeftIndexProximal);
        Vector3 litP = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal);
        Vector3 midP = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);

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
            fingerLabel.text = $"指({(isRight ? "R" : "L")}): {count}/{Mathf.Clamp(requiredCount, 1, 4)}";

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

        isWhispering = true;
        _talkerHoldUntil = Time.time + talkerExitGraceSec; // 入った直後は少し粘る
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

        // ダッキング（話し手）
        _duckTarget = 1f;
        // EnableWhisper() の中（先頭～ターゲット決定までの直前）
        if (gatePool != null && _myGate == null)
            _myGate = gatePool.GetGateForLocal();

        int target = -1;
        var other = _FindNearestListener();
        if (other != null) target = other.playerId;

        // Gate が取れていれば OwnerStart（内部で SetOwner & RequestSerialization）
        if (_myGate != null) _myGate.OwnerStart(target);
        _lastTargetPid = target;

        if (reply != null) reply.SendCustomEvent("TalkerEnter");
    }

    private void DisableWhisper()
    {

        isWhispering = false;
        UpdateStateLabel(false);

        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
        if (sfxSource != null && sfxExitWhisper != null) sfxSource.PlayOneShot(sfxExitWhisper, sfxExitVolume);

        _vigTarget = vignetteExitAlpha;
        _iconTarget = iconExitAlpha;
        _duckTarget = 0f;

        // --- Gate 停止 ---
        if (_myGate != null) _myGate.OwnerStop();
        _lastTargetPid = -1;

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
   public void OnWhisperEnter(float dR, float dL) {
    // 既存処理の前後どちらでもOK
    _replyAssistUntil = Time.time + replyAssistWindow;
    OnSample(dR, dL, "ENTER"); // 既存
}
    public void OnWhisperPing(float dR, float dL, bool keepAlive) {
    _replyAssistUntil = Time.time + replyAssistWindow; // keep 延長
    OnSample(dR, dL, keepAlive ? "PING_KEEPALIVE" : "PING"); // 既存
}
   public void OnWhisperExit() {
    _replyAssistUntil = 0f; // 窓を閉じる
    // 既存処理
    lastPingTime = Time.time;
    unstableCounter = confirmCount;
    stableCounter = 0;
    if (isReceiving) { isReceiving = false; UpdateLabel("受信: ❌ (exit)"); }
}

    private void OnSample(float dR, float dL, string tag)
    {
        lastPingTime = Time.time;

        float near = Mathf.Min(dR, dL);            // 耳の区別なし
        bool inRange = near <= enterDistance;     // ON候補
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
        if (replyLabelTMP != null) replyLabelTMP.text = text;
        if (replyLabelUGUI != null) replyLabelUGUI.text = text;
    }

    // 候補を賢く選ぶ：掌向きOK＆距離しきい値内の中で最短を返す。なければ fallbackNearest が true のとき最寄り。
    private int _SelectTargetSmart(bool preferRight, bool preferLeft, bool fallbackNearest)
    {
        var lp = Networking.LocalPlayer; if (lp == null) return -1;

        VRCPlayerApi[] list = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(list);

        // 手の基準位置
        Vector3 wristR = lp.GetBonePosition(HumanBodyBones.RightHand);
        Vector3 wristL = lp.GetBonePosition(HumanBodyBones.LeftHand);

        float bestDist = 1e9f;
        int bestPid = -1;

        for (int i = 0; i < list.Length; i++)
        {
            var p = list[i];
            if (p == null || !p.IsValid() || p.isLocal) continue;

            // 両手を評価（使う手だけ見る）
            float dBestForThis = 1e9f;
            bool okForThis = false;

            if (preferRight && wristR != Vector3.zero)
            {
                float d = Vector3.Distance(wristR, p.GetBonePosition(HumanBodyBones.Head));
                bool orientOK; float dotS, dyRaw, dyNorm;
                orientOK = IsPalmFacingEarByThreshold(p, true, false, out dotS, out dyRaw, out dyNorm); // enter条件
                if (orientOK && d <= targetCandidateMaxDist)
                {
                    okForThis = true;
                    if (d < dBestForThis) dBestForThis = d;
                }
                else if (fallbackNearest && d < dBestForThis) dBestForThis = d;
            }

            if (preferLeft && wristL != Vector3.zero)
            {
                float d = Vector3.Distance(wristL, p.GetBonePosition(HumanBodyBones.Head));
                bool orientOK; float dotS, dyRaw, dyNorm;
                orientOK = IsPalmFacingEarByThreshold(p, false, false, out dotS, out dyRaw, out dyNorm);
                if (orientOK && d <= targetCandidateMaxDist)
                {
                    okForThis = true;
                    if (d < dBestForThis) dBestForThis = d;
                }
                else if (fallbackNearest && d < dBestForThis) dBestForThis = d;
            }

            // 合格者を優先、それ以外は fallbackNearest の場合のみ比較
            if (okForThis)
            {
                if (dBestForThis < bestDist) { bestDist = dBestForThis; bestPid = p.playerId; }
            }
            else if (fallbackNearest)
            {
                if (dBestForThis < bestDist) { bestDist = dBestForThis; bestPid = p.playerId; }
            }
        }

        return bestPid;
    }

    // 現在のターゲットはまだ“囁ける条件”を満たしているか？
    private bool _IsTargetStillValid(int pid)
    {
        if (pid < 0) return false;
        var lp = Networking.LocalPlayer; if (lp == null) return false;
        var p = VRCPlayerApi.GetPlayerById(pid); if (p == null || !p.IsValid()) return false;

        // 右/左いずれかで条件OKなら継続
        float ds; float dr; float dn;
        bool okR = IsPalmFacingEarByThreshold(p, true, false, out ds, out dr, out dn)
                   && Vector3.Distance(lp.GetBonePosition(HumanBodyBones.RightHand), p.GetBonePosition(HumanBodyBones.Head)) <= targetCandidateMaxDist;
        bool okL = IsPalmFacingEarByThreshold(p, false, false, out ds, out dr, out dn)
                   && Vector3.Distance(lp.GetBonePosition(HumanBodyBones.LeftHand), p.GetBonePosition(HumanBodyBones.Head)) <= targetCandidateMaxDist;

        return okR || okL;
    }

            // 口のワールド座標（IsPalmFacingEarByThreshold と同じオフセットを関数化）
        private Vector3 GetMouthPosition(VRCPlayerApi who)
        {
            Vector3 head   = who.GetBonePosition(HumanBodyBones.Head);
            Quaternion rot = who.GetBoneRotation(HumanBodyBones.Head);
            return head + rot * new Vector3(0f, -0.07f, 0.10f);
        }

        private float DistWristToMouth(bool isRight)
        {
            Vector3 mouth = GetMouthPosition(localPlayer);
            Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (mouth == Vector3.zero || wrist == Vector3.zero) return 1e9f;
            return Vector3.Distance(mouth, wrist);
        }

        private bool IsWristInMouthBubble(bool isRight, bool loosened)
        {
            float rEnter = headBubbleRadiusEnter;
            float rExit  = headBubbleRadiusExit;
            float rUse   = loosened ? rExit : rEnter;  // ヒステリシス

            float d = DistWristToMouth(isRight);
            bool insideNow = d <= rUse;

            // 粘り（手が一瞬だけ外れても揺れないように）
            if (insideNow)
            {
                if (isRight) _rInUntil = Time.time + handGateStickSeconds;
                else         _lInUntil = Time.time + handGateStickSeconds;
                return true;
            }
            else
            {
                return isRight ? (Time.time < _rInUntil) : (Time.time < _lInUntil);
            }
        }

        // 両手イン時に“それっぽさ”を比べるための簡易 dot
        private float LooseDotToMouth(bool isRight)
        {
            Vector3 mouth = GetMouthPosition(localPlayer);
            Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (mouth == Vector3.zero || wrist == Vector3.zero) return -1f;

            Vector3 toMouth = (mouth - wrist).normalized;
            Vector3 palm    = usePalmNormalFromFingers ? ComputePalmNormal(isRight) : ComputePalmNormalFallback(isRight);
            return palmDotSign * Vector3.Dot(palm, toMouth);
        }

        // （返信アシスト用）口-手の距離判定
        private bool IsHandNearMouth(VRCPlayerApi who, float threshold, bool isRight)
        {
            Vector3 mouth = GetMouthPosition(who);
            Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (mouth == Vector3.zero || wrist == Vector3.zero) return false;
            return Vector3.Distance(mouth, wrist) < threshold;
        }


    // 新候補が現ターゲットより十分近いか？
    private bool _IsSignificantlyCloser(int newPid, int currentPid)
    {
        if (newPid < 0 || currentPid < 0) return true;
        var lp = Networking.LocalPlayer; if (lp == null) return true;

        var pn = VRCPlayerApi.GetPlayerById(newPid);
        var pc = VRCPlayerApi.GetPlayerById(currentPid);
        if (pn == null || !pn.IsValid()) return false;
        if (pc == null || !pc.IsValid()) return true;

        float dNew = Mathf.Min(
            Vector3.Distance(lp.GetBonePosition(HumanBodyBones.RightHand), pn.GetBonePosition(HumanBodyBones.Head)),
            Vector3.Distance(lp.GetBonePosition(HumanBodyBones.LeftHand), pn.GetBonePosition(HumanBodyBones.Head))
        );
        float dCur = Mathf.Min(
            Vector3.Distance(lp.GetBonePosition(HumanBodyBones.RightHand), pc.GetBonePosition(HumanBodyBones.Head)),
            Vector3.Distance(lp.GetBonePosition(HumanBodyBones.LeftHand), pc.GetBonePosition(HumanBodyBones.Head))
        );

        if (dCur <= 0.0001f) return true;
        return (dNew <= dCur * targetPreferCloserFactor);
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
