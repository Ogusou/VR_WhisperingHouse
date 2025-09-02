
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SoundproofArea : UdonSharpBehaviour
{
    [Header("【壁越しのボイス音量】", order = 0)]
    [Header("エリア外→内 で声が聞こえる距離。0なら完全遮音。", order = 1)]
    [SerializeField, Min(0)] private float voiceDistanceOutsideToInside = 0.9f;
    [Header("エリア内→外 で声が聞こえる距離。0なら完全遮音。")]
    [SerializeField, Min(0)] private float voiceDistanceInsideToOutside = 0.9f;

    [Space(20, order = 0)]
    [Header("【通常時のボイス音量】", order = 1)]
    [Header("エリア内→内 で声が聞こえる距離。VRCデフォルト25を推奨。", order = 2)]
    [SerializeField, Min(0)] private float voiceDistanceInsideToInside = 25.0f;
    [Header("エリア外→外 で声が聞こえる距離。VRCデフォルト25を推奨。")]
    [SerializeField, Min(0)] private float voiceDistanceOutsideToOutside = 25.0f;

    // [Space(20, order = 0)]
    // [Header("【壁越しの Avatar Audio 調整】", order = 1)]
    // [Header("エリア外→内 でAvatar Audioが聞こえる距離。0なら完全遮音。", order = 2)]
    // [SerializeField, Min(0)] private float avatarAudioDistanceOutsideToInside = 0.25f;
    // [Header("エリア内→外 でAvatar Audioが聞こえる距離。0なら完全遮音。")]
    // [SerializeField, Min(0)] private float avatarAudioDistanceInsideToOutside = 0.25f;

    // [Space(20, order = 0)]
    // [Header("【通常時の Avatar Audio 調整】", order = 1)]
    // [Header("エリア内→内 でAvatar Audioが聞こえる距離。VRCデフォルトは40。", order = 2)]
    // [SerializeField, Min(0)] private float avatarAudioDistanceInsideToInside = 40f;
    // [Header("エリア外→外 でAvatar Audioが聞こえる距離。VRCデフォルトは40。")]
    // [SerializeField, Min(0)] private float avatarAudioDistanceOutsideToOutside = 40f;

    [Space(20, order = 0)]
    [Header("【その他 / 高度な設定】", order = 1)]
    [Header("更新頻度。推奨は5。Mobileで大人数", order = 2)]
    [Header("など、極限まで負荷低減する場合のみ上げる。", order = 3)]
    [SerializeField, Range(1, 30)] private int updateEveryNFrame = 5;

    private VRCPlayerApi[] players = new VRCPlayerApi[100];

    void Start()
    {
        RefreshTimerCallback();
    }

    public void RefreshTimerCallback()
    {
        if (Networking.LocalPlayer == null) return; // Prevent errors when the local player leaves
        UpdateAllPlayersVoiceDistance();
        SendCustomEventDelayedFrames(nameof(RefreshTimerCallback), updateEveryNFrame);
    }

    private void UpdateAllPlayersVoiceDistance()
    {
        VRCPlayerApi.GetPlayers(players);
        int playerCount = VRCPlayerApi.GetPlayerCount();
        bool isLocalPlayerInside = IsThisPlayerInside(Networking.LocalPlayer);
        for (int i = 0; i < playerCount; i++)
        {
            if (players[i] == Networking.LocalPlayer) continue;
            bool isRemotePlayerInside = IsThisPlayerInside(players[i]);
            // https://creators.vrchat.com/worlds/udon/players/player-audio
            players[i].SetVoiceDistanceFar(GetVoiceDistance(isLocalPlayerInside, isRemotePlayerInside));
        }
    }

    private float GetVoiceDistance(bool isLocalPlayerInside, bool isRemotePlayerInside)
    {
        if (isLocalPlayerInside) {
            if (isRemotePlayerInside) {
                return voiceDistanceInsideToInside;
            } else {
                return voiceDistanceOutsideToInside;
            }
        } else {
            if (isRemotePlayerInside) {
                return voiceDistanceInsideToOutside;
            } else {
                return voiceDistanceOutsideToOutside;
            }
        }
    }

    private bool IsThisPlayerInside(VRCPlayerApi player)
    {
        return GetComponent<Collider>().bounds.Contains(player.GetPosition());
    }
}
