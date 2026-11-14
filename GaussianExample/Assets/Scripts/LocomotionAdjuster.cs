using UnityEngine;
using Oculus.Interaction.Locomotion;

public class LocomotionAdjuster : MonoBehaviour
{
    LocomotionTunneling m_Tunneling;

    void Awake()
    {
        m_Tunneling = GetComponentInChildren<LocomotionTunneling>(true);
        if (m_Tunneling == null)
            return;

        m_Tunneling.RotationStrength = AnimationCurve.Linear(0, 0, 1, 0);
        m_Tunneling.MovementStrength = AnimationCurve.Linear(0, 0, 1, 0);
        m_Tunneling.AccelerationStrength = AnimationCurve.Linear(0, 0, 1, 0);
        m_Tunneling.enabled = true;
    }
}

