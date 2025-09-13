using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// WhisperGatePool
/// 役割: ローカル話者用の Gate を1つ返す
///
/// 変更点(2025-09):
/// - 空き or 既に自分がOwnerの Gate を優先確保
/// - なければ playerId % N にフォールバック
/// - 取得時に SetOwner で明示的に占有
/// </summary>
public class WhisperGatePool : UdonSharpBehaviour
{
    public WhisperVoiceGate[] gates;

    public WhisperVoiceGate GetGateForLocal()
    {
        var lp = Networking.LocalPlayer;
        if (lp == null || gates == null || gates.Length == 0) return null;

        // 1) 既に自分がOwnerのゲートがあればそれ
        for (int i = 0; i < gates.Length; i++)
        {
            var g = gates[i];
            if (g == null) continue;
            var o = Networking.GetOwner(g.gameObject);
            if (o != null && o.IsValid() && o.isLocal)
            {
                return g;
            }
        }

        // 2) Owner不在（または無効）の空きゲートを確保
        for (int i = 0; i < gates.Length; i++)
        {
            var g = gates[i];
            if (g == null) continue;
            var o = Networking.GetOwner(g.gameObject);
            if (o == null || !o.IsValid())
            {
                Networking.SetOwner(lp, g.gameObject);
                return g;
            }
        }

        // 3) フォールバック: playerId % N
        int idx = Mathf.Abs(lp.playerId) % gates.Length;
        var fallback = gates[idx];
        if (fallback != null)
        {
            Networking.SetOwner(lp, fallback.gameObject);
        }
        return fallback;
    }
}
