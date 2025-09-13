using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// WhisperVoiceGate
/// 役割: 話者(=このオブジェクトの Owner)の声を、各クライアント側で
/// - targetPid のクライアントだけ「近距離(whisperNear/Far)」で聞こえる
/// - 第三者は「Far=0（ミュート相当）」
/// にする“ローカル適用装置”
///
/// 変更点(2025-09):
/// - Re-apply 追加（他スクリプトの上書きを抑止）
/// - ターゲット不在時の安全動作を選択できるフラグ追加
/// - デバッグ表示を常時更新可能に
/// - Join/Leave/Ownership/Deserialization で再適用
/// </summary>
public class WhisperVoiceGate : UdonSharpBehaviour
{
    [Header("Whisper distances (heard only by target)")]
    public float whisperNear = 0f;
    public float whisperFar  = 0.30f;

    [Header("Normal distances (when gateOff)")]
    public float normalNear = 0f;
    public float normalFar  = 25f;

    [Header("Behavior when target is missing")]
    [Tooltip("true: ターゲット不在時は全員ミュート(Far=0) / false: 通常距離に戻す")]
    public bool muteAllWhenNoTarget = true;

    [Header("Re-apply (override protection)")]
    [Tooltip("毎フレーム/一定周期で再適用して、他スクリプトの上書きを回避")]
    public bool reapplyEveryFrame = true;
    [Tooltip("reapplyEveryFrame=false のとき、定期的に再適用する間隔(秒)")]
    public float reapplyInterval = 0.25f;

    [Header("Debug Label (optional)")]
    public TextMeshProUGUI debugLabelTMP;
    public Text debugLabelUGUI;
    public bool debugShowEveryFrame = true;
    public float debugUpdateInterval = 0.25f;

    [UdonSynced] public int  targetPid = -1;  // この人だけが聞こえる
    [UdonSynced] public bool gateOn   = false;

    private float _nextReapply;
    private float _nextDebug;

    // ===== Owner（話者）側 API =====
    public void OwnerStart(int targetPlayerId)
    {
        var me = Networking.LocalPlayer; if (me == null) return;
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(me, gameObject);

        targetPid = targetPlayerId;
        gateOn = true;
        RequestSerialization();

        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    public void OwnerUpdateTarget(int targetPlayerId)
    {
        var me = Networking.LocalPlayer; if (me == null) return;
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(me, gameObject);

        targetPid = targetPlayerId;
        RequestSerialization();

        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    public void OwnerStop()
    {
        var me = Networking.LocalPlayer; if (me == null) return;
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(me, gameObject);

        gateOn = false;
        targetPid = -1;
        RequestSerialization();

        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    // ===== ネットワーク/人数変動イベント =====
    public override void OnDeserialization()
    {
        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi _)
    {
        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    public override void OnPlayerJoined(VRCPlayerApi _)
    {
        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    public override void OnPlayerLeft(VRCPlayerApi left)
    {
        // ターゲットが抜けたら安全側へ
        if (left != null && left.playerId == targetPid)
        {
            targetPid = -1;
            if (Networking.IsOwner(gameObject)) RequestSerialization();
        }
        _ApplyLocal();
        _UpdateDebugLabel(true);
    }

    void Start()
    {
        _ApplyLocal();
        _UpdateDebugLabel(true);
        _nextReapply = Time.time + reapplyInterval;
        _nextDebug   = Time.time + debugUpdateInterval;
    }

    void Update()
    {
        // 定期再適用（他のUdonに上書きされても勝つ）
        if (reapplyEveryFrame || Time.time >= _nextReapply)
        {
            _ApplyLocal();
            _nextReapply = Time.time + reapplyInterval;
        }

        // デバッグ表示
        if (debugShowEveryFrame && Time.time >= _nextDebug)
        {
            _UpdateDebugLabel(false);
            _nextDebug = Time.time + debugUpdateInterval;
        }
    }

    // ===== 実装本体（各クライアントが “Ownerの声” をどう聞くかを決める） =====
    private void _ApplyLocal()
    {
        var owner = Networking.GetOwner(gameObject);   // 話者A
        if (owner == null || !owner.IsValid()) return;

        var local = Networking.LocalPlayer;
        if (local == null) return;

        // Gate OFF → 全員通常へ
        if (!gateOn)
        {
            owner.SetVoiceDistanceNear(normalNear);
            owner.SetVoiceDistanceFar(normalFar);
            owner.SetVoiceLowpass(false);
            return;
        }

        var tgt = (targetPid >= 0) ? VRCPlayerApi.GetPlayerById(targetPid) : null;
        bool targetValid = (tgt != null && tgt.IsValid());

        if (!targetValid)
        {
            // ターゲット不在時の安全動作
            if (muteAllWhenNoTarget)
            {
                owner.SetVoiceDistanceNear(0f);
                owner.SetVoiceDistanceFar(0f);
                owner.SetVoiceLowpass(false);
            }
            else
            {
                owner.SetVoiceDistanceNear(normalNear);
                owner.SetVoiceDistanceFar(normalFar);
                owner.SetVoiceLowpass(false);
            }
            return;
        }

        if (local.playerId == targetPid)
        {
            // 受信者だけ近距離で聞こえる
            owner.SetVoiceDistanceNear(whisperNear);
            owner.SetVoiceDistanceFar(whisperFar);
            owner.SetVoiceLowpass(false);
        }
        else
        {
            // 第三者はミュート相当
            owner.SetVoiceDistanceNear(0f);
            owner.SetVoiceDistanceFar(0f);
            owner.SetVoiceLowpass(false);
        }
    }

    private void _UpdateDebugLabel(bool force)
    {
        if (debugLabelTMP == null && debugLabelUGUI == null) return;
        if (!force && !debugShowEveryFrame) return;

        var owner = Networking.GetOwner(gameObject);
        var local = Networking.LocalPlayer;
        var targetPlayer = (targetPid >= 0) ? VRCPlayerApi.GetPlayerById(targetPid) : null;

        string ownerStr = (owner != null && owner.IsValid()) ? $"{owner.playerId}:{owner.displayName}" : "(none)";
        string targetStr = (targetPlayer != null && targetPlayer.IsValid())
            ? $"{targetPid}:{targetPlayer.displayName}"
            : (targetPid >= 0 ? targetPid.ToString() : "-");
        string you = (local != null && targetPid == local.playerId && gateOn) ? "  ←YOU" : "";
        string safe = muteAllWhenNoTarget ? "muteAllWhenNoTarget=ON" : "muteAllWhenNoTarget=OFF";

        string text =
            $"[Gate '{name}']\n" +
            $"On={gateOn}  {safe}\n" +
            $"Owner={ownerStr}\n" +
            $"Target={targetStr}{you}";

        if (debugLabelTMP != null)  debugLabelTMP.text  = text;
        if (debugLabelUGUI != null) debugLabelUGUI.text = text;
    }
}
