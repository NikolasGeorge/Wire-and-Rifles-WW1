using TMPro;
using UnityEngine;

public class AmmoUI : MonoBehaviour
{
    public BoltActionRifle rifle;
    public TMP_Text ammoText;

    private void Update()
    {
        if (rifle == null || ammoText == null)
        {
            return;
        }

        if (rifle.IsReloading)
        {
            ammoText.text = "Reloading...";
            return;
        }

        if (rifle.IsEmpty)
        {
            ammoText.text = "Empty | " + rifle.ReserveAmmo;
            return;
        }

        ammoText.text = rifle.CurrentAmmo + " / " + rifle.ReserveAmmo;
    }
}