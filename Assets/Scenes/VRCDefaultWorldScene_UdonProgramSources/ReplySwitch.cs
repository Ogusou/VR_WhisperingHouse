using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI;

public class ReplySwitch : UdonSharpBehaviour
{
    [Header("Target")]
    public WhisperRelay relay;                  // ここに WhisperRelay をドラッグ

    [Header("UI (optional)")]
    public TextMeshProUGUI label;               // 任意：ON/OFF表示
    public Image indicator;                     // 任意：色でON/OFF表示
    public string onText = "Reply: ON";
    public string offText = "Reply: OFF";
    public Color onColor  = new Color(0.3f, 1f, 0.9f, 1f);
    public Color offColor = new Color(0.35f, 0.35f, 0.35f, 1f);

    private bool _latched;                      // ラッチ状態

    void Start() { _ApplyUI(); }

    public override void Interact()
    {
        _latched = !_latched;

        if (relay != null)
        {
            if (_latched) relay.SendCustomEvent("DebugReplyOn");
            else          relay.SendCustomEvent("DebugReplyOff");
        }

        _ApplyUI();
    }

    private void _ApplyUI()
    {
        if (label != null)     label.text = _latched ? onText : offText;
        if (indicator != null) indicator.color = _latched ? onColor : offColor;
    }

    // （任意）他スクリプトから使う用
    public void SetOn()  { if (!_latched) { _latched = true;  if (relay!=null) relay.SendCustomEvent("DebugReplyOn");  _ApplyUI(); } }
    public void SetOff() { if (_latched)  { _latched = false; if (relay!=null) relay.SendCustomEvent("DebugReplyOff"); _ApplyUI(); } }
    public void Toggle() { Interact(); }
}
