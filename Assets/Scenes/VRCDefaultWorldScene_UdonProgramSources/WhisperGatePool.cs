// WhisperGatePool.cs（差し替え推奨）
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class WhisperGatePool : UdonSharpBehaviour
{
    public WhisperVoiceGate[] gates;

    // gates[i] を誰に割り当てたか（playerId）。未使用は -1
    [UdonSynced] private int[] ownerPids;

    void Start()
    {
        if (ownerPids == null || ownerPids.Length != gates.Length)
        {
            ownerPids = new int[gates.Length];
            for (int i = 0; i < ownerPids.Length; i++) ownerPids[i] = -1;
        }
        // Master が初期オーナを保持しておく（デフォで Master 所有ですが明示しておくと安全）
        if (Networking.IsMaster) Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (!Networking.IsMaster) return;

        // 空きスロットに入室順で割り当て
        int idx = _FindIndexByPid(-1);
        if (idx >= 0)
        {
            ownerPids[idx] = player.playerId;
            // Gate のオーナーをその人に
            if (gates[idx] != null)
            {
                Networking.SetOwner(player, gates[idx].gameObject);

                // 新オーナーで Gate を初期化（Allで投げて中で Owner チェック）
                gates[idx].SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(WhisperVoiceGate.OwnerStop));
            }
            RequestSerialization();
        }
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (!Networking.IsMaster) return;

        int idx = _FindIndexByPid(player.playerId);
        if (idx >= 0)
        {
            ownerPids[idx] = -1;
            if (gates[idx] != null)
            {
                // Gate を空にしておく（Allで投げて Owner チェック）
                gates[idx].SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(WhisperVoiceGate.OwnerStop));
            }
            RequestSerialization();
        }
    }

    private int _FindIndexByPid(int pid)
    {
        for (int i = 0; i < ownerPids.Length; i++)
            if (ownerPids[i] == pid) return i;
        return -1;
    }

    // ▼ “そのプレイヤーに割り当てられた” Gate を返す（全員が同じ結果になる）
    public WhisperVoiceGate GetGateAssignedTo(int playerId)
    {
        int idx = _FindIndexByPid(playerId);
        return (idx >= 0 && idx < gates.Length) ? gates[idx] : null;
    }

    // ▼ 互換/お手軽
    public WhisperVoiceGate GetGateForLocal()
    {
        var lp = Networking.LocalPlayer;
        return (lp == null) ? null : GetGateAssignedTo(lp.playerId);
    }

    // （互換ヘルパ）
    public WhisperVoiceGate ClaimGateForLocal() => GetGateForLocal();

    // DebugHUD から割り当て表を読みたい時用
    public int[] GetOwnerPids() { return ownerPids; }
}
