using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using TMPro;
using UnityEngine.UI;

public class GateDebugHUD : UdonSharpBehaviour
{
    [Header("Refs")]
    public WhisperGatePool pool;

    [Header("UI (片方でOK)")]
    public TextMeshProUGUI labelTMP;
    public Text            labelUGUI;

    [Header("表示更新")]
    public float refreshInterval = 0.25f;
    public bool  showPlayerIds   = true;

    private float _nextAt;

    void Update()
    {
        if (Time.time < _nextAt) return;
        _nextAt = Time.time + Mathf.Max(0.05f, refreshInterval);

        string text = BuildText();
        if (labelTMP != null) labelTMP.text = text;
        if (labelUGUI != null) labelUGUI.text = text;
    }

    private string BuildText()
    {
        if (pool == null || pool.gates == null || pool.gates.Length == 0)
            return "Whisper Gates\n(no pool / gates)";

        var local = Networking.LocalPlayer;

        // ── セクション1：ゲート一覧（今の Owner）
        string s = "GATE ROSTER (current owner)\n";
        for (int i = 0; i < pool.gates.Length; i++)
        {
            var g = pool.gates[i];
            if (g == null) { s += $"{i:00}: (null)\n"; continue; }

            var owner    = Networking.GetOwner(g.gameObject);
            string ownerStr = OwnerToStr(owner);
            string youMark  = (owner != null && local != null && owner.playerId == local.playerId) ? " (YOU)" : "";
            s += $"{i:00}: {ownerStr}{youMark}\n";
        }

        // ── セクション2：アクティブ（gateOn==true）
        s += "\nACTIVE WHISPERS (talker ⇒ target)\n";
        bool anyActive = false;
        for (int i = 0; i < pool.gates.Length; i++)
        {
            var g = pool.gates[i];
            if (g == null) continue;
            if (g.gateOn)
            {
                anyActive = true;
                var talker = Networking.GetOwner(g.gameObject);
                string talkerStr = OwnerToStr(talker);

                int tpid = g.targetPid;
                var target = (tpid >= 0) ? VRCPlayerApi.GetPlayerById(tpid) : null;
                string targetStr = TargetToStr(tpid, target);

                string you = (local != null && tpid == local.playerId) ? " ⇐ YOU" : "";
                s += $"{i:00}: {talkerStr} ⇒ {targetStr}{you}   [gateOn={g.gateOn} targetPid={tpid}]\n";
            }
        }
        if (!anyActive) s += "(none)\n";

        return s;
    }

    private string OwnerToStr(VRCPlayerApi owner)
    {
        if (owner == null || !owner.IsValid()) return "(unowned)";
        return showPlayerIds ? $"{owner.displayName}#{owner.playerId}" : owner.displayName;
    }

    private string TargetToStr(int pid, VRCPlayerApi p)
    {
        if (pid < 0 || p == null || !p.IsValid())
            return (pid < 0) ? "-" : (showPlayerIds ? $"(pid {pid})" : "(unknown)");
        return showPlayerIds ? $"{p.displayName}#{pid}" : p.displayName;
    }
}
