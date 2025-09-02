using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class WhisperDebugSwitch : UdonSharpBehaviour
{
    public WhisperManager target;

    public override void Interact()
    {
        if (target == null) return;
        target._DebugToggleWhisper();
    }
}
