using UnityEngine;

public class WeaponRecoil : MonoBehaviour
{
    public PlayerController playerController;

    [Header("Recoil Multipliers")]
    public float hipRecoilMultiplier = 1f;
    public float aimRecoilMultiplier = 0.55f;

    [Header("Camera Recoil")]
    public float cameraPitchKick = 2.2f;
    public float cameraYawRandom = 0.35f;

    [Header("Weapon Visual Recoil")]
    public Vector3 weaponPositionKick = new Vector3(0f, -0.03f, -0.08f);
    public Vector3 weaponRotationKick = new Vector3(-6f, 1.5f, 1.5f);

    [Header("Recovery")]
    public float positionRecoverySpeed = 14f;
    public float rotationRecoverySpeed = 16f;

    private Vector3 currentPositionOffset;
    private Vector3 currentRotationOffset;

    public Vector3 PositionOffset => currentPositionOffset;
    public Vector3 RotationOffset => currentRotationOffset;

    private void Awake()
    {
        if (playerController == null)
        {
            playerController = GetComponent<PlayerController>();
        }
    }

    private void Update()
    {
        currentPositionOffset = Vector3.Lerp(currentPositionOffset, Vector3.zero, Time.deltaTime * positionRecoverySpeed);
        currentRotationOffset = Vector3.Lerp(currentRotationOffset, Vector3.zero, Time.deltaTime * rotationRecoverySpeed);
    }

    public void ApplyRecoil()
    {
        ApplyRecoil(hipRecoilMultiplier);
    }

    public void ApplyRecoil(float recoilMultiplier)
    {
        float finalPitchKick = cameraPitchKick * recoilMultiplier;
        float finalYawRandom = cameraYawRandom * recoilMultiplier;

        float randomYaw = Random.Range(-finalYawRandom, finalYawRandom);

        if (playerController != null)
        {
            playerController.AddCameraRecoil(finalPitchKick, randomYaw);
        }

        currentPositionOffset += weaponPositionKick * recoilMultiplier;

        currentRotationOffset.x += weaponRotationKick.x * recoilMultiplier;
        currentRotationOffset.y += Random.Range(-weaponRotationKick.y, weaponRotationKick.y) * recoilMultiplier;
        currentRotationOffset.z += Random.Range(-weaponRotationKick.z, weaponRotationKick.z) * recoilMultiplier;
    }

    public float GetRecoilMultiplier(bool isAiming)
    {
        return isAiming ? aimRecoilMultiplier : hipRecoilMultiplier;
    }
}