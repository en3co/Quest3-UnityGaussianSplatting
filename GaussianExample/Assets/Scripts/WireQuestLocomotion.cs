using Oculus.Interaction.Locomotion;
using UnityEngine;

// The comprehensive rig ships with an empty player origin. Without it,
// thumbsticks do nothing because FirstPersonLocomotor refuses to start.
[DefaultExecutionOrder(-200)]
public class WireQuestLocomotion : MonoBehaviour
{
    void Awake()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig == null || rig.centerEyeAnchor == null)
            return;

        var locomotors = FindObjectsByType<FirstPersonLocomotor>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var locomotor in locomotors)
        {
            locomotor.InjectPlayerOrigin(rig.transform);
            locomotor.InjectPlayerEyes(rig.centerEyeAnchor);
        }
    }
}
