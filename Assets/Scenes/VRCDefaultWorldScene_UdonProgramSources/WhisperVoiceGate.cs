using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class WhisperVoiceGate : UdonSharpBehaviour
{
    [UdonSynced] public int assignedPid = -1;  // このゲートの「割当て所有者」
    [UdonSynced] public int targetPid   = -1;  // 受信者
    [UdonSynced] public bool gateOn     = false;

    [Header("Assigned index (pool sets it)")]
    public int assignedIndex = -1;

    public TextMeshProUGUI debugLabelTMP;
    public Text           debugLabelUGUI;
    public bool  debugShowEveryFrame = false;
    private float _nextDebugRefresh;
    
   // 追加: インスペクタで調整できるように
[Header("Voice distances & gain")]
public float normalNear   = 0f;
public float normalFar    = 25f;
public float normalGain   = 15f;   // 既定相当

public float whisperNear  = 0f; 
public float whisperFar   = 25f; // いま使っている値
public float whisperGain  = 15f;   // 少しだけ底上げ（18–24 で調整）

    // 追加: 再適用の間隔（保険）
    [Header("Reapply (watchdog)")]
    [Tooltip("囁きON中にこの間隔でSetVoiceDistanceFarを再適用します")]
    public float reapplyInterval = 0.5f;
        private float _nextReapply;


    public void OwnerStart(int targetPlayerId)
    {
        var me = Networking.LocalPlayer; if (me == null) return;
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(me, gameObject);

        assignedPid = me.playerId;
        targetPid = targetPlayerId;
        gateOn = true;
        RequestSerialization();
        _ApplyLocal();
        _UpdateDebugLabel();
    }

    public void OwnerUpdateTarget(int targetPlayerId)
    {
        if (!Networking.IsOwner(gameObject)) return;
        var me = Networking.LocalPlayer;
        if (me != null && assignedPid != me.playerId) assignedPid = me.playerId;

        targetPid = targetPlayerId;
        gateOn    = true;
        RequestSerialization();
        _ApplyLocal();
        _UpdateDebugLabel();
    }

    public void OwnerStop()
    {
        if (!Networking.IsOwner(gameObject)) return;
        gateOn = false; // assignedPid は維持
        RequestSerialization();
        _ApplyLocal();
        _UpdateDebugLabel();
    }

    public override void OnDeserialization()                { _ApplyLocal(); _UpdateDebugLabel(); }
    public override void OnOwnershipTransferred(VRCPlayerApi _) { _ApplyLocal(); _UpdateDebugLabel(); }
    void Start()                                            { _ApplyLocal(); _UpdateDebugLabel(); }

    void Update()
{
    // 既存のデバッグ更新
    if (debugShowEveryFrame)
    {
        if (Time.time >= _nextDebugRefresh)
        {
            _nextDebugRefresh = Time.time + 0.25f;
            _UpdateDebugLabel();
        }
    }

    // 追加: 囁きON中は保険として定期再適用
    if (gateOn && Time.time >= _nextReapply)
    {
        _nextReapply = Time.time + Mathf.Max(0.1f, reapplyInterval);
        _ApplyLocal();
    }
}

    // ▼ 各クライアントが“自分から見た talker の声の距離”を更新
    private void _ApplyLocal()
{
    var talker = Networking.GetOwner(gameObject);
    if (talker == null || !talker.IsValid()) return;

    var local = Networking.LocalPlayer;

    if (!gateOn || targetPid < 0 || local == null)
    {   // 通常時
        talker.SetVoiceDistanceNear(normalNear);
        talker.SetVoiceDistanceFar(normalFar);
        talker.SetVoiceGain(normalGain);
        talker.SetVoiceLowpass(false);
        return;
    }

    bool meIsTarget = (local.playerId == targetPid);

    if (meIsTarget)
    {   // ★自分が相手のときだけ“聞こえる囁き”にする
        talker.SetVoiceDistanceNear(whisperNear);
        talker.SetVoiceDistanceFar(whisperFar);
        talker.SetVoiceGain(whisperGain);
        talker.SetVoiceLowpass(false);
    }
    else
    {   // 第三者はミュート
        talker.SetVoiceDistanceNear(0f);
        talker.SetVoiceDistanceFar(0f);
        talker.SetVoiceGain(normalGain); // 任意（何でも可）
        talker.SetVoiceLowpass(false);
    }
}
private void _UpdateDebugLabel()
{
    if (debugLabelTMP == null && debugLabelUGUI == null) return;

    var owner  = Networking.GetOwner(gameObject);
    var local  = Networking.LocalPlayer;
    var target = (targetPid >= 0) ? VRCPlayerApi.GetPlayerById(targetPid) : null;

    string ownerStr  = (owner  != null && owner.IsValid())  ? $"{owner.playerId}:{owner.displayName}"   : "(none)";
    string targetStr = (target != null && target.IsValid()) ? $"{targetPid}:{target.displayName}"
                                                            : (targetPid >= 0 ? targetPid.ToString() : "-");

    // このクライアントで「実際に適用している A(=owner) の Far 値」
    float farForYou =
        (!gateOn || targetPid < 0 || local == null) ? normalFar :
        (local.playerId == targetPid ? whisperFar : 0f);
        


    string youMark  = (local != null && targetPid == local.playerId && gateOn) ? "  ←YOU" : "";
    string gateLine = (assignedIndex >= 0) ? $"Gate: {assignedIndex:00}\n" : "";



        string text =
            gateLine +
            $"Gate '{name}'\n" +
            $"On: {gateOn}\n" +
            $"Owner: {ownerStr}\n" +
            $"TargetPid: {targetStr}{youMark}\n" +
            $"FarForYOU: {farForYou:F2}m\n";

    if (debugLabelTMP  != null) debugLabelTMP.text  = text;
    if (debugLabelUGUI != null) debugLabelUGUI.text = text;
}

}
