using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using TMPro;

public class GateDebugHUD : UdonSharpBehaviour
{
    public WhisperGatePool pool;
    public TextMeshProUGUI label;
    public float refreshInterval = 0.25f;
    private float _next;

    void Update()
    {
        if (label == null || pool == null || pool.gates == null) return;
        if (Time.time < _next) return;
        _next = Time.time + refreshInterval;

        var local = Networking.LocalPlayer;
        string s = "Whisper Gates\n";
        for (int i = 0; i < pool.gates.Length; i++)
        {
            var g = pool.gates[i];
            if (g == null) { s += $"{i:00}: (null)\n"; continue; }

            var o = Networking.GetOwner(g.gameObject);
            var t = (g.targetPid >= 0) ? VRCPlayerApi.GetPlayerById(g.targetPid) : null;

            string ownerStr  = (o != null && o.IsValid()) ? $"{o.playerId}:{o.displayName}" : "(none)";
            string targetStr = (t != null && t.IsValid()) ? $"{g.targetPid}:{t.displayName}" : (g.targetPid >= 0 ? g.targetPid.ToString() : "-");
            string you = (local != null && g.targetPid == local.playerId && g.gateOn) ? " ←YOU" : "";
            string safe = g.muteAllWhenNoTarget ? "muteAll=ON" : "muteAll=OFF";

            s += $"{i:00}: On={g.gateOn}  {safe}  Owner={ownerStr}  Target={targetStr}{you}\n";
        }
        label.text = s;
    }
}
