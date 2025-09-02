using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

public class WristLedFollow : UdonSharpBehaviour
{
    public bool isRight = true;
    public Vector3 localOffset = new Vector3(0f, 0f, 0.03f); // 手の甲側に少し出す
    VRCPlayerApi _me;
    void Start(){ _me = Networking.LocalPlayer; }
    void LateUpdate(){
        if (_me == null) return;
        var bone = isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand;
        var p = _me.GetBonePosition(bone);
        var r = _me.GetBoneRotation(bone);
        if (p == Vector3.zero) return; // 取得失敗対策
        transform.SetPositionAndRotation(p + r * localOffset, r);
    }
}